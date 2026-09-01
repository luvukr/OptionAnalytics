using FluentAssertions;
using Xunit;

namespace OptionAnalytics.Tests;

public class ImpliedVolatilityTests
{
    private static double P(OptionInputs i) => BlackScholesMerton.Price(i)!.Value;

    [Theory]
    [InlineData("BlackScholes")]
    [InlineData("Merton")]
    [InlineData("Black76")]
    [InlineData("GarmanKohlhagen")]
    public void RecoversTheVolatilityThatProducedThePrice(string model)
    {
        // The tolerance is not a taste judgement. No solver can recover volatility more precisely
        // than priceTolerance / vega -- that is the conditioning of the problem itself. Contracts
        // where the bound is looser than 1% of the true volatility are genuinely unrecoverable by
        // ANY method, and are counted rather than asserted on.
        const double priceTolerance = 1e-10;
        int recovered = 0, unrecoverable = 0;

        foreach (var x in GreekVerificationTests.Grid(model))
        {
            double price = P(x);
            if (price < 1e-12) continue;

            var result = ImpliedVolatility.Solve(price, x with { Volatility = 0 }, priceTolerance);
            result.Should().NotBeNull();

            double volUncertainty = priceTolerance / Math.Max(result!.VegaAtSolution, 1e-300);
            if (volUncertainty > 0.01 * x.Volatility) { unrecoverable++; continue; }

            recovered++;
            result.Volatility.Should().BeApproximately(x.Volatility, Math.Max(volUncertainty * 100, 1e-12),
                $"{model} S={x.Spot} T={x.TimeToExpiry} vol={x.Volatility} {x.Right}");
        }

        recovered.Should().BeGreaterThan(unrecoverable,
            "most of the grid must be recoverable or this test proves nothing");
    }

    [Fact]
    public void ConvergesInFarFewerStepsThanBisectionWould()
    {
        // Bisection needs about 50 steps to reach double precision over this bracket. Newton with
        // analytic vega should need single digits on a well-behaved contract.
        var x = OptionModel.BlackScholes(100, 100, 1.0, 0.25, 0.05, OptionRight.Call);
        var result = ImpliedVolatility.Solve(P(x), x with { Volatility = 0 })!;

        result.Iterations.Should().BeLessThan(10);
        result.PriceError.Should().BeLessThan(1e-10);
        result.UsedBisectionFallback.Should().BeFalse();
    }

    [Fact]
    public void VegaAtSolution_FlagsAnUnrecoverableVolatility()
    {
        // A deep in-the-money put a week from expiry is pure intrinsic: its price is flat in
        // volatility to the last representable digit, so a "converged" solve carries no
        // information. The library reports the conditioning rather than imposing a cutoff -- the
        // caller decides what is usable.
        var x = OptionModel.BlackScholes(60, 100, 0.02, 0.2, 0.02, OptionRight.Put);

        double atLowVol = P(x with { Volatility = 0.05 });
        double atHighVol = P(x with { Volatility = 0.40 });
        atHighVol.Should().Be(atLowVol, "the price is identical across an eightfold change in volatility");

        var result = ImpliedVolatility.Solve(P(x), x with { Volatility = 0 })!;

        result.PriceError.Should().BeLessThan(1e-9, "the solve itself is perfect...");
        result.VegaAtSolution.Should().BeLessThan(1e-30, "...and the answer is still meaningless");
    }

    [Fact]
    public void APriceOutsideTheNoArbitrageBoundsHasNoImpliedVolatility()
    {
        var call = OptionModel.BlackScholes(100, 100, 1.0, 0.2, 0.05, OptionRight.Call);

        ImpliedVolatility.Solve(0.001, call).Should().BeNull("below any achievable price");
        ImpliedVolatility.Solve(500.0, call).Should().BeNull("above the underlying itself");
    }

