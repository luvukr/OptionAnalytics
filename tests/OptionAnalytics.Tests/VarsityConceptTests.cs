using FluentAssertions;
using Xunit;

namespace OptionAnalytics.Tests;

public class ExpectedMoveTests
{
    [Fact]
    public void ReproducesTheVarsityWorkedExample()
    {
        // Zerodha Varsity, "Volatility Applications": NIFTY at 8462, daily SD 0.89%, daily mean
        // 0.04%, 16 days to expiry -> upper 8818, lower 8214.
        //
        // Note the construction the published numbers actually imply: the drift is ADDED to BOTH
        // bounds, so the range is centred on the drifted price rather than on today's price.
        // Subtracting it from the lower bound gives 8106, which is not the figure Varsity states.
        var range = ExpectedMove.SimpleRange(
            spot: 8462, dailyVolatility: 0.0089, days: 16, sigmas: 1.0, dailyMeanReturn: 0.0004);

        range.Should().NotBeNull();
        range!.Upper.Should().BeApproximately(8818, 1.0);
        range.Lower.Should().BeApproximately(8214, 2.0);
        range.Confidence.Should().BeApproximately(0.6827, 1e-3, "one sigma either side");
    }

    [Fact]
    public void SquareRootOfTimeScaling()
    {
        // 16-day SD = daily SD * sqrt(16) = 4x the daily figure.
        ExpectedMove.Scale(0.0089, fromPeriods: 1, toPeriods: 16)!.Value
            .Should().BeApproximately(0.0356, 1e-9);

        // And annualised -> daily at the conventional 252 base.
        ExpectedMove.OverDays(0.16, days: 1)!.Value
            .Should().BeApproximately(0.16 / Math.Sqrt(252), 1e-12);
    }

    [Theory]
    [InlineData(1.0, 0.6827)]
    [InlineData(2.0, 0.9545)]
    [InlineData(3.0, 0.9973)]
    public void ConfidenceMatchesTheNormalDistribution(double sigmas, double expected)
    {
        ExpectedMove.ConfidenceForSigmas(sigmas)!.Value.Should().BeApproximately(expected, 5e-5);
    }

    [Fact]
    public void TheTwoConstructionsAgreeOverShortHorizons()
    {
        // Over an expiry cycle the arithmetic form Varsity teaches and the lognormal form agree
        // closely, which is why the simpler one is perfectly serviceable there.
        const double spot = 24_800, annualVol = 0.13;
        double dailyVol = ExpectedMove.OverDays(annualVol, 1)!.Value;

        var lognormal = ExpectedMove.Range(spot, annualVol, days: 20)!;
        var simple = ExpectedMove.SimpleRange(spot, dailyVol, days: 20)!;

        (Math.Abs(lognormal.Upper - simple.Upper) / spot).Should().BeLessThan(0.005);
        (Math.Abs(lognormal.Lower - simple.Lower) / spot).Should().BeLessThan(0.005);
    }

    [Fact]
    public void TheSimpleConstructionBreaksDownOverLongHorizons()
    {
        // This is why the lognormal form is the default. At 60% annualised volatility over a year,
        // two sigma drives the arithmetic lower bound through zero -- a negative price. The
        // lognormal bound stays positive by construction, as a price must.
        const double spot = 100, annualVol = 0.60;
        double dailyVol = ExpectedMove.OverDays(annualVol, 1)!.Value;

        var simple = ExpectedMove.SimpleRange(spot, dailyVol, days: 252, sigmas: 2.0)!;
        var lognormal = ExpectedMove.Range(spot, annualVol, days: 252, sigmas: 2.0)!;

        simple.Lower.Should().BeLessThan(0, "the arithmetic form is linear and runs off the bottom");
        lognormal.Lower.Should().BeGreaterThan(0, "a lognormal price cannot go negative");
    }

    [Fact]
    public void TheLognormalRangeCarriesTheItoCorrection()
    {
        // With zero drift the median of a lognormal sits BELOW the spot by sigma^2 * T / 2.
        // Omitting that term is a common error that biases both bounds upward.
        var range = ExpectedMove.Range(spot: 100, annualisedVolatility: 0.4, days: 252, sigmas: 0.0)!;

        range.Lower.Should().Be(range.Upper, "zero sigmas collapses the range to the median");
        range.Upper.Should().BeApproximately(100 * Math.Exp(-0.5 * 0.16), 1e-9);
        range.Upper.Should().BeLessThan(100);
    }

