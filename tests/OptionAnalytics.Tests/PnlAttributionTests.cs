using FluentAssertions;
using OptionAnalytics;
using Xunit;

namespace OptionAnalytics.Tests;

public class PnlAttributionTests
{
    private const double Day = 1 / 365.0;

    private static OptionInputs Start(double strike = 24_800, OptionRight right = OptionRight.Call)
        => new(24_800, strike, 30 / 365.0, 0.13, 0.065, 0.053, right);

    // ---------------------------------------------------------------- wiring: one variable at a time
    //
    // The real risk in this class is not the mathematics, it is pairing the wrong greek with the
    // wrong move -- vega multiplied by the spot change, charm by the volatility change. Moving one
    // variable at a time and asserting exactly which terms fire catches every such transposition.

    [Fact]
    public void MovingOnlySpotFiresOnlyTheSpotTerms()
    {
        var start = Start();
        var a = PnlAttribution.Explain(start, start with { Spot = 24_900 })!;

        a.DeltaPnl.Should().NotBe(0.0);
        a.GammaPnl.Should().BeGreaterThan(0.0, "gamma is positive for a long option");

        a.ThetaPnl.Should().Be(0.0);
        a.VegaPnl.Should().Be(0.0);
        a.VannaPnl.Should().Be(0.0);
        a.VolgaPnl.Should().Be(0.0);
        a.CharmPnl.Should().Be(0.0);
        a.RatePnl.Should().Be(0.0);
        a.CarryPnl.Should().Be(0.0);

        // And the terms are the greeks times the move, exactly.
        var g = BlackScholesMerton.ComputeGreeks(start)!;
        a.DeltaPnl.Should().BeApproximately(g.Delta * 100, 1e-12);
        a.GammaPnl.Should().BeApproximately(0.5 * g.Gamma * 100 * 100, 1e-12);
    }

    [Fact]
    public void MovingOnlyVolatilityFiresOnlyTheVolatilityTerms()
    {
        var start = Start();
        var a = PnlAttribution.Explain(start, start with { Volatility = 0.15 })!;

        a.VegaPnl.Should().BeGreaterThan(0.0);
        a.VolgaPnl.Should().NotBe(0.0);

        a.DeltaPnl.Should().Be(0.0);
        a.GammaPnl.Should().Be(0.0);
        a.ThetaPnl.Should().Be(0.0);
        a.VannaPnl.Should().Be(0.0, "vanna needs a spot move as well");
        a.CharmPnl.Should().Be(0.0);
        a.RatePnl.Should().Be(0.0);
        a.CarryPnl.Should().Be(0.0);

        var g = BlackScholesMerton.ComputeGreeks(start)!;
        a.VegaPnl.Should().BeApproximately(g.Vega * 0.02, 1e-12);
        a.VolgaPnl.Should().BeApproximately(0.5 * g.Volga * 0.02 * 0.02, 1e-12);
    }

    [Fact]
    public void MovingOnlyTimeFiresOnlyTheta()
    {
        var start = Start();
        var a = PnlAttribution.Explain(start, start with { TimeToExpiry = start.TimeToExpiry - Day })!;

        a.ThetaPnl.Should().BeLessThan(0.0, "a long option bleeds");

        a.DeltaPnl.Should().Be(0.0);
        a.GammaPnl.Should().Be(0.0);
        a.VegaPnl.Should().Be(0.0);
        a.VannaPnl.Should().Be(0.0);
        a.VolgaPnl.Should().Be(0.0);
        a.CharmPnl.Should().Be(0.0, "charm needs a spot move as well");
        a.RatePnl.Should().Be(0.0);
        a.CarryPnl.Should().Be(0.0);
    }

    [Fact]
    public void ThetaIsPerYearSoOneDayIsAThreeHundredAndSixtyFifthOfIt()
    {
        // The units trap this class exists to make impossible: a per-DAY theta in a per-year
        // expansion would be wrong by 365. Greeks are computed internally in raw units precisely so
        // a caller cannot pass Scaled() values by mistake.
        var start = Start();
        var g = BlackScholesMerton.ComputeGreeks(start)!;

        var a = PnlAttribution.Explain(start, start with { TimeToExpiry = start.TimeToExpiry - Day })!;

        a.ThetaPnl.Should().BeApproximately(g.Theta / 365.0, 1e-12);

        // The scaled convention is a different number, and it is NOT what the expansion uses.
        var scaled = g.Scaled();
        scaled.Theta.Should().BeApproximately(g.Theta / 365.0, 1e-12);
        a.ThetaPnl.Should().NotBeApproximately(scaled.Theta / 365.0, 1e-9);
    }

