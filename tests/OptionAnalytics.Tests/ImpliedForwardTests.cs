using FluentAssertions;
using OptionAnalytics;
using Xunit;

namespace OptionAnalytics.Tests;

public class ImpliedForwardTests
{
    private const double Spot = 24_800;
    private const double T = 30 / 365.0;

    /// <summary>
    /// Prices a call/put pair with the library at a given carry, so extraction can be checked
    /// against a known answer rather than against another extraction.
    /// </summary>
    private static CallPutQuote Quote(double strike, double carry, double rate, double volatility)
    {
        var call = new OptionInputs(Spot, strike, T, volatility, rate, carry, OptionRight.Call);
        return new CallPutQuote(
            strike,
            BlackScholesMerton.Price(call)!.Value,
            BlackScholesMerton.Price(call with { Right = OptionRight.Put })!.Value);
    }

    // ---------------------------------------------------------------- round trips

    [Theory]
    [InlineData(0.065, 0.065)]   // Black-Scholes: carry is the rate
    [InlineData(0.053, 0.065)]   // Merton: 1.2% dividend yield
    [InlineData(0.0, 0.065)]     // Black-76: option on a future
    [InlineData(0.055, 0.065)]   // Garman-Kohlhagen: 1% foreign rate
    [InlineData(-0.02, 0.065)]   // dividend yield above the rate
    [InlineData(-0.005, -0.005)] // negative policy rate
    [InlineData(0.10, 0.02)]     // carry above the rate
    public void OnePairRecoversTheCarryItWasPricedWith(double carry, double rate)
    {
        var quote = Quote(24_800, carry, rate, 0.13);

        var result = ImpliedForward.FromCallPut(Spot, quote, T, rate)!;

        result.CostOfCarry.Should().BeApproximately(carry, 1e-10);
        result.Forward.Should().BeApproximately(Spot * Math.Exp(carry * T), 1e-6);
        result.RateWasDerived.Should().BeFalse();
        result.StrikesUsed.Should().Be(1);
    }

    [Theory]
    [InlineData(0.065, 0.065)]
    [InlineData(0.053, 0.065)]
    [InlineData(0.0, 0.065)]
    [InlineData(-0.02, 0.065)]
    [InlineData(-0.005, -0.005)]
    public void AChainRecoversBothTheCarryAndTheRateWithNoRateSupplied(double carry, double rate)
    {
        var chain = new[] { 24_600, 24_700, 24_800, 24_900, 25_000 }
            .Select(k => Quote(k, carry, rate, 0.13)).ToList();

        var result = ImpliedForward.FromChain(Spot, chain, T)!;

        result.CostOfCarry.Should().BeApproximately(carry, 1e-8);
        result.RiskFreeRate.Should().BeApproximately(rate, 1e-8, "the rate is an output here");
        result.RateWasDerived.Should().BeTrue();
        result.StrikesUsed.Should().Be(5);
        result.WorstParityResidual.Should().BeLessThan(1e-8);
    }

    [Fact]
    public void ExtractionSurvivesASkewBecauseParityIsModelFree()
    {
        // A different implied volatility at every strike -- which is what a real chain looks like.
        // Parity is a static arbitrage, so it must hold across the smile and the extracted carry
        // must be unaffected. If this test passed only on a flat volatility, the method would be
        // quietly assuming something it has no right to.
        const double carry = 0.053, rate = 0.065;
        var skewed = new[]
        {
            Quote(24_400, carry, rate, 0.168),
            Quote(24_600, carry, rate, 0.152),
            Quote(24_800, carry, rate, 0.130),
            Quote(25_000, carry, rate, 0.121),
            Quote(25_200, carry, rate, 0.118),
        };

        var result = ImpliedForward.FromChain(Spot, skewed, T)!;

        result.CostOfCarry.Should().BeApproximately(carry, 1e-8);
        result.RiskFreeRate.Should().BeApproximately(rate, 1e-8);
        result.WorstParityResidual.Should().BeLessThan(1e-8);
    }

    [Fact]
    public void TheExtractedParametersRepriceTheQuotesAndSatisfyParity()
    {
        const double carry = 0.053, rate = 0.065;
        var chain = new[] { 24_600, 24_800, 25_000 }.Select(k => Quote(k, carry, rate, 0.13)).ToList();

        var result = ImpliedForward.FromChain(Spot, chain, T)!;

        var basis = new OptionInputs(Spot, 24_800, T, 0.13, 0.0, 0.0, OptionRight.Call);
        var applied = result.ApplyTo(basis)!;

        applied.CostOfCarry.Should().BeApproximately(carry, 1e-8);
        applied.RiskFreeRate.Should().BeApproximately(rate, 1e-8);
        applied.Spot.Should().Be(Spot);
        applied.Volatility.Should().Be(0.13);

        // The independent check: at the extracted parameters, every observed pair satisfies parity.
        foreach (var q in chain)
        {
            double violation = PutCallParity.Violation(
                q.CallPrice, q.PutPrice, applied with { Strike = q.Strike })!.Value;

            violation.Should().BeApproximately(0.0, 1e-8, $"strike {q.Strike}");
        }
    }