    [Fact]
    public void AWiderRangeIsAlwaysMoreConfident()
    {
        double previousWidth = 0, previousConfidence = 0;
        for (double s = 0.5; s <= 4.0; s += 0.25)
        {
            var r = ExpectedMove.Range(100, 0.2, 30, s)!;
            (r.Upper - r.Lower).Should().BeGreaterThan(previousWidth);
            r.Confidence.Should().BeGreaterThan(previousConfidence);
            previousWidth = r.Upper - r.Lower;
            previousConfidence = r.Confidence;
        }
    }

    [Theory]
    [InlineData(0, 0.2, 30)]
    [InlineData(100, -0.2, 30)]
    [InlineData(100, 0.2, 0)]
    [InlineData(double.NaN, 0.2, 30)]
    public void InvalidInputsReturnNull(double spot, double vol, double days)
    {
        ExpectedMove.Range(spot, vol, days).Should().BeNull();
    }
}

public class OptionProbabilityTests
{
    private static OptionInputs Contract(double spot = 100, double strike = 100,
        double t = 0.25, double vol = 0.25, OptionRight right = OptionRight.Call)
        => OptionModel.BlackScholes(spot, strike, t, vol, 0.05, right);

    [Fact]
    public void InTheMoneyProbabilityIsNd2_NotDelta()
    {
        // The distinction this class exists to make. Delta is N(d1); the probability of expiring
        // in the money is N(d2), and d2 = d1 - sigma*sqrt(T). They are close, not equal.
        var x = Contract();

        double itm = OptionProbability.InTheMoneyAtExpiry(x)!.Value;
        double delta = BlackScholesMerton.Delta(x)!.Value;

        itm.Should().BeLessThan(delta, "N(d2) < N(d1) for a call, always");
        itm.Should().BeApproximately(delta, 0.06, "but close enough that the rule of thumb survives");
    }

    [Fact]
    public void TheDeltaApproximationDegradesWithTimeAndVolatility()
    {
        // The rule of thumb is safest exactly where it is usually applied -- short-dated, near the
        // money -- and worst where a seller is most tempted to lean on it.
        double shortDated = OptionProbability.DeltaVersusProbabilityGap(Contract(t: 7 / 365.0, vol: 0.15))!.Value;
        double longDated = OptionProbability.DeltaVersusProbabilityGap(Contract(t: 2.0, vol: 0.15))!.Value;
        double highVol = OptionProbability.DeltaVersusProbabilityGap(Contract(t: 2.0, vol: 0.60))!.Value;

        shortDated.Should().BeLessThan(0.02);
        longDated.Should().BeGreaterThan(shortDated);
        highVol.Should().BeGreaterThan(longDated);
    }

    [Fact]
    public void CallAndPutProbabilitiesAreComplementary()
    {
        foreach (double strike in new[] { 80.0, 100.0, 120.0 })
        {
            var call = Contract(strike: strike);
            double pCall = OptionProbability.InTheMoneyAtExpiry(call)!.Value;
            double pPut = OptionProbability.InTheMoneyAtExpiry(call with { Right = OptionRight.Put })!.Value;

            (pCall + pPut).Should().BeApproximately(1.0, 1e-12);
        }
    }

    [Fact]
    public void ProbabilityAboveTheStrikeMatchesTheCallsItmProbability()
    {
        var x = Contract(strike: 110);

        OptionProbability.ProbabilityAbove(x, 110)!.Value
            .Should().BeApproximately(OptionProbability.InTheMoneyAtExpiry(x)!.Value, 1e-12);
    }

    [Fact]
    public void BreakevenProbabilityIsLowerThanItmProbability()
    {
        // The honest answer to "how often does this trade make money" uses the breakeven, not the
        // strike -- a long call that expires one point in the money still loses its premium.
        var x = Contract(strike: 100);
        double premium = BlackScholesMerton.Price(x)!.Value;

        double atStrike = OptionProbability.InTheMoneyAtExpiry(x)!.Value;
        double atBreakeven = OptionProbability.ProbabilityAbove(x, 100 + premium)!.Value;

        atBreakeven.Should().BeLessThan(atStrike);
    }