    [Fact]
    public void AnExpiredOptionHasNoImpliedVolatility()
    {
        var expired = OptionModel.BlackScholes(110, 100, 0.0, 0.2, 0.05, OptionRight.Call);

        ImpliedVolatility.Solve(10.0, expired)
            .Should().BeNull("with no time left, every volatility gives the same price");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1e-10)]
    public void ANonPositiveToleranceIsRefused(double tolerance)
    {
        var x = OptionModel.BlackScholes(100, 100, 1.0, 0.2, 0.05, OptionRight.Call);

        ImpliedVolatility.Solve(P(x), x, tolerance).Should().BeNull();
    }
}

public class PutCallParityTests
{
    [Fact]
    public void PutFromCall_ReproducesTheModelPrice()
    {
        var call = OptionModel.Merton(105, 100, 0.75, 0.22, 0.04, 0.015, OptionRight.Call);
        double callPrice = BlackScholesMerton.Price(call)!.Value;
        double modelPut = BlackScholesMerton.Price(call with { Right = OptionRight.Put })!.Value;

        PutCallParity.PutFromCall(callPrice, call)!.Value.Should().BeApproximately(modelPut, 1e-12);
        PutCallParity.CallFromPut(modelPut, call)!.Value.Should().BeApproximately(callPrice, 1e-12);
    }

    [Fact]
    public void Violation_IsZeroForConsistentPricesAndNonZeroOtherwise()
    {
        var x = OptionModel.BlackScholes(100, 95, 0.5, 0.3, 0.03, OptionRight.Call);
        double call = BlackScholesMerton.Price(x)!.Value;
        double put = BlackScholesMerton.Price(x with { Right = OptionRight.Put })!.Value;

        PutCallParity.Violation(call, put, x)!.Value.Should().BeApproximately(0.0, 1e-12);
        PutCallParity.Violation(call + 1.0, put, x)!.Value.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void ParityIsZeroForAnAtTheMoneyFuturesOption()
    {
        // With zero carry and strike equal to the futures price, a call and a put are worth
        // exactly the same. A useful sanity anchor, and the case where a relative error measure
        // would divide by zero.
        var x = OptionModel.Black76(100, 100, 2.0, 0.3, 0.05, OptionRight.Call);

        PutCallParity.ForwardMinusStrike(x)!.Value.Should().BeApproximately(0.0, 1e-13);
        BlackScholesMerton.Price(x)!.Value
            .Should().BeApproximately(BlackScholesMerton.Price(x with { Right = OptionRight.Put })!.Value, 1e-13);
    }
}

public class GreekConventionTests
{
    private static readonly OptionInputs Contract =
        OptionModel.BlackScholes(100, 100, 1.0, 0.25, 0.05, OptionRight.Call);

    [Fact]
    public void RawConventionsLeaveTheDerivativesUntouched()
    {
        var g = BlackScholesMerton.ComputeGreeks(Contract)!;

        g.Scaled(GreekConventions.Raw).Should().BeEquivalentTo(g);
    }

    [Fact]
    public void DefaultConventionsMatchWhatTradingPlatformsQuote()
    {
        var raw = BlackScholesMerton.ComputeGreeks(Contract)!;
        var shown = raw.Scaled();

        shown.Theta.Should().BeApproximately(raw.Theta / 365.0, 1e-15, "theta per calendar day");
        shown.Vega.Should().BeApproximately(raw.Vega / 100.0, 1e-15, "vega per volatility point");
        shown.RhoCarryTracksRate.Should().BeApproximately(raw.RhoCarryTracksRate / 100.0, 1e-15, "rho per rate point");

        shown.Price.Should().Be(raw.Price, "price is not a derivative");
        shown.Delta.Should().Be(raw.Delta, "delta is already dimensionless");
        shown.Gamma.Should().Be(raw.Gamma);
    }

    [Fact]
    public void VolgaScalesByTheSquare_BecauseItIsASecondDerivativeInVolatility()
    {
        var raw = BlackScholesMerton.ComputeGreeks(Contract)!;

        raw.Scaled().Volga.Should().BeApproximately(raw.Volga / 10_000.0, 1e-15);
    }

