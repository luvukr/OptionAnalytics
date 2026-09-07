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
/// A quoted option's volatility seen from both sides of the spread.
/// </summary>
/// <remarks>
/// <para>
/// <b>The width of this bracket, not the width of the price spread, is what tells you whether a
/// strike is usable.</b> Two strikes quoted a rupee wide are not equally tradeable: a rupee on an
/// at-the-money contract is a fraction of a volatility point, and the same rupee on a far wing is
/// tens of points, because vega has collapsed and price barely responds to volatility at all. A
/// price spread is denominated in the wrong unit for the decision.
/// </para>
/// <para>
/// Each side is a full <see cref="ImpliedVolatilityResult"/> rather than a bare number, so
/// <see cref="ImpliedVolatilityResult.Converged"/> and
/// <see cref="ImpliedVolatilityResult.VegaAtSolution"/> survive per side. Sides fail independently
/// and routinely: a zero bid is ordinary in the wings and has no implied volatility, while the ask
/// on the same contract solves perfectly.
/// </para>
/// </remarks>
/// <param name="Bid">
/// The volatility that reproduces the bid, or <c>null</c> if no volatility does — the bid sits
/// outside the model's achievable range.
/// </param>
/// <param name="Ask">The volatility that reproduces the ask, or <c>null</c> on the same grounds.</param>
/// <param name="Mid">
/// The volatility at the mid price. SOLVED at the mid, not averaged from the two sides: price is
/// non-linear in volatility, so the volatility at the mid price is not the mid of the two
/// volatilities. The gap is small — measured at 0.002 volatility points on an at-the-money quote
/// eight points wide, rising to 0.145 on a wing — so this is not a correction anyone needs for
/// accuracy. It is simply the only one of the two that reprices the mid, which is what a mid
/// volatility is for.
/// </param>
/// <param name="MidPrice">The mid price that <paramref name="Mid"/> was solved against.</param>
public sealed record BidAskVolatility(
    ImpliedVolatilityResult? Bid,
    ImpliedVolatilityResult? Ask,
    ImpliedVolatilityResult? Mid,
    double MidPrice)
{
    /// <summary>
    /// <c>Ask volatility - Bid volatility</c>: the quote's width expressed in the unit the decision
    /// is actually made in.
    /// </summary>
    /// <remarks>
    /// As a decimal, so multiply by 100 for volatility points. Non-negative whenever the quote is
    /// not crossed, because price rises monotonically with volatility.
    /// </remarks>
    /// <returns>The width, or <c>null</c> if either side has no implied volatility.</returns>
    public double? VolatilitySpread => Bid is null || Ask is null
        ? null
        : Ask.Volatility - Bid.Volatility;

    /// <summary>
    /// True when both sides of the quote correspond to a real volatility that the solver actually
    /// reached.
    /// </summary>
    /// <remarks>
    /// The precondition for treating a strike as priceable at all. False means at least one side is
    /// outside the achievable range or was clamped at a search bound — in which case
    /// <see cref="Mid"/> may still return a number, and that number is not a measurement.
    /// </remarks>
    public bool IsResolvable => Bid is { Converged: true } && Ask is { Converged: true };
}

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

    /// <summary>
    /// Back-solves implied volatility from both sides of a quote, and from the mid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not just solve the mid.</b> On an illiquid strike the mid is frequently not a price
    /// any volatility can produce — it sits below intrinsic, or below the model's floor — and
    /// <see cref="Solve"/> correctly refuses it. Handed a whole chain, that refusal is common rather
    /// than exceptional: measured on a 1,000-strike NIFTY chain a week from expiry, <b>13% of
    /// strikes had no resolvable volatility at all</b>. One strike in eight is not an edge case, and
    /// a caller who only ever solves the mid learns nothing about which eighth.
    /// </para>
    /// <para>
    /// Solving both sides answers the question the mid cannot: not "what is the volatility" but
    /// "how much volatility does this quote fail to pin down". See
    /// <see cref="BidAskVolatility.VolatilitySpread"/>.
    /// </para>
    /// <para>
    /// <b>Sides fail independently and that is reported, not smoothed over.</b> A zero bid — routine
    /// in the wings — has no implied volatility, while the ask beside it solves to machine
    /// precision. Nothing here substitutes a floor, a nearest bound, or the other side's answer for
    /// a side that cannot be solved: it returns <c>null</c> for that side, because a fabricated
    /// volatility on the bid is how a strike that cannot be sold gets quoted as though it can.
    /// </para>
    /// </remarks>
    /// <param name="bid">The bid price. May be zero; may equal <paramref name="ask"/>.</param>
    /// <param name="ask">The ask price. Must not be below <paramref name="bid"/>.</param>
    /// <param name="inputs">
    /// The contract and market state. <see cref="OptionInputs.Volatility"/> is ignored — it is the
    /// unknown.
    /// </param>
    /// <param name="tolerance">Absolute price tolerance, as for <see cref="Solve"/>.</param>
    /// <param name="maxIterations">Hard cap on solver steps per side.</param>
    /// <returns>
    /// The three solves, or <c>null</c> if the contract or market state is unusable, if a solver
    /// setting is unusable, if there is no time left to invert, or if the quote is CROSSED. All
    /// three sides being <c>null</c> INSIDE a returned result means the market does not price this
    /// strike; a <c>null</c> result means the call itself was not answerable, and the two must not
    /// be confusable. A crossed quote is refused rather than
    /// reported because the entire meaning of the result is a bracket, and a bid above an ask does
    /// not bracket anything — it means one side is stale or the feed is wrong, which is a data
    /// problem and not a market condition.
    /// </returns>
    public static BidAskVolatility? SolveBidAsk(
        double bid,
        double ask,
        OptionInputs inputs,
        double tolerance = 1e-10,
        int maxIterations = 100)
    {
        if (inputs is null) return null;
        if (!double.IsFinite(bid) || !double.IsFinite(ask)) return null;
        if (bid < 0 || ask < bid) return null;

        // Solver settings are configuration, not market data, so an unusable one is a refusal here
        // rather than three per-side nulls further down. Without this check a caller who passed
        // tolerance: 0 got a result whose every side was null -- indistinguishable from a strike the
        // market genuinely does not price, which turns a programmer error into a false market
        // observation.
        if (!double.IsFinite(tolerance) || tolerance <= 0 || maxIterations <= 0) return null;

        // Validate everything but the unknown, so an unusable contract is one refusal rather than
        // three coincidental ones.
        if (!(inputs with { Volatility = 1.0 }).IsValid) return null;
        if (inputs.TimeToExpiry <= 0) return null;

        double mid = 0.5 * (bid + ask);

        return new BidAskVolatility(
            Bid: Solve(bid, inputs, tolerance, maxIterations),
            Ask: Solve(ask, inputs, tolerance, maxIterations),
            Mid: Solve(mid, inputs, tolerance, maxIterations),
            MidPrice: mid);
    }
}