    [Fact]
    public void TouchingIsRoughlyTwiceAsLikelyAsFinishingBeyond()
    {
        // The reason a stop set from an expiry distribution is hit far more often than it looks.
        var x = Contract(t: 0.25, vol: 0.25);
        const double barrier = 115.0;

        double finishes = OptionProbability.ProbabilityAbove(x, barrier)!.Value;
        double touches = OptionProbability.ProbabilityOfTouching(x, barrier)!.Value;

        touches.Should().BeGreaterThan(finishes * 1.5);
        touches.Should().BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void TouchingProbabilityIsSymmetricForADriftlessProcess()
    {
        // Careful: zero CARRY makes the PRICE a martingale, but the LOG price still drifts down
        // by sigma^2 / 2 -- so a Black-76 contract is NOT log-symmetric, and a lower barrier is
        // genuinely likelier to be touched. Log-symmetry needs a carry of exactly sigma^2 / 2.
        const double sigma = 0.3;
        var x = new OptionInputs(100, 100, 0.5, sigma, 0.0, 0.5 * sigma * sigma, OptionRight.Call);
        double up = OptionProbability.ProbabilityOfTouching(x, 100 * Math.Exp(0.2))!.Value;
        double down = OptionProbability.ProbabilityOfTouching(x, 100 * Math.Exp(-0.2))!.Value;

        up.Should().BeApproximately(down, 1e-9);
    }

    [Fact]
    public void ABarrierAtSpotIsAlreadyTouched()
    {
        OptionProbability.ProbabilityOfTouching(Contract(), 100)!.Value.Should().Be(1.0);
    }

    [Fact]
    public void AtExpiryProbabilitiesCollapseToCertainty()
    {
        var itm = Contract(spot: 120, strike: 100, t: 0);
        var otm = Contract(spot: 80, strike: 100, t: 0);

        OptionProbability.InTheMoneyAtExpiry(itm)!.Value.Should().Be(1.0);
        OptionProbability.InTheMoneyAtExpiry(otm)!.Value.Should().Be(0.0);
    }
}

public class OptionStrategyTests
{
    // A bull call spread: long the 100 call at 6, short the 110 call at 2. Net debit 4.
    private static readonly StrategyLeg[] BullCallSpread =
    {
        new(OptionRight.Call, 100, +1, 6.0),
        new(OptionRight.Call, 110, -1, 2.0),
    };

    // A short strangle: sell the 90 put at 3 and the 110 call at 3. Net credit 6.
    private static readonly StrategyLeg[] ShortStrangle =
    {
        new(OptionRight.Put, 90, -1, 3.0),
        new(OptionRight.Call, 110, -1, 3.0),
    };

