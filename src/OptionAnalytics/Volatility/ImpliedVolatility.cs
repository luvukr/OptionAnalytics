namespace OptionAnalytics;

/// <summary>The outcome of a back-solve, including how it was reached.</summary>
/// <param name="Volatility">The volatility that reproduces the observed price.</param>
/// <param name="Iterations">How many solver steps were taken.</param>
/// <param name="PriceError">
/// <c>|model price at Volatility - observed price|</c>. The honest measure of whether the answer
/// is any good — a converged solver leaves this at machine noise.
/// </param>
/// <param name="UsedBisectionFallback">
/// True if Newton's method was abandoned and the bracketed fallback finished the job. Not a
/// failure, but worth surfacing: it means the price sat somewhere Newton behaves badly, which is
/// usually deep in a wing or very close to expiry.
/// </param>
/// <param name="VegaAtSolution">
/// The option's vega at <paramref name="Volatility"/> — how much the price moves per unit of
/// volatility, right where the answer was found.
/// <para>
/// <b>Read this before trusting the volatility.</b> A converged solve says only that the model
/// reproduces the price; it says nothing about whether the answer is unique. Where vega is
/// vanishingly small — deep in the money, or a whisker from expiry — a wide range of volatilities
/// produces the same price to the last representable digit, so the number returned is one of many
/// and carries no information. A measured example: a 40-point in-the-money put a week from expiry
/// prices identically at 5% and at 40% volatility, with vega of 8e-71 and
/// <see cref="PriceError"/> of exactly zero. The solve is perfect and the volatility is
/// meaningless.
/// </para>
/// <para>
/// This library deliberately reports the conditioning rather than imposing a cutoff. Where the
/// threshold should sit depends on the price scale, the tick size and what the number is for —
/// which the caller knows and this library does not. A rough rule: if a one-tick change in the
/// observed price would move the implied volatility by more than you care about
/// (<c>tick / VegaAtSolution</c>), the answer is not resolvable at that tick.
/// </para>
/// </param>
/// <param name="Converged">
/// True only if the solve actually reached the requested tolerance.
/// <para>
/// <b>Check this first.</b> The other fields all report something, but each reports a different
/// failure and none of them is a plain verdict — which is a trap. A price at or beyond the
/// achievable range returns the clamped bound with <see cref="PriceError"/> of exactly zero and
/// <see cref="Iterations"/> of zero, so the two fields a caller is most likely to read say the
/// solve was perfect when in fact no solving happened. Exhausting the iteration cap returns the
/// last iterate, which is usually close and occasionally not. This flag is false in every such
/// case.
/// </para>
/// </param>
public sealed record ImpliedVolatilityResult(
    double Volatility,
    int Iterations,
    double PriceError,
    bool UsedBisectionFallback,
    double VegaAtSolution,
    bool Converged);

/// <summary>
/// Back-solves the volatility that reproduces an observed option price.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not just bisection.</b> Price is monotonic in volatility, so bisection always
/// works — and converges linearly, gaining one bit per step, so reaching double precision takes
/// around fifty iterations and reaching a merely usable answer still takes dozens. Newton's method
/// uses vega, the exact analytic derivative this library already has, and converges quadratically:
/// the error squares each step, so four or five steps is typical. The cost is that Newton can
/// wander when vega is tiny, which is exactly what happens deep in the wings and near expiry.
/// </para>
/// <para>
/// <b>So this does both.</b> Newton runs inside a maintained bracket. Every step that leaves the
/// bracket, or fails to reduce the error, is rejected and replaced by a bisection step. That gives
/// Newton's speed where the function is well behaved and bisection's unconditional convergence
/// where it is not — the answer is never worse than bisection alone, and is usually an order of
/// magnitude cheaper.
/// </para>
/// <para>
/// <b>On the state of the art.</b> Jäckel's "Let's Be Rational" (Wilmott, 2015) reaches full
/// machine precision in at most two Householder steps for every input, by way of four rational
/// branches in log-moneyness. It is materially better than this and materially more complicated,
/// and it is the natural upgrade if profiling ever shows this to be the bottleneck. The
/// arrangement here is deliberate: the interface returns <see cref="ImpliedVolatilityResult"/>
/// rather than a bare double, so a future solver can be swapped in without changing any caller.
/// </para>
/// <para>
/// <b>A price outside the no-arbitrage bounds has no implied volatility at all.</b> Below
/// intrinsic value or above the underlying's discounted value, no volatility reproduces it — such
/// a print is stale, crossed, or mismarked. This returns <c>null</c> rather than the nearest
/// bound, because silently returning the floor volatility for a bad print is how a bad print
/// becomes a trade.
/// </para>
/// </remarks>
public static class ImpliedVolatility
{
    /// <summary>Lowest volatility considered. Below this an option is effectively a forward.</summary>
    public const double MinVolatility = 1e-6;

    /// <summary>Highest volatility considered. Above this, quotes are not describing a tradeable market.</summary>
    public const double MaxVolatility = 10.0;