    [Fact]
    public void MovingOnlyTheRateFiresOnlyTheRateTerm()
    {
        var start = Start();
        var a = PnlAttribution.Explain(start, start with { RiskFreeRate = 0.0675 })!;

        a.RatePnl.Should().NotBe(0.0);
        a.CarryPnl.Should().Be(0.0, "the carry did not move, so it contributed nothing");
        a.DeltaPnl.Should().Be(0.0);
        a.VegaPnl.Should().Be(0.0);
        a.ThetaPnl.Should().Be(0.0);

        // dV/dr with the carry fixed is exactly -T * V.
        var g = BlackScholesMerton.ComputeGreeks(start)!;
        a.RatePnl.Should().BeApproximately(-start.TimeToExpiry * g.Price * 0.0025, 1e-12);
    }

    [Fact]
    public void MovingOnlyTheCarryFiresOnlyTheCarryTerm()
    {
        var start = Start();
        var a = PnlAttribution.Explain(start, start with { CostOfCarry = 0.056 })!;

        a.CarryPnl.Should().NotBe(0.0);
        a.RatePnl.Should().Be(0.0);
        a.DeltaPnl.Should().Be(0.0);
    }

    // ---------------------------------------------------------------- the dV/db identity

    [Theory]
    [InlineData(23_000, OptionRight.Call, 7)]
    [InlineData(24_800, OptionRight.Call, 7)]
    [InlineData(26_500, OptionRight.Call, 90)]
    [InlineData(24_800, OptionRight.Put, 7)]
    [InlineData(23_000, OptionRight.Put, 90)]
    public void CarrySensitivityIsTheDifferenceOfTheTwoRhosAndEqualsTimeSpotDelta(
        double strike, OptionRight right, int days)
    {
        // The class needs dV/db, which the library does not expose directly. The two rhos determine
        // it: RhoCarryTracksRate is dV/dr WITH the carry following (dV/dr + dV/db) and RhoCarryFixed
        // is dV/dr alone, so their difference is dV/db -- which reduces to T * S * Delta. Checked
        // here against that closed form AND against a finite-difference bump of the price, so the
        // identity is not merely asserted.
        var inputs = Start(strike, right) with { TimeToExpiry = days / 365.0 };
        var g = BlackScholesMerton.ComputeGreeks(inputs)!;

        double fromRhos = g.RhoCarryTracksRate - g.RhoCarryFixed;
        double closedForm = inputs.TimeToExpiry * inputs.Spot * g.Delta;

        const double h = 1e-6;
        double finiteDifference =
            (BlackScholesMerton.Price(inputs with { CostOfCarry = inputs.CostOfCarry + h })!.Value
             - BlackScholesMerton.Price(inputs with { CostOfCarry = inputs.CostOfCarry - h })!.Value)
            / (2 * h);

        fromRhos.Should().BeApproximately(closedForm, 1e-9);
        fromRhos.Should().BeApproximately(finiteDifference, Math.Abs(fromRhos) * 1e-6 + 1e-6);

        // And that is the sensitivity the carry term actually uses.
        var a = PnlAttribution.Explain(inputs, inputs with { CostOfCarry = inputs.CostOfCarry + 0.001 })!;
        a.CarryPnl.Should().BeApproximately(fromRhos * 0.001, 1e-9);
    }

    // ---------------------------------------------------------------- self-consistency

    [Fact]
    public void ExplainedIsTheSumOfTermsAndResidualClosesTheGap()
    {
        var start = Start();
        var end = start with
        {
            Spot = 25_050, Volatility = 0.147, TimeToExpiry = start.TimeToExpiry - Day,
            RiskFreeRate = 0.0675, CostOfCarry = 0.0560,
        };

        var a = PnlAttribution.Explain(start, end)!;

        double summed = a.DeltaPnl + a.GammaPnl + a.ThetaPnl + a.VegaPnl + a.VannaPnl
                        + a.VolgaPnl + a.CharmPnl + a.RatePnl + a.CarryPnl;

        a.Explained.Should().BeApproximately(summed, 1e-12);
        (a.Explained + a.Residual).Should().BeApproximately(a.Actual, 1e-12);

        // Actual comes from repricing, not from the terms, so it is exact.
        double repriced = BlackScholesMerton.Price(end)!.Value - BlackScholesMerton.Price(start)!.Value;
        a.Actual.Should().BeApproximately(repriced, 1e-12);
    }

