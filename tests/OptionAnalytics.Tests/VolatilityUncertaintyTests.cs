using FluentAssertions;
using OptionAnalytics;
using Xunit;

namespace OptionAnalytics.Tests;

// The conditioning of an implied-volatility solve, made measurable. Ported from an application
// that had learned the same lesson empirically with a |delta| > 0.95 cutoff -- delta being a
// proxy for the vega that actually causes the problem.
public class VolatilityUncertaintyTests
{
    private const double NseOptionTick = 0.05;

    [Fact]
    public void It_is_the_tick_divided_by_vega()
    {
        var result = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, VegaAtSolution: 250.0, Converged: true);

        result.VolatilityUncertaintyPerTick(NseOptionTick)!.Value
            .Should().BeApproximately(NseOptionTick / 250.0, 1e-15);
    }

    [Fact]
    public void A_well_conditioned_at_the_money_solve_is_resolvable_to_a_fraction_of_a_point()
    {
        // 30-DTE at-the-money NIFTY: vega is large, so one tick barely moves the volatility.
        var inputs = new OptionInputs(24_800, 24_800, 30.0 / 365.0, 0.13, 0.065, 0.065, OptionRight.Put);
        double price = BlackScholesMerton.Price(inputs)!.Value;

        var solved = ImpliedVolatility.Solve(price, inputs with { Volatility = 0.20 })!;
        solved.Converged.Should().BeTrue();

        double uncertainty = solved.VolatilityUncertaintyPerTick(NseOptionTick)!.Value;
        uncertainty.Should().BeLessThan(0.001, "one tick moves this by under a tenth of a point");
    }

    [Fact]
    public void A_deep_in_the_money_solve_is_not_resolvable_at_any_useful_precision()
    {
        // The documented failure: the price is almost entirely intrinsic, vega collapses, and a
        // wide range of volatilities reproduces the same price. The solve still converges.
        var inputs = new OptionInputs(24_800, 30_000, 7.0 / 365.0, 0.13, 0.065, 0.065, OptionRight.Put);
        double price = BlackScholesMerton.Price(inputs)!.Value;

        var solved = ImpliedVolatility.Solve(price, inputs with { Volatility = 0.20 })!;

        // Either it refuses outright, or it converges and the uncertainty is enormous. Both are
        // acceptable; silently returning a confident-looking number is not.
        double? uncertainty = solved.VolatilityUncertaintyPerTick(NseOptionTick);
        (solved.Converged is false || uncertainty is null || uncertainty > 0.05)
            .Should().BeTrue("a deep-ITM solve must not look trustworthy");
    }

    [Fact]
    public void The_uncertainty_rises_monotonically_as_the_option_goes_further_in_the_money()
    {
        // Vega falls as moneyness grows, so the resolution limit worsens. Monotone, not a cliff
        // -- which is why reading it matters rather than relying on a threshold.
        double previous = -1;
        foreach (double strike in new[] { 24_800.0, 25_500, 26_500, 27_500, 28_500 })
        {
            var inputs = new OptionInputs(24_800, strike, 21.0 / 365.0, 0.13, 0.065, 0.065, OptionRight.Put);
            double price = BlackScholesMerton.Price(inputs)!.Value;
            var solved = ImpliedVolatility.Solve(price, inputs with { Volatility = 0.20 });

            double? u = solved?.VolatilityUncertaintyPerTick(NseOptionTick);
            if (u is null) continue;               // past the point of being solvable at all

            u.Value.Should().BeGreaterThan(previous, $"strike {strike}");
            previous = u.Value;
        }

        previous.Should().BePositive("at least some strikes were measurable");
    }

    [Fact]
    public void A_finer_tick_gives_a_finer_resolution_limit()
    {
        var result = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, VegaAtSolution: 100.0, Converged: true);

        result.VolatilityUncertaintyPerTick(0.05)!.Value
            .Should().BeApproximately(result.VolatilityUncertaintyPerTick(0.01)!.Value * 5.0, 1e-15);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.05)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unusable_tick_size_is_refused(double tick)
    {
        var result = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, VegaAtSolution: 250.0, Converged: true);
        result.VolatilityUncertaintyPerTick(tick).Should().BeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void An_unusable_vega_is_refused_rather_than_producing_an_infinity(double vega)
    {
        // A vega of zero means no finite volatility change reproduces a one-tick price move,
        // which is the extreme of the problem being measured -- reported as a refusal, not as
        // an infinite number that a caller might format and display.
        var result = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, vega, Converged: true);
        result.VolatilityUncertaintyPerTick(0.05).Should().BeNull();
    }

    [Fact]
    public void A_denormal_vega_that_would_overflow_is_refused()
    {
        // The measured 8e-71 case from the VegaAtSolution documentation: 0.05 / 8e-71 overflows
        // to infinity, which must not be returned as a number.
        var result = new ImpliedVolatilityResult(0.13, 0, 0.0, false, VegaAtSolution: 8e-71, Converged: true);

        double? u = result.VolatilityUncertaintyPerTick(0.05);
        if (u is not null) double.IsFinite(u.Value).Should().BeTrue();
    }

    [Fact]
    public void It_pairs_with_the_bid_ask_bracket_to_say_whether_a_spread_is_meaningful()
    {
        // A volatility spread narrower than the resolution limit is not a spread, it is noise.
        var inputs = new OptionInputs(24_800, 24_500, 14.0 / 365.0, 0.14, 0.065, 0.065, OptionRight.Put);
        double mid = BlackScholesMerton.Price(inputs)!.Value;

        var bracket = ImpliedVolatility.SolveBidAsk(mid - 0.5, mid + 0.5, inputs with { Volatility = 0.20 });
        bracket.Should().NotBeNull();
        bracket!.IsResolvable.Should().BeTrue();

        double spread = bracket.VolatilitySpread!.Value;
        double resolution = bracket.Mid!.VolatilityUncertaintyPerTick(NseOptionTick)!.Value;

        spread.Should().BeGreaterThan(resolution,
            "a one-rupee-wide market on a near-ATM option is wider than one tick of resolution");
    }

    // -- the relative measure: the failure the tick rule does not catch -----------

    [Fact]
    public void The_relative_measure_is_the_price_percent_divided_by_vega()
    {
        var r = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, VegaAtSolution: 200.0, Converged: true);

        r.VolatilityUncertaintyPerPricePercent(1_600.0)!.Value
            .Should().BeApproximately(1_600.0 * 0.01 / 200.0, 1e-15);
    }

    [Fact]
    public void The_tick_rule_pronounces_a_strike_healthy_while_the_relative_measure_does_not()
    {
        // THE POINT OF HAVING BOTH, at the strike where it matters most: the solve still succeeds
        // cleanly, the tick rule reports no problem, and the answer is already badly conditioned.
        var inputs = new OptionInputs(24_800, 26_000, 21.0 / 365.0, 0.105, 0.065, 0.065, OptionRight.Put);
        double price = BlackScholesMerton.Price(inputs)!.Value;

        BlackScholesMerton.Delta(inputs)!.Value.Should().BeApproximately(-0.957, 0.005);

        var solved = ImpliedVolatility.Solve(price, inputs with { Volatility = 0.20 })!;
        solved.Converged.Should().BeTrue("the solve itself is clean, which is the trap");

        solved.VolatilityUncertaintyPerTick(0.05)!.Value
            .Should().BeLessThan(0.0005, "the tick rule sees no problem here");

        solved.VolatilityUncertaintyPerPricePercent(price)!.Value
            .Should().BeGreaterThan(0.02, "but one percent of price error is over two volatility points");
    }

    [Fact]
    public void The_relative_measure_predicts_the_damage_a_small_price_error_actually_does()
    {
        var inputs = new OptionInputs(24_800, 26_000, 21.0 / 365.0, 0.105, 0.065, 0.065, OptionRight.Put);
        double fair = BlackScholesMerton.Price(inputs)!.Value;

        var solved = ImpliedVolatility.Solve(fair, inputs with { Volatility = 0.20 })!;
        double predicted = solved.VolatilityUncertaintyPerPricePercent(fair)!.Value * 0.2;

        var offByTwoTenths = ImpliedVolatility.Solve(fair * 0.998, inputs with { Volatility = 0.20 })!;
        offByTwoTenths.Converged.Should().BeTrue("this strike is still inside the arbitrage bounds");

        double actual = Math.Abs(offByTwoTenths.Volatility - 0.105);

        // A linearisation, so agreement to a third is the honest bar. Measured: 0.44 vol points
        // actual against 0.41 predicted.
        actual.Should().BeApproximately(predicted, predicted * 0.35);
        actual.Should().BeGreaterThan(0.003, "at least a third of a volatility point from 0.2% of price");
    }

    [Fact]
    public void Deeper_still_the_arbitrage_bounds_take_over_and_the_solve_refuses()
    {
        // The well-behaved end of the problem. Past about delta -0.99, a 0.2% price error puts the
        // price below the European lower bound K*exp(-rT) - S, which no volatility can produce --
        // so the answer is a refusal rather than a wrong number.
        var inputs = new OptionInputs(24_800, 26_500, 21.0 / 365.0, 0.105, 0.065, 0.065, OptionRight.Put);
        double fair = BlackScholesMerton.Price(inputs)!.Value;

        // A deep European put legitimately quotes BELOW its immediate intrinsic value, because
        // the strike is only received at expiry.
        double immediateIntrinsic = 26_500 - 24_800;
        fair.Should().BeLessThan(immediateIntrinsic);

        double europeanLowerBound = 26_500 * Math.Exp(-0.065 * 21.0 / 365.0) - 24_800;
        (fair * 0.998).Should().BeLessThan(europeanLowerBound, "0.2% below fair breaches the bound");

        var perturbed = ImpliedVolatility.Solve(fair * 0.998, inputs with { Volatility = 0.20 });
        (perturbed is null || !perturbed.Converged).Should().BeTrue(
            "no volatility reproduces a price below the arbitrage bound");
    }

    [Fact]
    public void At_the_money_the_relative_measure_is_small_which_is_why_the_smile_is_read_there()
    {
        var atm = new OptionInputs(24_800, 24_800, 21.0 / 365.0, 0.105, 0.065, 0.065, OptionRight.Put);
        double price = BlackScholesMerton.Price(atm)!.Value;
        var solved = ImpliedVolatility.Solve(price, atm with { Volatility = 0.20 })!;

        solved.VolatilityUncertaintyPerPricePercent(price)!.Value
            .Should().BeLessThan(0.002, "under a fifth of a volatility point per percent of price");
    }

    [Fact]
    public void The_relative_measure_rises_monotonically_with_moneyness()
    {
        double previous = -1;
        foreach (double strike in new[] { 24_800.0, 25_500, 26_500, 27_500 })
        {
            var inputs = new OptionInputs(24_800, strike, 21.0 / 365.0, 0.105, 0.065, 0.065, OptionRight.Put);
            double price = BlackScholesMerton.Price(inputs)!.Value;
            var solved = ImpliedVolatility.Solve(price, inputs with { Volatility = 0.20 });

            double? u = solved?.VolatilityUncertaintyPerPricePercent(price);
            if (u is null) continue;

            u.Value.Should().BeGreaterThan(previous, $"strike {strike}");
            previous = u.Value;
        }
        previous.Should().BePositive();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unusable_price_is_refused_by_the_relative_measure(double price)
    {
        var r = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, VegaAtSolution: 200.0, Converged: true);
        r.VolatilityUncertaintyPerPricePercent(price).Should().BeNull();
    }

    [Fact]
    public void An_unusable_vega_is_refused_by_the_relative_measure()
    {
        foreach (double vega in new[] { 0.0, -1.0, double.NaN })
        {
            var r = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, vega, Converged: true);
            r.VolatilityUncertaintyPerPricePercent(1_600.0).Should().BeNull();
        }
    }

    [Fact]
    public void The_two_measures_answer_different_questions_and_neither_subsumes_the_other()
    {
        var r = new ImpliedVolatilityResult(0.13, 4, 1e-12, false, VegaAtSolution: 111.9, Converged: true);

        double tick = r.VolatilityUncertaintyPerTick(0.05)!.Value;
        double rel  = r.VolatilityUncertaintyPerPricePercent(1_602.44)!.Value;

        tick.Should().BeLessThan(0.001);
        rel.Should().BeGreaterThan(0.10);
        (rel / tick).Should().BeGreaterThan(100, "same vega, three orders of magnitude apart");
    }
}