    [Fact]
    public void TradingDayThetaIsLargerThanCalendarDayTheta()
    {
        var raw = BlackScholesMerton.ComputeGreeks(Contract)!;

        double calendar = raw.Scaled(new GreekConventions(DaysPerYear: 365)).Theta;
        double trading = raw.Scaled(new GreekConventions(DaysPerYear: 252)).Theta;

        Math.Abs(trading).Should().BeGreaterThan(Math.Abs(calendar),
            "the same annual decay spread over fewer days is more per day");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-365)]
    [InlineData(double.NaN)]
    public void AnInvalidConventionThrowsRatherThanProducingInfinity(double daysPerYear)
    {
        var g = BlackScholesMerton.ComputeGreeks(Contract)!;
        Action act = () => g.Scaled(new GreekConventions(DaysPerYear: daysPerYear));

        act.Should().Throw<ArgumentException>().WithMessage("*DaysPerYear*");
    }
}

public class DegenerateInputTests
{
    [Fact]
    public void AtExpiryPricesCollapseToIntrinsicAndGreeksToTheirLimits()
    {
        var itmCall = OptionModel.BlackScholes(110, 100, 0.0, 0.2, 0.05, OptionRight.Call);

        BlackScholesMerton.Price(itmCall)!.Value.Should().BeApproximately(10.0, 1e-12);
        BlackScholesMerton.Delta(itmCall)!.Value.Should().BeApproximately(1.0, 1e-12);
        BlackScholesMerton.Gamma(itmCall)!.Value.Should().Be(0.0);
        BlackScholesMerton.Vega(itmCall)!.Value.Should().Be(0.0);

        var otmCall = OptionModel.BlackScholes(90, 100, 0.0, 0.2, 0.05, OptionRight.Call);
        BlackScholesMerton.Price(otmCall)!.Value.Should().Be(0.0);
        BlackScholesMerton.Delta(otmCall)!.Value.Should().Be(0.0);
    }

    [Fact]
    public void ZeroVolatilityGivesDiscountedForwardIntrinsic_NotNaN()
    {
        // An unguarded implementation divides by sigma*sqrt(T) here and returns NaN. Note it is
        // the FORWARD intrinsic, not the spot intrinsic -- using spot would break parity.
        var x = OptionModel.Merton(110, 100, 1.0, 0.0, 0.05, 0.02, OptionRight.Call);

        double expected = Math.Exp(-0.05) * Math.Max(0.0, 110 * Math.Exp(0.03) - 100);
        BlackScholesMerton.Price(x)!.Value.Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void ZeroVolatilityStillSatisfiesPutCallParity()
    {
        var x = OptionModel.Merton(110, 100, 1.0, 0.0, 0.05, 0.02, OptionRight.Call);
        double call = BlackScholesMerton.Price(x)!.Value;
        double put = BlackScholesMerton.Price(x with { Right = OptionRight.Put })!.Value;

        (call - put).Should().BeApproximately(PutCallParity.ForwardMinusStrike(x)!.Value, 1e-12);
    }

    [Theory]
    [InlineData(-100, 100, 1, 0.2)]
    [InlineData(100, -100, 1, 0.2)]
    [InlineData(100, 100, -1, 0.2)]
    [InlineData(100, 100, 1, -0.2)]
    [InlineData(double.NaN, 100, 1, 0.2)]
    [InlineData(100, 100, double.PositiveInfinity, 0.2)]
    public void InvalidInputsReturnNullRatherThanNaN(double s, double k, double t, double v)
    {
        var x = OptionModel.BlackScholes(s, k, t, v, 0.05, OptionRight.Call);

        BlackScholesMerton.Price(x).Should().BeNull();
        BlackScholesMerton.ComputeGreeks(x).Should().BeNull();
    }

    [Fact]
    public void ANonFiniteRateReturnsNull()
    {
        var x = OptionModel.BlackScholes(100, 100, 1, 0.2, double.NaN, OptionRight.Call);

        BlackScholesMerton.Price(x).Should().BeNull();
    }

    [Fact]
    public void NegativeRatesArePricedRatherThanRefused()
    {
        // Negative policy rates existed for a decade; a library that refuses them is refusing
        // reality, not protecting the caller.
        var x = OptionModel.BlackScholes(100, 100, 1.0, 0.2, -0.005, OptionRight.Call);

        BlackScholesMerton.Price(x)!.Value.Should().BeGreaterThan(0.0);
    }
}