    [Fact]
    public void EveryTermIsPopulatedWhenEveryVariableMoves()
    {
        var start = Start();
        var end = start with
        {
            Spot = 25_050, Volatility = 0.147, TimeToExpiry = start.TimeToExpiry - Day,
            RiskFreeRate = 0.0675, CostOfCarry = 0.0560,
        };

        var a = PnlAttribution.Explain(start, end)!;

        foreach (var term in new[] { a.DeltaPnl, a.GammaPnl, a.ThetaPnl, a.VegaPnl,
                                     a.VannaPnl, a.VolgaPnl, a.CharmPnl, a.RatePnl, a.CarryPnl })
            term.Should().NotBe(0.0);

        a.ResidualShare!.Value.Should().BeLessThan(0.02,
            "an ordinary day should be explained to within a couple of percent");
        a.LargestTerm.Name.Should().Be(nameof(PnlAttributionResult.DeltaPnl));
    }

    // ---------------------------------------------------------------- residual behaviour

    [Fact]
    public void ResidualFallsFasterThanTheMoveShrinks()
    {
        // With time held still, the residual is third order and higher in the spot move, so halving
        // the move should cut it by roughly eight times or better.
        var start = Start();

        double Residual(double fraction) => Math.Abs(
            PnlAttribution.Explain(start, start with { Spot = start.Spot * (1 + fraction) })!.Residual);

        double coarse = Residual(0.04);
        double fine = Residual(0.02);
        double finer = Residual(0.01);

        (coarse / fine).Should().BeGreaterThan(6.0);
        (fine / finer).Should().BeGreaterThan(6.0);
    }

    [Fact]
    public void ResidualHasAFloorFromTheOmittedTimeCurvature()
    {
        // The documented limitation, pinned. The expansion is first order in time, so the second
        // derivative in time is missing and leaves a residual that does NOT vanish as the spot move
        // goes to zero. Anyone reading a small residual share as proof of a clean attribution on a
        // very quiet day is reading it wrong.
        var start = Start();
        var timeOnly = start with { TimeToExpiry = start.TimeToExpiry - Day };

        double floor = Math.Abs(PnlAttribution.Explain(start, timeOnly)!.Residual);

        floor.Should().BeGreaterThan(0.0, "the time-curvature term is genuinely missing");
        floor.Should().BeLessThan(0.2, "but it is small for an ordinary daily step");

        // Shrinking the spot move does not shrink it away.
        var tinyMove = timeOnly with { Spot = start.Spot * 1.0001 };
        Math.Abs(PnlAttribution.Explain(start, tinyMove)!.Residual)
            .Should().BeApproximately(floor, floor * 0.5);
    }

    [Fact]
    public void ALargeGapIsPoorlyExplainedAndSaysSo()
    {
        // Measured: a 10% move leaves about a quarter of the change unexplained, because gamma
        // itself moves over a gap that size. The number is the warning.
        var start = Start();
        var gapped = start with { Spot = start.Spot * 1.10, TimeToExpiry = start.TimeToExpiry - Day };

        var a = PnlAttribution.Explain(start, gapped)!;

        a.ResidualShare!.Value.Should().BeGreaterThan(0.15,
            "a second-order expansion cannot describe a 10% gap");
    }

    [Fact]
    public void ResidualShareIsNullWhenNothingHappened()
    {
        var start = Start();

        var a = PnlAttribution.Explain(start, start)!;

        a.Actual.Should().Be(0.0);
        a.Explained.Should().Be(0.0);
        a.ResidualShare.Should().BeNull("a ratio against zero is meaningless, not large");
    }

    // ---------------------------------------------------------------- positions

    [Fact]
    public void APositionIsTheSignedSumOfItsLegs()
    {
        var start = Start();
        var end = start with { Spot = 25_000, Volatility = 0.14, TimeToExpiry = start.TimeToExpiry - Day };

        var legs = new List<StrategyLeg>
        {
            new(OptionRight.Call, 25_300, -2, 150),
            new(OptionRight.Put, 24_300, -1, 140),
            new(OptionRight.Call, 25_800, +1, 60),
        };

        var position = PnlAttribution.ExplainPosition(legs, start, end)!;

        double expectedActual = 0, expectedDelta = 0, expectedTheta = 0;
        foreach (var leg in legs)
        {
            var one = PnlAttribution.Explain(
                start with { Strike = leg.Strike, Right = leg.Right },
                end with { Strike = leg.Strike, Right = leg.Right })!;

            expectedActual += leg.Quantity * one.Actual;
            expectedDelta += leg.Quantity * one.DeltaPnl;
            expectedTheta += leg.Quantity * one.ThetaPnl;
        }

        position.Actual.Should().BeApproximately(expectedActual, 1e-10);
        position.DeltaPnl.Should().BeApproximately(expectedDelta, 1e-10);
        position.ThetaPnl.Should().BeApproximately(expectedTheta, 1e-10);
        (position.Explained + position.Residual).Should().BeApproximately(position.Actual, 1e-10);
    }

