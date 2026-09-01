using FluentAssertions;
using Xunit;

namespace OptionAnalytics.Tests;

/// <summary>
/// Verifies every analytic greek against a finite-difference bump of <c>Price</c>, across a grid
/// spanning moneyness, maturity, volatility, sign of the rate, and all four models.
/// </summary>
/// <remarks>
/// This is an INDEPENDENT check: the closed-form greek and the price function share no code path,
/// so agreement between them is real evidence rather than a tautology.
///
/// The tolerance deserves explanation, because getting it wrong makes the test useless in either
/// direction. A finite difference carries two errors that pull opposite ways:
///
///   truncation  ~ h^2 * (third derivative)  -- shrinks as h shrinks
///   round-off   ~ eps * |P| / h^order       -- GROWS as h shrinks
///
/// Near a zero-valued greek the round-off term swamps the quantity outright: the true gamma of a
/// deep out-of-the-money option can be 3e-11 while the second difference of its price is pure
/// float noise at 1e-14. An earlier version of this test used a flat relative threshold and
/// "failed" on exactly those points -- it was measuring the harness, not the library.
///
/// So each greek is estimated at h and at 2h. |FD(h) - FD(2h)| / 3 estimates the truncation term
/// by Richardson; eps * |P| / h^order bounds the round-off term. The analytic value must agree
/// with FD(h) to within their sum -- as closely as the finite difference is itself capable of
/// agreeing. Points where the difference resolves nothing are counted and asserted to be a
/// minority, rather than silently skipped.
/// </remarks>
public class GreekVerificationTests
{
    private const double Eps = 2.220446049250313e-16;

    public static TheoryData<string> ModelNames => new() { "BlackScholes", "Merton", "Black76", "GarmanKohlhagen" };