    // ---------------------------------------------------------------- the documented properties

    [Fact]
    public void NearTheForwardTheAnswerBarelyMovesWhenTheRateAssumptionIsWrong()
    {
        // Sensitivity to the supplied rate is T * exp(rT) * (C - P), which vanishes where the
        // call-put spread does -- at the FORWARD, not at the spot.
        const double trueCarry = 0.053, trueRate = 0.065;
        double forward = Spot * Math.Exp(trueCarry * T);
        forward.Should().BeApproximately(24_908.27, 0.01, "the forward is 108 points above spot");

        double nearForward = Error(24_900);   // nearest the forward on a 100-point grid
        double spotAtm = Error(24_800);       // "at the money" if you mean spot
        double deepOtm = Error(22_000);

        double Error(double strike)
        {
            var q = Quote(strike, trueCarry, trueRate, 0.13);
            var wrong = ImpliedForward.FromCallPut(Spot, q, T, trueRate + 0.02)!;   // 200bp wrong
            return Math.Abs(wrong.Forward - forward);
        }

        nearForward.Should().BeLessThan(0.02, "near the forward a 200bp rate error barely registers");

        // The trap the docs warn about: spot-at-the-money gives away most of the benefit.
        spotAtm.Should().BeGreaterThan(nearForward * 10);
        deepOtm.Should().BeGreaterThan(spotAtm * 10);

        // And the sensitivity matches the closed form it is documented as, at every strike.
        foreach (var strike in new[] { 22_000.0, 24_800.0, 24_900.0 })
        {
            var q = Quote(strike, trueCarry, trueRate, 0.13);
            double predicted = T * Math.Exp(trueRate * T) * Math.Abs(q.Spread) * 0.02;

            Error(strike).Should().BeApproximately(predicted, predicted * 0.01 + 1e-6,
                $"T*exp(rT)*(C-P) should predict the error at strike {strike}");
        }
    }

