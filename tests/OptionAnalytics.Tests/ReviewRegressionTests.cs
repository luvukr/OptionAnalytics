using FluentAssertions;
using OptionAnalytics;
using Xunit;

namespace OptionAnalytics.Tests;

/// <summary>
/// Regressions for defects found by an independent review of 1.1.0, plus the invariants that
/// review's rejected findings turned out to depend on.
/// </summary>
/// <remarks>
/// Each test names the mechanism rather than the finding number, but the grouping is deliberate:
/// three of the ten findings were wrong, and two of those had plausible-sounding fixes that would
/// have made the library less correct. The tests that pin the CURRENT behaviour at an atom of
/// probability exist so that argument does not have to be had twice.
/// </remarks>
public class ReviewRegressionTests
{
    // ---------------------------------------------------------------- undefined enum values

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(-2)]
    public void UndefinedOptionRightIsRejectedRatherThanPriced(int raw)
    {
        var inputs = new OptionInputs(100, 100, 1.0, 0.20, 0.065, 0.065, (OptionRight)raw);

        inputs.IsValid.Should().BeFalse("an enum cast is not a contract right");

        BlackScholesMerton.Price(inputs).Should().BeNull();
        BlackScholesMerton.Delta(inputs).Should().BeNull();
        BlackScholesMerton.Gamma(inputs).Should().BeNull();
        BlackScholesMerton.ComputeGreeks(inputs).Should().BeNull();
        OptionProbability.InTheMoneyAtExpiry(inputs).Should().BeNull();
        ImpliedVolatility.Solve(5.0, inputs).Should().BeNull();
    }

    [Fact]
    public void AnUndefinedRightUsedToProduceAConfidentlyWrongPrice()
    {
        // Before the fix, Sign was (int)Right and multiplied straight through the formula, so a
        // cast of 99 priced a 100-strike at-the-money call at 482.83 -- not a zero, not a NaN, but
        // a plausible-looking number 24 times too large. That is the worst failure mode available
        // to a pricing library, which is why the guard is on IsValid rather than in each formula.
        var garbage = new OptionInputs(100, 100, 1.0, 0.20, 0.065, 0.065, (OptionRight)99);
        BlackScholesMerton.Price(garbage).Should().BeNull();

        // default(OptionRight) is 0 because neither member is zero -- reachable from a missing
        // JSON field or an uninitialised struct, not only from a deliberate cast.
        ((int)default(OptionRight)).Should().Be(0);
    }

    [Theory]
    [InlineData(OptionRight.Call)]
    [InlineData(OptionRight.Put)]
    public void DefinedRightsStillPrice(OptionRight right)
    {
        var inputs = new OptionInputs(100, 100, 1.0, 0.20, 0.065, 0.065, right);
        inputs.IsValid.Should().BeTrue();
        BlackScholesMerton.Price(inputs).Should().NotBeNull();
    }

    // ---------------------------------------------------------------- degenerate time/rate greeks

    [Fact]
    public void ThetaAtZeroVolatilityIsTheDiscountUnwinding()
    {
        // 110 spot, 100 strike, one year, no volatility: the payoff is certain, but the strike is
        // still discounted and that discount still unwinds. Theta is -r*K*exp(-r*T).
        var inputs = OptionModel.BlackScholes(110, 100, 1.0, 0.0, 0.05, OptionRight.Call);

        BlackScholesMerton.Theta(inputs)!.Value
            .Should().BeApproximately(-0.05 * 100 * Math.Exp(-0.05), 1e-12);
    }

    [Fact]
    public void RhoAtZeroVolatilityIsTheDiscountedStrikeSensitivity()
    {
        var inputs = OptionModel.BlackScholes(110, 100, 1.0, 0.0, 0.05, OptionRight.Call);

        BlackScholesMerton.RhoCarryTracksRate(inputs)!.Value
            .Should().BeApproximately(100 * 1.0 * Math.Exp(-0.05), 1e-12);
    }

    [Fact]
    public void CharmAtZeroVolatilityIsNonZeroWheneverCarryDiffersFromRate()
    {
        // Degenerate delta is exp((b-r)T), which moves with time unless b == r. The review found
        // the theta and rho holes but not this one.
        var withDividend = OptionModel.Merton(110, 100, 1.0, 0.0, 0.05, dividendYield: 0.03, right: OptionRight.Call);

        BlackScholesMerton.Charm(withDividend)!.Value
            .Should().BeApproximately(0.03 * Math.Exp(-0.03), 1e-12);

        // ...and zero exactly when it should be.
        var noCarryGap = OptionModel.BlackScholes(110, 100, 1.0, 0.0, 0.05, OptionRight.Call);
        BlackScholesMerton.Charm(noCarryGap)!.Value.Should().Be(0.0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1e-12)]
    [InlineData(1e-8)]
    [InlineData(1e-4)]
    [InlineData(0.20)]
    public void ThePdeIdentityHoldsAcrossTheZeroVolatilityBoundary(double volatility)
    {
        // The bug this pins: the identity held to machine precision at 1e-8 and was violated by
        // 4.76 at exactly zero. A discontinuity in a verified identity is the signature of a
        // special case that was guessed rather than derived.
        foreach (var (spot, strike) in new[] { (110.0, 100.0), (90.0, 100.0), (100.0, 100.0) })
            foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
                foreach (var (rate, carry) in new[] { (0.065, 0.065), (0.065, 0.0), (0.065, 0.09), (-0.005, -0.005) })
                {
                    var inputs = new OptionInputs(spot, strike, 1.0, volatility, rate, carry, right);
                    var g = BlackScholesMerton.ComputeGreeks(inputs)!;

                    double residual = g.Theta
                                      + carry * spot * g.Delta
                                      + 0.5 * volatility * volatility * spot * spot * g.Gamma
                                      - rate * g.Price;

                    residual.Should().BeApproximately(0.0, 1e-9,
                        $"PDE identity at spot={spot} strike={strike} vol={volatility} b={carry} {right}");
                }
    }

    [Fact]
    public void TheTwoRhosNoLongerDisagreeAtZeroVolatility()
    {
        // RhoCarryFixed delegates to Price and was always right in the degenerate branch, while
        // RhoCarryTracksRate returned zero. That internal disagreement is what proved it a bug
        // rather than a deliberate convention.
        var inputs = OptionModel.Black76(110, 100, 1.0, 0.0, 0.05, OptionRight.Call);

        double fixedRho = BlackScholesMerton.RhoCarryFixed(inputs)!.Value;
        fixedRho.Should().BeApproximately(-1.0 * BlackScholesMerton.Price(inputs)!.Value, 1e-12);

        // Under Black-76 the carry cannot track the rate, but the equity rho is still the
        // discounted-strike derivative and is still not zero for an in-the-money contract.
        BlackScholesMerton.RhoCarryTracksRate(inputs)!.Value.Should().NotBe(0.0);
    }

    [Fact]
    public void VolatilityDerivativesDoVanishWhenVolatilityDoes()
    {
        // The counterpart to the fix: these four genuinely are zero, and the fix must not have
        // "corrected" them too.
        var inputs = OptionModel.BlackScholes(110, 100, 1.0, 0.0, 0.05, OptionRight.Call);

        BlackScholesMerton.Gamma(inputs)!.Value.Should().Be(0.0);
        BlackScholesMerton.Vega(inputs)!.Value.Should().Be(0.0);
        BlackScholesMerton.Vanna(inputs)!.Value.Should().Be(0.0);
        BlackScholesMerton.Volga(inputs)!.Value.Should().Be(0.0);
    }

    [Fact]
    public void ExactlyAtTheMoneyEveryDegenerateGreekIsZero()
    {
        // The payoff has a kink here and the left and right derivatives differ, so zero is the only
        // answer that does not privilege one side.
        var atm = OptionModel.Black76(100, 100, 1.0, 0.0, 0.05, OptionRight.Call);

        BlackScholesMerton.Delta(atm)!.Value.Should().Be(0.0);
        BlackScholesMerton.Theta(atm)!.Value.Should().Be(0.0);
        BlackScholesMerton.RhoCarryTracksRate(atm)!.Value.Should().Be(0.0);
        BlackScholesMerton.Charm(atm)!.Value.Should().Be(0.0);
    }

    // ---------------------------------------------------------------- delta above one

    [Fact]
    public void DeltaExceedsOneWhenCarryExceedsRate()
    {
        // Documented as [-1, 1], which was wrong: the bound is the carry discount factor. A
        // negative dividend yield is the cleanest way to see it, and a foreign rate below the
        // domestic one does the same under Garman-Kohlhagen.
        var inputs = OptionModel.Merton(100, 50, 2.0, 0.20, 0.02, dividendYield: -0.05, right: OptionRight.Call);

        double delta = BlackScholesMerton.Delta(inputs)!.Value;

        delta.Should().BeGreaterThan(1.0);
        delta.Should().BeLessThan(Math.Exp((inputs.CostOfCarry - inputs.RiskFreeRate) * inputs.TimeToExpiry));
    }

    [Fact]
    public void DeltaStaysWithinTheCarryFactorBoundEverywhere()
    {
        foreach (var spot in new[] { 20.0, 60.0, 100.0, 140.0, 400.0 })
            foreach (var t in new[] { 0.0, 0.02, 1.0, 5.0 })
                foreach (var vol in new[] { 0.0, 0.08, 0.35, 1.2 })
                    foreach (var (rate, carry) in new[] { (0.065, 0.065), (0.065, 0.0), (0.02, 0.07), (-0.005, -0.02) })
                        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
                        {
                            var inputs = new OptionInputs(spot, 100, t, vol, rate, carry, right);
                            double bound = Math.Exp((carry - rate) * t);
                            double delta = BlackScholesMerton.Delta(inputs)!.Value;

                            Math.Abs(delta).Should().BeLessThanOrEqualTo(bound + 1e-12);
                            (right == OptionRight.Call ? delta : -delta).Should().BeGreaterThanOrEqualTo(-1e-12);
                        }
    }

    // ---------------------------------------------------------------- touch probability overflow

    [Fact]
    public void ProbabilityOfTouchingNeverReturnsNaN()
    {
        // exp(2*mu*a/sigma^2) overflowed to +Infinity while the CDF factor underflowed to exactly
        // zero, and Infinity * 0 is NaN -- which survived Math.Clamp and would have propagated
        // through every downstream calculation. Reachable only at absurd parameters, but NaN is the
        // one failure mode that cannot be noticed downstream.
        var extreme = OptionModel.BlackScholes(100, 100, 1.0, 0.05, 0.50, OptionRight.Call);

        double p = OptionProbability.ProbabilityOfTouching(extreme, 1000.0)!.Value;

        double.IsNaN(p).Should().BeFalse();
        p.Should().BeInRange(0.0, 1.0);
        p.Should().BeLessThan(1e-200, "a barrier 56 standard deviations away is not reachable");
    }

    [Theory]
    [InlineData(0.05, 0.50, 1000.0)]
    [InlineData(0.02, 0.50, 5000.0)]
    [InlineData(0.01, 0.80, 100000.0)]
    [InlineData(0.05, 0.07, 1000.0)]
    [InlineData(0.20, 0.065, 200.0)]
    [InlineData(0.30, 0.065, 50.0)]
    public void TouchProbabilityIsAlwaysAProbability(double vol, double rate, double barrier)
    {
        var inputs = OptionModel.BlackScholes(100, 100, 1.0, vol, rate, OptionRight.Call);

        double p = OptionProbability.ProbabilityOfTouching(inputs, barrier)!.Value;

        double.IsFinite(p).Should().BeTrue();
        p.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void TouchProbabilitySurvivesAVolatilityWhoseSquareUnderflows()
    {
        // Volatility of 1e-300 is valid input; its square is zero, which used to divide.
        var inputs = OptionModel.BlackScholes(100, 100, 1.0, 1e-300, 0.065, OptionRight.Call);

        double p = OptionProbability.ProbabilityOfTouching(inputs, 120.0)!.Value;

        double.IsFinite(p).Should().BeTrue();
        p.Should().BeInRange(0.0, 1.0);
    }

    // ---------------------------------------------------------------- the atom at the strike

    [Fact]
    public void AtAnAtomNeitherCallNorPutFinishesInTheMoney()
    {
        // The review argued P(call ITM) + P(put ITM) must equal 1 and that both should return 0.5.
        // That axiom needs P(S_T == K) == 0, which is exactly what is false here: with no time
        // left the terminal price IS the strike, so neither finishes beyond it. Returning 0.5
        // would claim a coin flip on an already-settled contract.
        var call = OptionModel.BlackScholes(100, 100, 0.0, 0.20, 0.05, OptionRight.Call);
        var put = call with { Right = OptionRight.Put };

        OptionProbability.InTheMoneyAtExpiry(call)!.Value.Should().Be(0.0);
        OptionProbability.InTheMoneyAtExpiry(put)!.Value.Should().Be(0.0);
        BlackScholesMerton.Price(call)!.Value.Should().Be(0.0);
    }

    [Fact]
    public void AboveAndBelowAgreeWithInTheMoneyAtAnAtom()
    {
        // The real defect the complementarity argument exposed: ProbabilityBelow was derived as
        // 1 - Above, which turned the atom into certainty and reported 1 for finishing below a
        // level the price sits exactly on -- disagreeing with the put's own ITM probability.
        var inputs = OptionModel.BlackScholes(100, 100, 0.0, 0.20, 0.05, OptionRight.Put);

        double itmPut = OptionProbability.InTheMoneyAtExpiry(inputs)!.Value;
        double below = OptionProbability.ProbabilityBelow(inputs, 100.0)!.Value;
        double above = OptionProbability.ProbabilityAbove(inputs, 100.0)!.Value;

        below.Should().Be(itmPut);
        above.Should().Be(0.0);
        below.Should().Be(0.0);
    }

    [Fact]
    public void AboveAndBelowStillSumToOneWhereverTheDistributionIsContinuous()
    {
        foreach (var level in new[] { 60.0, 99.99, 100.0, 100.01, 180.0 })
        {
            var inputs = OptionModel.BlackScholes(100, 100, 0.25, 0.22, 0.065, OptionRight.Call);

            double above = OptionProbability.ProbabilityAbove(inputs, level)!.Value;
            double below = OptionProbability.ProbabilityBelow(inputs, level)!.Value;

            (above + below).Should().BeApproximately(1.0, 1e-14, $"level {level}");
        }
    }

    // ---------------------------------------------------------------- solver honesty

    [Fact]
    public void ABoundClampIsNotReportedAsAConvergedSolve()
    {
        var inputs = OptionModel.BlackScholes(100, 100, 1.0, 0.20, 0.05, OptionRight.Call);
        double floorPrice = BlackScholesMerton.Price(inputs with { Volatility = ImpliedVolatility.MinVolatility })!.Value;
        double ceilingPrice = BlackScholesMerton.Price(inputs with { Volatility = ImpliedVolatility.MaxVolatility })!.Value;

        var atFloor = ImpliedVolatility.Solve(floorPrice, inputs)!;
        var atCeiling = ImpliedVolatility.Solve(ceilingPrice, inputs)!;

        // PriceError of exactly zero and Iterations of zero look like a flawless solve. They are
        // not: no solving happened, and the returned volatility is the search bound.
        atFloor.PriceError.Should().Be(0.0);
        atFloor.Iterations.Should().Be(0);
        atFloor.Volatility.Should().Be(ImpliedVolatility.MinVolatility);
        atFloor.Converged.Should().BeFalse();

        atCeiling.Volatility.Should().Be(ImpliedVolatility.MaxVolatility);
        atCeiling.Converged.Should().BeFalse();
    }

    [Fact]
    public void ExhaustingTheIterationCapIsReportedAsNotConverged()
    {
        var inputs = OptionModel.BlackScholes(100, 100, 1.0, 0.20, 0.05, OptionRight.Call);

        var starved = ImpliedVolatility.Solve(15.0, inputs, tolerance: 1e-15, maxIterations: 1)!;

        starved.Converged.Should().BeFalse();
        starved.PriceError.Should().BeGreaterThan(1e-15);
    }

    [Fact]
    public void AGenuineSolveIsReportedAsConverged()
    {
        var inputs = OptionModel.BlackScholes(100, 100, 1.0, 0.20, 0.05, OptionRight.Call);
        double price = BlackScholesMerton.Price(inputs)!.Value;

        var solved = ImpliedVolatility.Solve(price, inputs)!;

        solved.Converged.Should().BeTrue();
        solved.Volatility.Should().BeApproximately(0.20, 1e-8);
    }

    [Fact]
    public void TheVegaUnderflowCaseIsFlaggedByBothVegaAndConvergence()
    {
        // A deep in-the-money put a week out prices identically across a wide volatility range.
        // The solve lands on the floor with a perfect price error; vega is the honest signal, and
        // Converged now agrees rather than leaving vega as the only tell.
        var inputs = OptionModel.BlackScholes(100, 140, 7.0 / 365.0, 0.20, 0.065, OptionRight.Put);
        double price = BlackScholesMerton.Price(inputs with { Volatility = 0.05 })!.Value;

        var result = ImpliedVolatility.Solve(price, inputs)!;

        result.VegaAtSolution.Should().BeLessThan(1e-20);
        result.Converged.Should().BeFalse();
    }

    // ---------------------------------------------------------------- breakeven dedup at scale

    [Theory]
    [InlineData(1e-7)]
    [InlineData(1.0)]
    [InlineData(25000.0)]
    public void DistinctBreakevensSurviveAtEveryPriceScale(double scale)
    {
        // A butterfly has two breakevens. The dedup tolerance must not merge them at a tiny price
        // scale, nor emit either twice at an index-sized one.
        var legs = new List<StrategyLeg>
        {
            new(OptionRight.Call, 1.00 * scale, 1, 0.020 * scale),
            new(OptionRight.Call, 1.05 * scale, -2, 0.010 * scale),
            new(OptionRight.Call, 1.10 * scale, 1, 0.005 * scale),
        };

        var profile = OptionStrategy.Profile(legs)!;

        profile.Breakevens.Should().HaveCount(2, $"scale {scale}");
        profile.Breakevens[0].Should().BeApproximately(1.005 * scale, 1e-6 * scale);
        profile.Breakevens[1].Should().BeApproximately(1.095 * scale, 1e-6 * scale);
    }

    // ---------------------------------------------------------------- deep-tail invariants

    [Theory]
    [InlineData(20.0, 0.02, 0.08)]
    [InlineData(20.0, 3.0, 1.20)]
    [InlineData(400.0, 0.02, 0.08)]
    [InlineData(400.0, 3.0, 1.20)]
    [InlineData(60.0, 0.0027, 0.05)]
    public void DeepTailsSatisfyTheInvariantsFiniteDifferencesCannotCheck(double spot, double t, double vol)
    {
        // The finite-difference harness skips grid points where the greek is smaller than its own
        // error estimate, which leaves the extremes unasserted -- precisely where overflow and
        // underflow hide. These invariants hold by mathematics, not by tolerance.
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
            foreach (var (rate, carry) in new[] { (0.065, 0.065), (0.065, 0.0), (-0.005, -0.02) })
            {
                var inputs = new OptionInputs(spot, 100, t, vol, rate, carry, right);
                var g = BlackScholesMerton.ComputeGreeks(inputs)!;
                string ctx = $"spot={spot} t={t} vol={vol} b={carry} {right}";

                double.IsFinite(g.Price).Should().BeTrue(ctx);
                g.Price.Should().BeGreaterThanOrEqualTo(0.0, ctx);
                g.Gamma.Should().BeGreaterThanOrEqualTo(0.0, ctx);
                g.Vega.Should().BeGreaterThanOrEqualTo(0.0, ctx);

                foreach (var value in new[] { g.Delta, g.Gamma, g.Vega, g.Theta, g.Vanna, g.Volga, g.Charm })
                    double.IsFinite(value).Should().BeTrue(ctx);

                // Price is bounded by the discounted forward for a call and the discounted strike
                // for a put -- the no-arbitrage envelope, independent of the model.
                double rateDiscount = Math.Exp(-rate * t);
                double ceiling = right == OptionRight.Call
                    ? spot * Math.Exp((carry - rate) * t)
                    : 100 * rateDiscount;
                g.Price.Should().BeLessThanOrEqualTo(ceiling + 1e-9, ctx);
            }
    }

    [Fact]
    public void PriceIsMonotonicInVolatilityIntoTheDeepTails()
    {
        foreach (var spot in new[] { 20.0, 60.0, 100.0, 400.0 })
            foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
            {
                double previous = double.NegativeInfinity;
                foreach (var vol in new[] { 0.0, 1e-6, 1e-3, 0.05, 0.2, 0.8, 2.0, 5.0 })
                {
                    double price = BlackScholesMerton.Price(
                        new OptionInputs(spot, 100, 0.25, vol, 0.065, 0.065, right))!.Value;

                    price.Should().BeGreaterThanOrEqualTo(previous - 1e-12,
                        $"spot={spot} {right} at vol={vol}");
                    previous = price;
                }
            }
    }
}