    internal static IEnumerable<OptionInputs> Grid(string model)
    {
        foreach (double s in new[] { 60.0, 90.0, 100.0, 110.0, 150.0 })
        foreach (double t in new[] { 0.02, 0.25, 1.0, 3.0 })
        foreach (double v in new[] { 0.08, 0.20, 0.60 })
        foreach (double r in new[] { -0.005, 0.02, 0.07 })
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            yield return model switch
            {
                "BlackScholes" => OptionModel.BlackScholes(s, 100, t, v, r, right),
                "Merton" => OptionModel.Merton(s, 100, t, v, r, 0.03, right),
                "Black76" => OptionModel.Black76(s, 100, t, v, r, right),
                "GarmanKohlhagen" => OptionModel.GarmanKohlhagen(s, 100, t, v, r, 0.04, right),
                _ => throw new ArgumentOutOfRangeException(nameof(model)),
            };
        }
    }

    private static double P(OptionInputs i) => BlackScholesMerton.Price(i)!.Value;
    private static double D(OptionInputs i) => BlackScholesMerton.Delta(i)!.Value;
    private static double V(OptionInputs i) => BlackScholesMerton.Vega(i)!.Value;

    /// <summary>Asserts one greek against its finite difference, returning false if unresolvable.</summary>
    private static bool Agrees(
        double analytic, double fdH, double fdTwoH, double priceScale, double h, int order, string where)
    {
        double roundOff = Eps * Math.Max(priceScale, 1.0) / Math.Pow(h, order);
        double fdError = roundOff + Math.Abs(fdH - fdTwoH) / 3.0;

        if (Math.Abs(fdH) < 10 * fdError) return false;   // the difference resolves nothing here

        double tolerance = Math.Max(10 * fdError, 1e-9 * Math.Abs(fdH));
        Math.Abs(analytic - fdH).Should().BeLessThanOrEqualTo(tolerance, where);
        return true;
    }

    [Theory]
    [MemberData(nameof(ModelNames))]
    public void EveryGreekMatchesAFiniteDifferenceBumpOfPrice(string model)
    {
        int resolved = 0, unresolvable = 0;

        foreach (var x in Grid(model))
        {
            string w = $"{model} S={x.Spot} T={x.TimeToExpiry} vol={x.Volatility} r={x.RiskFreeRate} {x.Right}";
            var g = BlackScholesMerton.ComputeGreeks(x)!;
            double scale = Math.Max(P(x), x.Spot);

            double CentralSpot(Func<OptionInputs, double> f, double h)
                => (f(x with { Spot = x.Spot + h }) - f(x with { Spot = x.Spot - h })) / (2 * h);
            double CentralVol(Func<OptionInputs, double> f, double h)
                => (f(x with { Volatility = x.Volatility + h }) - f(x with { Volatility = x.Volatility - h })) / (2 * h);
            double CentralTime(Func<OptionInputs, double> f, double h)
                => (f(x with { TimeToExpiry = x.TimeToExpiry + h }) - f(x with { TimeToExpiry = x.TimeToExpiry - h })) / (2 * h);
            double CentralRate(double h, bool carryToo)
                => (P(x with { RiskFreeRate = x.RiskFreeRate + h, CostOfCarry = carryToo ? x.CostOfCarry + h : x.CostOfCarry })
                    - P(x with { RiskFreeRate = x.RiskFreeRate - h, CostOfCarry = carryToo ? x.CostOfCarry - h : x.CostOfCarry })) / (2 * h);
            double SecondSpot(double h)
                => (P(x with { Spot = x.Spot + h }) - 2 * P(x) + P(x with { Spot = x.Spot - h })) / (h * h);

            double hS = x.Spot * 1e-5, hG = x.Spot * 1e-4, hV = 1e-4;
            double hT = Math.Min(1e-4, x.TimeToExpiry * 1e-3), hR = 1e-5;

            var outcomes = new[]
            {
                Agrees(g.Delta, CentralSpot(P, hS), CentralSpot(P, 2 * hS), scale, hS, 1, $"Delta {w}"),
                Agrees(g.Gamma, SecondSpot(hG), SecondSpot(2 * hG), scale, hG, 2, $"Gamma {w}"),
                Agrees(g.Vega, CentralVol(P, hV), CentralVol(P, 2 * hV), scale, hV, 1, $"Vega {w}"),
                // Theta is dV/dt = -dV/dT, hence the sign.
                Agrees(g.Theta, -CentralTime(P, hT), -CentralTime(P, 2 * hT), scale, hT, 1, $"Theta {w}"),
                Agrees(g.RhoCarryFixed, CentralRate(hR, false), CentralRate(2 * hR, false), scale, hR, 1, $"RhoCarryFixed {w}"),
                Agrees(g.RhoCarryTracksRate, CentralRate(hR, true), CentralRate(2 * hR, true), scale, hR, 1, $"RhoCarryTracksRate {w}"),
                // Vanna, volga and charm differentiate a GREEK, so the scale is that greek's size.
                Agrees(g.Vanna, CentralVol(D, hV), CentralVol(D, 2 * hV), 1.0, hV, 1, $"Vanna {w}"),
                Agrees(g.Volga, CentralVol(V, hV), CentralVol(V, 2 * hV), Math.Max(V(x), 1.0), hV, 1, $"Volga {w}"),
                Agrees(g.Charm, -CentralTime(D, hT), -CentralTime(D, 2 * hT), 1.0, hT, 1, $"Charm {w}"),
            };

            resolved += outcomes.Count(o => o);
            unresolvable += outcomes.Count(o => !o);
        }

        resolved.Should().BeGreaterThan(unresolvable * 2,
            "most of the grid must actually be resolvable, or this test proves nothing");
    }

    [Theory]
    [MemberData(nameof(ModelNames))]
    public void PutCallParityHoldsExactly(string model)
    {
        // Model-free: parity follows from a static arbitrage, not from Black-Scholes. It catches
        // errors that no single price reveals, because it constrains the two sides against each
        // other.
        foreach (var x in Grid(model).Where(i => i.Right == OptionRight.Call))
        {
            double call = P(x);
            double put = P(x with { Right = OptionRight.Put });
            double parity = PutCallParity.ForwardMinusStrike(x)!.Value;

            // Absolute, normalised by spot. Parity is legitimately ZERO for an at-the-money
            // futures option, so a relative measure divides by zero exactly where it holds best.
            (Math.Abs((call - put) - parity) / x.Spot).Should().BeLessThan(1e-14,
                $"{model} S={x.Spot} T={x.TimeToExpiry} vol={x.Volatility}");
        }
    }

    [Theory]
    [MemberData(nameof(ModelNames))]
    public void TheBlackScholesPdeIdentityHolds(string model)
    {
        // Theta + b*S*Delta + 0.5*sigma^2*S^2*Gamma == r*V.
        //
        // The strongest single check available: it ties four quantities computed by four separate
        // formulas into one equation that no-arbitrage forces to balance. An error in any one of
        // them breaks it.
        foreach (var x in Grid(model))
        {
            var g = BlackScholesMerton.ComputeGreeks(x)!;

            double lhs = g.Theta
                         + x.CostOfCarry * x.Spot * g.Delta
                         + 0.5 * x.Volatility * x.Volatility * x.Spot * x.Spot * g.Gamma;
            double rhs = x.RiskFreeRate * g.Price;

            (Math.Abs(lhs - rhs) / Math.Max(Math.Abs(rhs), 1e-6)).Should().BeLessThan(1e-9,
                $"{model} S={x.Spot} T={x.TimeToExpiry} vol={x.Volatility}");
        }
    }
}