    [Fact]
    public void TheResidualIsZeroByConstructionBelowThreeStrikes()
    {
        // Pinned because a zero here is meaningless and the docs say so: a straight line through two
        // points fits them exactly no matter how bad the quotes are.
        var nonsense = new List<CallPutQuote>
        {
            new(24_700, 400, 250),
            new(24_900, 111, 999),      // not a real pair
        };

        var result = ImpliedForward.FromChain(Spot, nonsense, T)!;

        result.StrikesUsed.Should().Be(2);
        result.WorstParityResidual.Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void AStaleLegShowsUpInTheResidualOnceThereAreThreeStrikes()
    {
        const double carry = 0.053, rate = 0.065;
        var chain = new[] { 24_400, 24_600, 24_800, 25_000, 25_200 }
            .Select(k => Quote(k, carry, rate, 0.13)).ToList();

        var clean = ImpliedForward.FromChain(Spot, chain, T)!;
        clean.WorstParityResidual.Should().BeLessThan(1e-8);

        // One put quoted 12 points stale -- inside a plausible bid/ask, invisible to eyeballing.
        chain[2] = chain[2] with { PutPrice = chain[2].PutPrice + 12.0 };

        var dirty = ImpliedForward.FromChain(Spot, chain, T)!;

        dirty.WorstParityResidual.Should().BeGreaterThan(5.0,
            "the fit cannot absorb a stale leg and should say so");
        Math.Abs(dirty.CostOfCarry - carry).Should().BeGreaterThan(1e-4,
            "and the carry is genuinely contaminated, which is why the residual must be read");
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void NoTimeLeftMeansNoIdentifiableCarry()
    {
        var q = Quote(24_800, 0.053, 0.065, 0.13);

        ImpliedForward.FromCallPut(Spot, q, 0.0, 0.065).Should().BeNull();
        ImpliedForward.FromCallPut(Spot, q, -1.0, 0.065).Should().BeNull();
        ImpliedForward.FromChain(Spot, new[] { q, Quote(25_000, 0.053, 0.065, 0.13) }, 0.0)
            .Should().BeNull();
    }

    [Fact]
    public void QuotesImplyingANonPositiveForwardAreRefusedRatherThanClamped()
    {
        // A put worth far more than the call at a low strike cannot come from any carry.
        var impossible = new CallPutQuote(1_000, 1.0, 30_000.0);

        ImpliedForward.FromCallPut(Spot, impossible, T, 0.065).Should().BeNull();
    }

    [Fact]
    public void AChainWhoseSpreadRisesWithStrikeIsRefused()
    {
        // C - P must fall as the strike rises. Rising means the quotes are not a no-arbitrage market.
        var backwards = new List<CallPutQuote>
        {
            new(24_600, 100, 300),
            new(24_800, 200, 200),
            new(25_000, 300, 100),
        };

        ImpliedForward.FromChain(Spot, backwards, T).Should().BeNull();
    }

    [Fact]
    public void FewerThanTwoDistinctStrikesIsRefused()
    {
        var q = Quote(24_800, 0.053, 0.065, 0.13);

        ImpliedForward.FromChain(Spot, new[] { q }, T).Should().BeNull();
        ImpliedForward.FromChain(Spot, new[] { q, q }, T).Should()
            .BeNull("two quotes at one strike determine no slope");
        ImpliedForward.FromChain(Spot, Array.Empty<CallPutQuote>(), T).Should().BeNull();
        ImpliedForward.FromChain(Spot, null!, T).Should().BeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableSpotIsRefused(double spot)
    {
        var q = Quote(24_800, 0.053, 0.065, 0.13);

        ImpliedForward.FromCallPut(spot, q, T, 0.065).Should().BeNull();
        ImpliedForward.FromChain(spot, new[] { q, Quote(25_000, 0.053, 0.065, 0.13) }, T)
            .Should().BeNull();
    }

    [Fact]
    public void InvalidQuotesAreRefusedAndInvalidOnesAreSkippedInAChain()
    {
        ImpliedForward.FromCallPut(Spot, null!, T, 0.065).Should().BeNull();
        ImpliedForward.FromCallPut(Spot, new CallPutQuote(-1, 10, 10), T, 0.065).Should().BeNull();
        ImpliedForward.FromCallPut(Spot, new CallPutQuote(24_800, double.NaN, 10), T, 0.065).Should().BeNull();
        ImpliedForward.FromCallPut(Spot, new CallPutQuote(24_800, -5, 10), T, 0.065).Should().BeNull();

        // A chain drops the unusable pairs and fits the rest, provided two distinct strikes survive.
        const double carry = 0.053, rate = 0.065;
        var mixed = new List<CallPutQuote>
        {
            Quote(24_600, carry, rate, 0.13),
            new(24_700, double.NaN, 100),
            Quote(24_800, carry, rate, 0.13),
            new(-5, 10, 10),
            Quote(25_000, carry, rate, 0.13),
        };

        var result = ImpliedForward.FromChain(Spot, mixed, T)!;

        result.StrikesUsed.Should().Be(3);
        result.CostOfCarry.Should().BeApproximately(carry, 1e-8);
    }

    [Fact]
    public void ApplyToRefusesANullContract()
    {
        var result = ImpliedForward.FromCallPut(Spot, Quote(24_800, 0.053, 0.065, 0.13), T, 0.065)!;

        result.ApplyTo(null!).Should().BeNull();
    }

    // ---------------------------------------------------------------- the motivating case

    [Fact]
    public void AWrongDividendAssumptionMisprisesTheWingsWhileTheChainFixesIt()
    {
        // The reason this class exists. The market is carrying at 5.3% (a 1.2% yield against a 6.5%
        // rate). A caller who guesses the yield at zero prices every strike off the wrong forward.
        const double trueCarry = 0.053, rate = 0.065;
        var chain = new[] { 24_600, 24_800, 25_000 }.Select(k => Quote(k, trueCarry, rate, 0.13)).ToList();

        var guessed = new OptionInputs(Spot, 25_400, T, 0.13, rate, rate, OptionRight.Call);
        double guessedPrice = BlackScholesMerton.Price(guessed)!.Value;

        var extracted = ImpliedForward.FromChain(Spot, chain, T)!;
        double correctPrice = BlackScholesMerton.Price(extracted.ApplyTo(guessed)!)!.Value;

        double truth = BlackScholesMerton.Price(guessed with { CostOfCarry = trueCarry })!.Value;

        correctPrice.Should().BeApproximately(truth, 1e-8);
        Math.Abs(guessedPrice - truth).Should().BeGreaterThan(5.0,
            "a 1.2% carry error is worth several points on a 600-point-out-of-the-money call");
    }
}