    /// <summary>
    /// Back-solves implied volatility from an observed price.
    /// </summary>
    /// <param name="observedPrice">The market price of the option.</param>
    /// <param name="inputs">
    /// The contract and market state. <see cref="OptionInputs.Volatility"/> is ignored — it is the
    /// unknown being solved for — so any value may be passed, including zero.
    /// </param>
    /// <param name="tolerance">
    /// Absolute price tolerance at which to stop. Defaults to <c>1e-10</c>, which is far tighter
    /// than any real quote justifies but costs almost nothing given quadratic convergence.
    /// </param>
    /// <param name="maxIterations">Hard cap on solver steps.</param>
    /// <returns>
    /// The result, or <c>null</c> if the inputs are invalid, the option has no time value left to
    /// solve against, or the price lies outside the no-arbitrage bounds.
    /// </returns>
    public static ImpliedVolatilityResult? Solve(
        double observedPrice,
        OptionInputs inputs,
        double tolerance = 1e-10,
        int maxIterations = 100)
    {
        if (inputs is null || !double.IsFinite(observedPrice) || !double.IsFinite(tolerance)) return null;
        if (tolerance <= 0 || maxIterations <= 0) return null;

        // Volatility is the unknown; validate everything else through a probe value.
        var probe = inputs with { Volatility = 1.0 };
        if (!probe.IsValid) return null;

        // With no time left there is no time value to invert -- price is intrinsic for every
        // volatility, so the problem is not merely hard, it is undefined.
        if (inputs.TimeToExpiry <= 0) return null;

        double discount = Math.Exp(-inputs.RiskFreeRate * inputs.TimeToExpiry);
        double lowerBound = BlackScholesMerton.Price(inputs with { Volatility = MinVolatility })!.Value;
        double upperBound = BlackScholesMerton.Price(inputs with { Volatility = MaxVolatility })!.Value;

        // Outside the achievable range no volatility reproduces the price. Allow a hair of slack
        // for the caller's own rounding at the boundary.
        double slack = Math.Max(1e-12, tolerance);
        if (observedPrice < lowerBound - slack || observedPrice > upperBound + slack) return null;

        // At or past a bound, the bound IS the answer -- but no solving happened, so this is not a
        // converged solve however small the price error looks.
        if (observedPrice <= lowerBound)
            return Finish(inputs, MinVolatility, 0, Math.Abs(observedPrice - lowerBound), false, converged: false);
        if (observedPrice >= upperBound)
            return Finish(inputs, MaxVolatility, 0, Math.Abs(observedPrice - upperBound), false, converged: false);

        double low = MinVolatility, high = MaxVolatility;
        double sigma = InitialGuess(observedPrice, inputs, discount);
        bool usedFallback = false;

        for (int i = 1; i <= maxIterations; i++)
        {
            double price = BlackScholesMerton.Price(inputs with { Volatility = sigma })!.Value;
            double error = price - observedPrice;

            if (Math.Abs(error) < tolerance)
                return Finish(inputs, sigma, i, Math.Abs(error), usedFallback, converged: true);

            // Maintain the bracket from the sign of the error: price rises with volatility.
            if (error > 0) high = sigma; else low = sigma;

            double vega = BlackScholesMerton.Vega(inputs with { Volatility = sigma })!.Value;
            double next = vega > 1e-12 ? sigma - error / vega : double.NaN;

            // Reject a Newton step that escapes the bracket or is not finite -- that is precisely
            // the low-vega wandering this guards against -- and bisect instead.
            if (!double.IsFinite(next) || next <= low || next >= high)
            {
                next = 0.5 * (low + high);
                usedFallback = true;
            }

            // Converged in the argument even if the price residual is still above tolerance,
            // which happens when vega is so small that price barely responds to volatility.
            // Converged in the argument but possibly not in the price: report honestly on which.
            if (Math.Abs(next - sigma) < 1e-14)
                return Finish(inputs, next, i, Math.Abs(error), usedFallback,
                    converged: Math.Abs(error) < tolerance);

            sigma = next;
        }

        double finalError = Math.Abs(BlackScholesMerton.Price(inputs with { Volatility = sigma })!.Value - observedPrice);
        return Finish(inputs, sigma, maxIterations, finalError, usedFallback,
            converged: finalError < tolerance);
    }

    /// <summary>Builds the result, attaching vega at the solution so the caller can judge conditioning.</summary>
    private static ImpliedVolatilityResult Finish(
        OptionInputs inputs, double sigma, int iterations, double priceError, bool usedFallback,
        bool converged)
        => new(sigma, iterations, priceError, usedFallback,
            BlackScholesMerton.Vega(inputs with { Volatility = sigma }) ?? 0.0,
            converged);

    /// <summary>
    /// Brenner-Subrahmanyam's at-the-money approximation, clamped into the search range.
    /// </summary>
    /// <remarks>
    /// For an at-the-money option the price is very nearly
    /// <c>0.4 * S * sigma * sqrt(T)</c>, which inverts directly. It is only accurate near the
    /// money, but it is free and it lands Newton in the right neighbourhood far more often than a
    /// fixed starting guess does; the bracket handles the cases where it does not.
    /// </remarks>
    private static double InitialGuess(double observedPrice, OptionInputs inputs, double discount)
    {
        double forward = inputs.Forward;
        double rootT = Math.Sqrt(inputs.TimeToExpiry);

        double guess = Math.Sqrt(2.0 * Math.PI / inputs.TimeToExpiry) * (observedPrice / (discount * forward));

        if (!double.IsFinite(guess) || guess <= 0)
            guess = 0.2;

        // A wildly out-of-the-money quote can make the approximation absurd; keep it inside the
        // bracket so the first Newton step starts somewhere legal.
        return Math.Clamp(guess, MinVolatility * 10, Math.Min(MaxVolatility * 0.5, 5.0)) is var g && double.IsFinite(g)
            ? g
            : 0.2;
    }
}