public class ModelEquivalenceTests
{
    private static double P(OptionInputs i) => BlackScholesMerton.Price(i)!.Value;

    [Fact]
    public void Black76OnTheForwardEqualsMertonOnTheSpot()
    {
        // The cost-of-carry design claims these are one model at two parameter values. If that is
        // true, pricing the forward under zero carry must reproduce pricing the spot under r - q.
        foreach (double s in new[] { 80.0, 100.0, 120.0 })
        foreach (double t in new[] { 0.1, 1.0, 2.0 })
        foreach (double r in new[] { 0.02, 0.06 })
        foreach (double q in new[] { 0.0, 0.03 })
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            var merton = OptionModel.Merton(s, 100, t, 0.25, r, q, right);
            var black = OptionModel.Black76(s * Math.Exp((r - q) * t), 100, t, 0.25, r, right);

            P(black).Should().BeApproximately(P(merton), Math.Max(P(merton), 1.0) * 1e-12,
                $"S={s} T={t} r={r} q={q} {right}");
        }
    }

    [Fact]
    public void MertonWithNoDividendIsExactlyBlackScholes()
    {
        foreach (var x in GreekVerificationTests.Grid("BlackScholes"))
        {
            var merton = OptionModel.Merton(x.Spot, x.Strike, x.TimeToExpiry, x.Volatility, x.RiskFreeRate, 0.0, x.Right);
            P(merton).Should().Be(P(x));
        }
    }

    [Fact]
    public void GarmanKohlhagenWithNoForeignRateIsExactlyBlackScholes()
    {
        foreach (var x in GreekVerificationTests.Grid("BlackScholes"))
        {
            var fx = OptionModel.GarmanKohlhagen(x.Spot, x.Strike, x.TimeToExpiry, x.Volatility, x.RiskFreeRate, 0.0, x.Right);
            P(fx).Should().Be(P(x));
        }
    }

    [Fact]
    public void RhoCarryFixedEqualsMinusTimeTimesPrice_ForEveryModel()
    {
        // Not a coincidence: holding the carry fixed, the whole price is exp(-rT) times a quantity
        // that does not involve r, so differentiating gives -T times the price. Asserted because
        // it is the identity that makes the two-rho design defensible.
        foreach (var model in new[] { "BlackScholes", "Merton", "Black76", "GarmanKohlhagen" })
        foreach (var x in GreekVerificationTests.Grid(model))
        {
            BlackScholesMerton.RhoCarryFixed(x)!.Value
                .Should().BeApproximately(-x.TimeToExpiry * P(x), 1e-12);
        }
    }
}