    [Theory]
    [InlineData(80, -4)]      // both expire worthless: lose the debit
    [InlineData(100, -4)]     // at the long strike: still just the debit
    [InlineData(104, 0)]      // breakeven
    [InlineData(110, 6)]      // maximum: spread width 10 less the 4 debit
    [InlineData(200, 6)]      // capped above the short strike
    public void BullCallSpreadPayoffMatchesHandArithmetic(double price, double expected)
    {
        OptionStrategy.PayoffAtExpiry(BullCallSpread, price)!.Value.Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void BullCallSpreadProfileIsBoundedBothWays()
    {
        var profile = OptionStrategy.Profile(BullCallSpread)!;

        profile.NetCashAtEntry.Should().BeApproximately(-4.0, 1e-12, "a net debit");
        profile.MaxProfit!.Value.Should().BeApproximately(6.0, 1e-12);
        profile.MaxLoss!.Value.Should().BeApproximately(-4.0, 1e-12);
        profile.Breakevens.Should().HaveCount(1);
        profile.Breakevens[0].Should().BeApproximately(104.0, 1e-9);
    }

    [Fact]
    public void ShortStrangleHasTwoBreakevensAndUnboundedLoss()
    {
        var profile = OptionStrategy.Profile(ShortStrangle)!;

        profile.NetCashAtEntry.Should().BeApproximately(6.0, 1e-12, "a net credit");
        profile.MaxProfit!.Value.Should().BeApproximately(6.0, 1e-12, "keep the credit between the strikes");
        profile.MaxLoss.Should().BeNull("a short call is unbounded above");
        profile.Breakevens.Should().HaveCount(2);
        profile.Breakevens[0].Should().BeApproximately(84.0, 1e-9);
        profile.Breakevens[1].Should().BeApproximately(116.0, 1e-9);
    }

    [Fact]
    public void ALongStraddleHasUnboundedProfitAndBoundedLoss()
    {
        var straddle = new StrategyLeg[]
        {
            new(OptionRight.Call, 100, +1, 5.0),
            new(OptionRight.Put, 100, +1, 5.0),
        };

        var profile = OptionStrategy.Profile(straddle)!;

        profile.MaxProfit.Should().BeNull("a long call is unbounded above");
        profile.MaxLoss!.Value.Should().BeApproximately(-10.0, 1e-12);
        profile.Breakevens.Should().HaveCount(2);
        profile.Breakevens[0].Should().BeApproximately(90.0, 1e-9);
        profile.Breakevens[1].Should().BeApproximately(110.0, 1e-9);
    }

    [Fact]
    public void AnIronCondorIsBoundedOnBothSides()
    {
        var condor = new StrategyLeg[]
        {
            new(OptionRight.Put, 85, +1, 1.0),
            new(OptionRight.Put, 95, -1, 3.0),
            new(OptionRight.Call, 105, -1, 3.0),
            new(OptionRight.Call, 115, +1, 1.0),
        };

        var profile = OptionStrategy.Profile(condor)!;

        profile.NetCashAtEntry.Should().BeApproximately(4.0, 1e-12);
        profile.MaxProfit!.Value.Should().BeApproximately(4.0, 1e-12);
        profile.MaxLoss!.Value.Should().BeApproximately(-6.0, 1e-12, "10 wide less the 4 credit");
        profile.Breakevens.Should().HaveCount(2);
    }

    [Fact]
    public void PayoffIsPiecewiseLinearWithKinksOnlyAtStrikes()
    {
        // The property that makes Profile exact rather than sampled: between strikes the payoff is
        // a straight line, so a midpoint must equal the average of its endpoints.
        foreach (var (a, b) in new[] { (80.0, 90.0), (91.0, 99.0), (111.0, 130.0) })
        {
            double mid = OptionStrategy.PayoffAtExpiry(ShortStrangle, (a + b) / 2)!.Value;
            double average = (OptionStrategy.PayoffAtExpiry(ShortStrangle, a)!.Value
                              + OptionStrategy.PayoffAtExpiry(ShortStrangle, b)!.Value) / 2;

            mid.Should().BeApproximately(average, 1e-9);
        }
    }

    [Fact]
    public void NetGreeksSumTheLegsWithTheirSigns()
    {
        var market = OptionModel.BlackScholes(100, 100, 0.25, 0.25, 0.05, OptionRight.Call);
        var net = OptionStrategy.NetGreeks(ShortStrangle, market)!;

        var shortPut = BlackScholesMerton.ComputeGreeks(market with { Strike = 90, Right = OptionRight.Put })!;
        var shortCall = BlackScholesMerton.ComputeGreeks(market with { Strike = 110, Right = OptionRight.Call })!;

        net.Delta.Should().BeApproximately(-shortPut.Delta - shortCall.Delta, 1e-12);
        net.Gamma.Should().BeApproximately(-shortPut.Gamma - shortCall.Gamma, 1e-12);
        net.Gamma.Should().BeLessThan(0, "a short options position is short gamma");
        net.Vega.Should().BeLessThan(0, "and short vega");
        net.Theta.Should().BeGreaterThan(0, "and collects time decay");
    }

    [Fact]
    public void ASyntheticLongReplicatesTheUnderlying()
    {
        // Long call + short put at the same strike is a forward. Its payoff slope is exactly 1
        // everywhere, and it has a single breakeven.
        var synthetic = new StrategyLeg[]
        {
            new(OptionRight.Call, 100, +1, 6.0),
            new(OptionRight.Put, 100, -1, 5.0),
        };

        double at90 = OptionStrategy.PayoffAtExpiry(synthetic, 90)!.Value;
        double at110 = OptionStrategy.PayoffAtExpiry(synthetic, 110)!.Value;

        (at110 - at90).Should().BeApproximately(20.0, 1e-12, "slope of exactly 1 over 20 points");

        var profile = OptionStrategy.Profile(synthetic)!;
        profile.MaxProfit.Should().BeNull("unbounded above, like the underlying");
        profile.MaxLoss!.Value.Should().BeApproximately(-101.0, 1e-12,
            "and bounded below, ALSO like the underlying -- a price cannot go below zero");
        profile.Breakevens.Should().ContainSingle();
        profile.Breakevens[0].Should().BeApproximately(101.0, 1e-9);
    }

    [Fact]
    public void InvalidLegsAreRefused()
    {
        OptionStrategy.Profile(Array.Empty<StrategyLeg>()).Should().BeNull();
        OptionStrategy.Profile(new StrategyLeg[] { new(OptionRight.Call, -100, 1, 5) }).Should().BeNull();
        OptionStrategy.Profile(new StrategyLeg[] { new(OptionRight.Call, 100, 0, 5) })
            .Should().BeNull("a zero-quantity leg is not a position");
        OptionStrategy.PayoffAtExpiry(BullCallSpread, -1).Should().BeNull();
    }
}
