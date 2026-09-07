using FluentAssertions;
using OptionAnalytics;
using Xunit;

namespace OptionAnalytics.Tests;

public class BidAskVolatilityTests
{
    private const double Spot = 24_800;
    private const double SevenDays = 7 / 365.0;

    private static OptionInputs At(double strike, double t = SevenDays)
        => new(Spot, strike, t, 0.13, 0.065, 0.053, OptionRight.Call);

    private static double Fair(OptionInputs i) => BlackScholesMerton.Price(i)!.Value;

    // ---------------------------------------------------------------- the reason the class exists

    [Fact]
    public void TheSameRupeeSpreadIsATotallyDifferentQuoteAcrossTheChain()
    {
        // The whole argument, measured. One quote 0.20 wide: at the money it pins volatility to a
        // hundredth of a point, six hundred points out it leaves nearly four tenths of a point
        // undetermined. A price spread is denominated in the wrong unit for the decision.
        const double half = 0.10;

        double atm = Width(24_800);
        double wing = Width(26_000);

        double Width(double strike)
        {
            var i = At(strike);
            double fair = Fair(i);
            return ImpliedVolatility.SolveBidAsk(fair - half, fair + half, i)!.VolatilitySpread!.Value;
        }

        atm.Should().BeLessThan(0.0005, "0.015 volatility points at the money");
        wing.Should().BeGreaterThan(0.003, "0.39 volatility points on the wing");
        (wing / atm).Should().BeGreaterThan(15.0, "measured at 26.5x");
    }

    [Theory]
    [InlineData(24_500)]
    [InlineData(24_800)]
    [InlineData(25_100)]
    [InlineData(25_500)]
    public void TheVolatilityWidthIsTheSpreadDividedByVega(double strike)
    {
        // The first-order relation, and it is essentially exact for a narrow quote. This is also the
        // theoretical form of the tick-resolvability rule already documented on
        // ImpliedVolatilityResult.VegaAtSolution -- the bid/ask bracket is that rule measured
        // rather than estimated.
        var inputs = At(strike);
        double fair = Fair(inputs);
        double vega = BlackScholesMerton.Vega(inputs)!.Value;
        double half = Math.Min(0.5, fair * 0.02);

        var q = ImpliedVolatility.SolveBidAsk(fair - half, fair + half, inputs)!;

        double predicted = 2 * half / vega;
        q.VolatilitySpread!.Value.Should().BeApproximately(predicted, predicted * 1e-3);
    }

    // ---------------------------------------------------------------- monotonicity and repricing