    [Fact]
    public void AShortPositionEarnsThetaAndPaysGamma()
    {
        var start = Start();
        var strangle = new List<StrategyLeg>
        {
            new(OptionRight.Call, 25_300, -1, 150),
            new(OptionRight.Put, 24_300, -1, 140),
        };

        var flat = PnlAttribution.ExplainPosition(
            strangle, start, start with { TimeToExpiry = start.TimeToExpiry - Day })!;

        flat.ThetaPnl.Should().BeGreaterThan(0.0, "a short position collects decay");
        flat.GammaPnl.Should().Be(0.0, "nothing moved");
        flat.LargestTerm.Name.Should().Be(nameof(PnlAttributionResult.ThetaPnl));
    }

    [Fact]
    public void TheThetaSellerWhoseProfitAndLossIsReallyShortGamma()
    {
        // The headline diagnostic. A short strangle described as harvesting time decay: on a day the
        // index moves 1.2%, the theta arrives exactly as advertised and is swamped. The narrative is
        // true and irrelevant, and this is what makes that mechanically visible rather than a
        // matter of interpretation.
        var start = Start();
        var strangle = new List<StrategyLeg>
        {
            new(OptionRight.Call, 25_300, -1, 150),
            new(OptionRight.Put, 24_300, -1, 140),
        };

        var moved = start with
        {
            Spot = start.Spot * 1.012,
            TimeToExpiry = start.TimeToExpiry - Day,
        };

        var a = PnlAttribution.ExplainPosition(strangle, start, moved)!;

        a.Actual.Should().BeLessThan(0.0, "a losing day");
        a.ThetaPnl.Should().BeGreaterThan(0.0, "while still collecting every rupee of decay");

        a.GammaPnl.Should().BeLessThan(0.0);
        a.DeltaPnl.Should().BeLessThan(0.0);
        Math.Abs(a.GammaPnl).Should().BeGreaterThan(a.ThetaPnl,
            "the term that decided the day is not the one the strategy is named after");
        a.LargestTerm.Name.Should().Be(nameof(PnlAttributionResult.GammaPnl));
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void TimeRunningBackwardsIsRefused()
    {
        // Almost always swapped arguments, which would otherwise return a plausible set of terms
        // with every sign inverted.
        var start = Start();
        var earlier = start with { TimeToExpiry = start.TimeToExpiry + Day };

        PnlAttribution.Explain(start, earlier).Should().BeNull();

        // Zero elapsed is fine: a pure market move at one instant.
        PnlAttribution.Explain(start, start with { Spot = 24_900 }).Should().NotBeNull();
    }

    [Fact]
    public void ADifferentContractIsRefused()
    {
        var start = Start();

        PnlAttribution.Explain(start, start with { Strike = 25_000 }).Should().BeNull();
        PnlAttribution.Explain(start, start with { Right = OptionRight.Put }).Should().BeNull();
    }

    [Fact]
    public void InvalidStatesAreRefused()
    {
        var start = Start();

        PnlAttribution.Explain(null!, start).Should().BeNull();
        PnlAttribution.Explain(start, null!).Should().BeNull();
        PnlAttribution.Explain(start with { Spot = -1 }, start).Should().BeNull();
        PnlAttribution.Explain(start, start with { Volatility = double.NaN }).Should().BeNull();
        PnlAttribution.Explain(start, start with { Right = (OptionRight)99 }).Should().BeNull();
    }

    [Fact]
    public void AnUnusablePositionIsRefused()
    {
        var start = Start();
        var end = start with { Spot = 25_000 };
        var good = new StrategyLeg(OptionRight.Call, 25_000, -1, 100);

        PnlAttribution.ExplainPosition(null!, start, end).Should().BeNull();
        PnlAttribution.ExplainPosition(Array.Empty<StrategyLeg>(), start, end).Should().BeNull();
        PnlAttribution.ExplainPosition(new[] { good, null! }, start, end).Should().BeNull();
        PnlAttribution.ExplainPosition(
            new[] { good, new StrategyLeg(OptionRight.Call, -5, 1, 10) }, start, end).Should().BeNull();
        PnlAttribution.ExplainPosition(new[] { good }, start, start with { TimeToExpiry = start.TimeToExpiry + Day })
            .Should().BeNull();
    }
}