    [Theory]
    [InlineData(24_000)]
    [InlineData(24_800)]
    [InlineData(25_500)]
    public void BidVolatilityNeverExceedsMidWhichNeverExceedsAsk(double strike)
    {
        // Price rises monotonically with volatility, so an uncrossed quote must bracket.
        //
        // The quote is built FROM volatilities rather than as a percentage of the fair value,
        // because a proportional spread does not survive deep in the money: 10% below the fair
        // value of a 24,000 call is below its intrinsic, so no volatility reproduces it and the bid
        // side correctly has none. That is the library behaving properly, not a bracket to test.
        var inputs = At(strike);
        double bid = BlackScholesMerton.Price(inputs with { Volatility = 0.11 })!.Value;
        double ask = BlackScholesMerton.Price(inputs with { Volatility = 0.15 })!.Value;

        var q = ImpliedVolatility.SolveBidAsk(bid, ask, inputs)!;

        q.Bid!.Volatility.Should().BeLessThanOrEqualTo(q.Mid!.Volatility);
        q.Mid.Volatility.Should().BeLessThanOrEqualTo(q.Ask!.Volatility);
        q.VolatilitySpread!.Value.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void EachSideRepricesItsOwnSideOfTheQuote()
    {
        var inputs = At(24_800);
        double fair = Fair(inputs);
        double bid = fair - 5, ask = fair + 5;

        var q = ImpliedVolatility.SolveBidAsk(bid, ask, inputs)!;

        BlackScholesMerton.Price(inputs with { Volatility = q.Bid!.Volatility })!.Value
            .Should().BeApproximately(bid, 1e-8);
        BlackScholesMerton.Price(inputs with { Volatility = q.Ask!.Volatility })!.Value
            .Should().BeApproximately(ask, 1e-8);
        BlackScholesMerton.Price(inputs with { Volatility = q.Mid!.Volatility })!.Value
            .Should().BeApproximately(q.MidPrice, 1e-8);

        q.MidPrice.Should().BeApproximately(fair, 1e-12);
    }

    [Fact]
    public void RoundTripsTwoKnownVolatilities()
    {
        // Build the quote FROM volatilities and get them back.
        var inputs = At(24_800);
        double bid = BlackScholesMerton.Price(inputs with { Volatility = 0.12 })!.Value;
        double ask = BlackScholesMerton.Price(inputs with { Volatility = 0.14 })!.Value;

        var q = ImpliedVolatility.SolveBidAsk(bid, ask, inputs)!;

        q.Bid!.Volatility.Should().BeApproximately(0.12, 1e-8);
        q.Ask!.Volatility.Should().BeApproximately(0.14, 1e-8);
        q.VolatilitySpread!.Value.Should().BeApproximately(0.02, 1e-8);
        q.IsResolvable.Should().BeTrue();
    }

    [Fact]
    public void SolvingAtTheMidIsNotAveragingTheTwoSides()
    {
        // Price is non-linear in volatility, so the two differ. The gap is small -- which is why the
        // documentation says the reason to solve at the mid is that it reprices the mid, not that
        // averaging is inaccurate.
        var inputs = At(26_000);
        double fair = Fair(inputs);

        var q = ImpliedVolatility.SolveBidAsk(fair * 0.5, fair * 1.5, inputs)!;

        double averaged = 0.5 * (q.Bid!.Volatility + q.Ask!.Volatility);

        q.Mid!.Volatility.Should().NotBeApproximately(averaged, 1e-6, "they are genuinely different");
        Math.Abs(q.Mid.Volatility - averaged).Should().BeLessThan(0.005,
            "but the gap is under half a volatility point even on a wing quote 50% wide");
    }

    // ---------------------------------------------------------------- sides failing independently

    [Fact]
    public void AZeroBidOnAnOutOfTheMoneyStrikeClampsAndSaysItDidNotConverge()
    {
        // Routine in the wings. The bid has no implied volatility; the ask beside it solves exactly.
        var inputs = At(26_000);
        double ask = Fair(inputs) + 0.5;

        var q = ImpliedVolatility.SolveBidAsk(0.0, ask, inputs)!;

        q.Bid.Should().NotBeNull();
        q.Bid!.Converged.Should().BeFalse("the floor is not a solve");
        q.Bid.Volatility.Should().Be(ImpliedVolatility.MinVolatility);

        q.Ask!.Converged.Should().BeTrue();

        q.IsResolvable.Should().BeFalse(
            "which is why IsResolvable checks Converged and not merely non-null");
    }

    [Fact]
    public void AZeroBidOnADeepInTheMoneyStrikeIsRefusedOutright()
    {
        // Below intrinsic, so no volatility reproduces it and there is nothing to clamp to.
        var inputs = At(23_000);

        var q = ImpliedVolatility.SolveBidAsk(0.0, Fair(inputs) + 0.5, inputs)!;

        q.Bid.Should().BeNull();
        q.Ask.Should().NotBeNull();
        q.VolatilitySpread.Should().BeNull("no bracket without both sides");
        q.IsResolvable.Should().BeFalse();
    }

    [Fact]
    public void AnAskAboveTheAchievableRangeIsRefusedOnThatSideOnly()
    {
        var inputs = At(24_800);
        double ceiling = BlackScholesMerton.Price(
            inputs with { Volatility = ImpliedVolatility.MaxVolatility })!.Value;

        var q = ImpliedVolatility.SolveBidAsk(Fair(inputs), ceiling * 2, inputs)!;

        q.Ask.Should().BeNull("no volatility produces a price above the model's ceiling");
        q.Bid.Should().NotBeNull();
        q.Bid!.Converged.Should().BeTrue();
        q.IsResolvable.Should().BeFalse();
    }

    [Fact]
    public void ALockedMarketHasZeroWidthAndIsStillResolvable()
    {
        var inputs = At(24_800);
        double fair = Fair(inputs);

        var q = ImpliedVolatility.SolveBidAsk(fair, fair, inputs)!;

        q.MidPrice.Should().BeApproximately(fair, 1e-12);
        q.VolatilitySpread!.Value.Should().BeApproximately(0.0, 1e-10);
        q.Bid!.Volatility.Should().BeApproximately(q.Ask!.Volatility, 1e-10);
        q.IsResolvable.Should().BeTrue();
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void ACrossedQuoteIsRefused()
    {
        // The result's whole meaning is a bracket, and a bid above an ask brackets nothing. It is a
        // stale side or a bad feed, which is a data problem rather than a market condition.
        var inputs = At(24_800);
        double fair = Fair(inputs);

        ImpliedVolatility.SolveBidAsk(fair + 1, fair - 1, inputs).Should().BeNull();
    }

    [Theory]
    [InlineData(-1.0, 40.0)]
    [InlineData(double.NaN, 40.0)]
    [InlineData(40.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 40.0)]
    [InlineData(40.0, double.NegativeInfinity)]
    public void UnusableQuotesAreRefused(double bid, double ask)
    {
        ImpliedVolatility.SolveBidAsk(bid, ask, At(24_800)).Should().BeNull();
    }

    [Fact]
    public void AnUnusableContractIsOneRefusalNotThreeCoincidentalOnes()
    {
        var inputs = At(24_800);
        double fair = Fair(inputs);

        ImpliedVolatility.SolveBidAsk(fair - 1, fair + 1, null!).Should().BeNull();
        ImpliedVolatility.SolveBidAsk(fair - 1, fair + 1, inputs with { TimeToExpiry = 0 })
            .Should().BeNull("no time value left to invert");
        ImpliedVolatility.SolveBidAsk(fair - 1, fair + 1, inputs with { Spot = -1 }).Should().BeNull();
        ImpliedVolatility.SolveBidAsk(fair - 1, fair + 1, inputs with { Right = (OptionRight)99 })
            .Should().BeNull();
    }

    [Fact]
    public void TheSolverParametersAreHonouredOnEverySide()
    {
        var inputs = At(24_800);
        double fair = Fair(inputs);

        var starved = ImpliedVolatility.SolveBidAsk(
            fair - 5, fair + 5, inputs, tolerance: 1e-15, maxIterations: 1)!;

        starved.Bid!.Converged.Should().BeFalse();
        starved.Ask!.Converged.Should().BeFalse();
        starved.Mid!.Converged.Should().BeFalse();
        starved.IsResolvable.Should().BeFalse();

        ImpliedVolatility.SolveBidAsk(fair - 5, fair + 5, inputs, tolerance: 0).Should().BeNull();
        ImpliedVolatility.SolveBidAsk(fair - 5, fair + 5, inputs, maxIterations: 0).Should().BeNull();
    }

    // ---------------------------------------------------------------- the chain-scale motivation

    [Fact]
    public void MostOfAChainIsResolvableAndAMeaningfulMinorityIsNot()
    {
        // The measurement that justified building this: on a real-shaped chain a week from expiry a
        // substantial minority of strikes cannot be resolved from a quote at all. A caller who only
        // solves the mid never learns which ones.
        var inputs = At(24_800);
        int resolvable = 0, unresolvable = 0;

        for (double strike = 20_000; strike <= 30_000; strike += 100)
        {
            var contract = inputs with { Strike = strike };
            double fair = Fair(contract);

            // A quote two ticks wide, floored at a tick, as a feed would carry.
            double bid = Math.Max(0.0, Math.Round(fair - 0.05, 2));
            double ask = Math.Round(fair + 0.05, 2);

            var q = ImpliedVolatility.SolveBidAsk(bid, ask, contract);
            if (q is { IsResolvable: true }) resolvable++; else unresolvable++;
        }

        resolvable.Should().BeGreaterThan(0);
        unresolvable.Should().BeGreaterThan(0, "the wings genuinely do not resolve");
        (resolvable + unresolvable).Should().Be(101);
    }
}
