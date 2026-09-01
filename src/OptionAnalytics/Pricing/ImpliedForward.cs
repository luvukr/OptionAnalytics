namespace OptionAnalytics;

/// <summary>A call and a put quoted at the same strike and expiry.</summary>
/// <remarks>
/// Use mid prices, or bids and asks from the same side consistently. A call mid paired with a stale
/// put last-trade is the commonest way to extract a carry that is confidently wrong.
/// </remarks>
/// <param name="Strike">The shared strike. Must be greater than zero.</param>
/// <param name="CallPrice">Observed call price.</param>
/// <param name="PutPrice">Observed put price at the same strike.</param>
public sealed record CallPutQuote(double Strike, double CallPrice, double PutPrice)
{
    /// <summary>True if the strike is positive and both prices are finite and non-negative.</summary>
    public bool IsValid =>
        double.IsFinite(Strike) && Strike > 0 &&
        double.IsFinite(CallPrice) && CallPrice >= 0 &&
        double.IsFinite(PutPrice) && PutPrice >= 0;

    /// <summary>The call-put spread <c>C - P</c>, which parity makes linear in the strike.</summary>
    public double Spread => CallPrice - PutPrice;
}

/// <summary>What a set of quotes says about the forward, the carry and the discount rate.</summary>
/// <param name="Forward">
/// The synthetic forward price the quotes imply. This is the price at which the market will actually
/// transact the underlying for delivery at expiry, which is not the cash index level.
/// </param>
/// <param name="CostOfCarry">
/// The carry rate <c>b = ln(Forward / Spot) / T</c>, ready to pass to
/// <see cref="OptionInputs.CostOfCarry"/>.
/// </param>
/// <param name="RiskFreeRate">
/// The discount rate. DERIVED from the quotes when <paramref name="RateWasDerived"/> is true;
/// otherwise the value the caller supplied, echoed back unchanged.
/// </param>
/// <param name="StrikesUsed">How many quote pairs contributed.</param>
/// <param name="WorstParityResidual">
/// The largest departure from the fitted line, in price units — the self-check.
/// <para>
/// <b>Zero is not always good news.</b> A straight line through one or two points fits them
/// exactly, so this is zero by construction below three strikes and means nothing there. From three
/// strikes up it is the real diagnostic: parity is a static arbitrage, so on clean simultaneous
/// quotes of European contracts every pair must sit on one line. Scatter means stale legs, crossed
/// markets, quotes sampled at different instants, or contracts that are not European.
/// </para>
/// </param>
/// <param name="RateWasDerived">
/// True when the discount rate came out of the quotes rather than being assumed.
/// </param>
public sealed record ImpliedForwardResult(
    double Forward,
    double CostOfCarry,
    double RiskFreeRate,
    int StrikesUsed,
    double WorstParityResidual,
    bool RateWasDerived)
{
    /// <summary>
    /// Returns <paramref name="inputs"/> with the carry and rate replaced by what the market implied.
    /// </summary>
    /// <remarks>
    /// The one-line bridge from quotes to pricing: extract once per expiry, then price and hedge
    /// every strike on that expiry off the result. Spot, strike, volatility and right are untouched.
    /// </remarks>
    /// <param name="inputs">The contract to re-base onto the implied carry.</param>
    /// <returns>A new <see cref="OptionInputs"/>, or <c>null</c> if <paramref name="inputs"/> is null.</returns>
    public OptionInputs? ApplyTo(OptionInputs inputs) => inputs is null
        ? null
        : inputs with { CostOfCarry = CostOfCarry, RiskFreeRate = RiskFreeRate };
}

/// <summary>
/// Extracts the forward price and cost of carry the option market is actually quoting, rather than
/// assuming them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every price in this library is driven by <see cref="OptionInputs.CostOfCarry"/>,
/// and until now that number had to come from somewhere else — a dividend-yield forecast, a
/// borrow-cost assumption, a rate from a data feed. For an index it is worse than an assumption: the
/// cash index is not a tradeable instrument, so there is no carry that makes it one. Nobody can buy
/// NIFTY. What trades is the future and the option chain, and those two agree with each other on a
/// forward that the cash index and a dividend guess will not reproduce.
/// </para>
/// <para>
/// The option market already knows the answer and states it in every call/put pair. Put-call parity
/// is a static arbitrage, not a model result, so
/// <c>C - P = exp(-rT) * (F - K)</c> holds whatever anyone believes about volatility. Rearranged, it
/// hands over the forward.
/// </para>
/// <para>
/// <b>The carry that comes back is better than the sum of its parts.</b> It absorbs the dividend
/// yield, the borrow or repo spread, the index basis and any funding skew, all at once, without any
/// of them being separately estimated. That is the whole point: those components are hard to forecast
/// individually and their total is directly observable.
/// </para>
/// <para>
/// <b>European contracts only.</b> Parity holds for European exercise. It does NOT hold for American
/// options, where the early-exercise premium sits on the put and breaks the relation — so on NSE this
/// is right for index options and wrong for single-stock options, which are American. Nothing here
/// can detect that: American quotes produce a plausible, biased carry rather than a refusal. Know
/// which you are feeding it.
/// </para>
/// </remarks>
public static class ImpliedForward
{
    /// <summary>
    /// The forward and carry implied by ONE call/put pair, given a discount rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>F = K + exp(rT) * (C - P)</c>, then <c>b = ln(F / S) / T</c>.
    /// </para>
    /// <para>
    /// <b>Use the strike where the call-put spread is SMALLEST, and note that this is not the strike
    /// nearest the spot.</b> The rate you supply enters through <c>exp(rT) * (C - P)</c>, so the
    /// error a wrong rate produces is <c>T * exp(rT) * (C - P)</c> — which vanishes as the strike
    /// approaches the FORWARD, because that is where the spread goes to zero. Since the forward is
    /// what you are trying to measure, the usable rule is the observable one: pick the pair with the
    /// smallest <see cref="CallPutQuote.Spread"/> in absolute value. No prior estimate needed.
    /// </para>
    /// <para>
    /// The distinction matters more than it sounds. On a 30-day NIFTY carrying at 5.3% the forward
    /// sits about 108 points above spot — four strikes away on a 100-point grid. Measured, with the
    /// supplied rate 200bp wrong: the spot-at-the-money strike misprices the forward by 0.178, the
    /// strike nearest the forward by 0.014, and a strike 2,800 points down by 4.78. Reaching for
    /// "at the money" and meaning spot gives away most of the benefit. That the near-forward strike
    /// also carries the tightest bid/ask is a second, independent reason to prefer it.
    /// </para>
    /// <para>
    /// <b>This form cannot check itself.</b> One pair determines one forward, and any pair whatsoever
    /// produces one, however stale or crossed. <see cref="FromChain"/> needs no rate at all and
    /// reports whether the quotes are mutually consistent; prefer it whenever more than one strike is
    /// quoted.
    /// </para>
    /// </remarks>
    /// <param name="spot">Current level of the underlying. Must be greater than zero.</param>
    /// <param name="quote">The call/put pair.</param>
    /// <param name="timeToExpiry">Time to expiry in years. Must be greater than zero.</param>
    /// <param name="riskFreeRate">The discount rate to assume. May be negative.</param>
    /// <returns>
    /// The result, or <c>null</c> if the inputs are invalid, if time has run out — with no time left
    /// the forward is the spot and the carry is unidentifiable, so a returned figure would be
    /// invented — or if the quotes imply a non-positive forward, which no carry can produce.
    /// </returns>
    public static ImpliedForwardResult? FromCallPut(
        double spot, CallPutQuote quote, double timeToExpiry, double riskFreeRate)
    {
        if (quote is null || !quote.IsValid) return null;
        if (!double.IsFinite(spot) || spot <= 0) return null;
        if (!double.IsFinite(timeToExpiry) || timeToExpiry <= 0) return null;
        if (!double.IsFinite(riskFreeRate)) return null;

        double forward = quote.Strike + Math.Exp(riskFreeRate * timeToExpiry) * quote.Spread;

        return Build(spot, forward, riskFreeRate, timeToExpiry,
            strikesUsed: 1, worstResidual: 0.0, rateWasDerived: false);
    }

    /// <summary>
    /// The forward, carry AND discount rate implied by two or more call/put pairs, with no rate
    /// assumption at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parity is linear in the strike:
    /// <c>C(K) - P(K) = exp(-rT) * F - exp(-rT) * K</c>. So the call-put spread plotted against the
    /// strike is a straight line whose slope is minus the discount factor and whose intercept is the
    /// discounted forward. Fitting that line recovers both — the rate is an OUTPUT here, not an input,
    /// which removes the last assumption from the chain.
    /// </para>
    /// <para>
    /// A least-squares fit is used, so extra strikes average away quote noise instead of compounding
    /// it, and the largest residual comes back as
    /// <see cref="ImpliedForwardResult.WorstParityResidual"/>. Below three strikes that residual is
    /// zero by construction and carries no information; at three or more it is the strongest
    /// available statement that the quotes are simultaneous, uncrossed and European.
    /// </para>
    /// <para>
    /// <b>Filtering is the caller's job.</b> An unweighted fit treats a wide illiquid wing as
    /// seriously as a tight at-the-money pair. Pass the strikes you would actually trade — typically
    /// a handful either side of the money — rather than the whole chain. This library will not guess
    /// which of your quotes are real.
    /// </para>
    /// </remarks>
    /// <param name="spot">Current level of the underlying. Must be greater than zero.</param>
    /// <param name="quotes">
    /// Call/put pairs on ONE expiry, at two or more distinct strikes. Order does not matter.
    /// </param>
    /// <param name="timeToExpiry">Time to expiry in years. Must be greater than zero.</param>
    /// <returns>
    /// The result, or <c>null</c> if there are fewer than two distinct valid strikes, if time has run
    /// out, or if the fitted line is not one parity can produce — a non-negative slope means the
    /// call-put spread does not fall as the strike rises, which no arbitrage-free market does, and a
    /// non-positive implied forward or discount factor is likewise unusable. Each of those is a
    /// refusal rather than a clamp, because the alternative is a carry that looks like a measurement.
    /// </returns>
    public static ImpliedForwardResult? FromChain(
        double spot, IReadOnlyList<CallPutQuote> quotes, double timeToExpiry)
    {
        if (!double.IsFinite(spot) || spot <= 0) return null;
        if (!double.IsFinite(timeToExpiry) || timeToExpiry <= 0) return null;
        if (quotes is null || quotes.Count < 2) return null;

        var usable = quotes.Where(q => q is not null && q.IsValid)
                           .GroupBy(q => q.Strike)
                           .Select(g => g.First())
                           .ToList();
        if (usable.Count < 2) return null;

        int n = usable.Count;
        double meanStrike = usable.Sum(q => q.Strike) / n;
        double meanSpread = usable.Sum(q => q.Spread) / n;

        double covariance = 0.0, variance = 0.0;
        foreach (var q in usable)
        {
            double dk = q.Strike - meanStrike;
            covariance += dk * (q.Spread - meanSpread);
            variance += dk * dk;
        }

        if (variance <= 0) return null;

        // Slope is minus the discount factor; intercept is the discounted forward.
        double slope = covariance / variance;
        double intercept = meanSpread - slope * meanStrike;

        // A rising call-put spread cannot come from a no-arbitrage market at one expiry.
        if (!double.IsFinite(slope) || slope >= 0) return null;

        double discountFactor = -slope;
        if (!double.IsFinite(discountFactor) || discountFactor <= 0) return null;

        double forward = intercept / discountFactor;
        double derivedRate = -Math.Log(discountFactor) / timeToExpiry;
        if (!double.IsFinite(derivedRate)) return null;

        double worstResidual = usable.Max(q => Math.Abs(q.Spread - (intercept + slope * q.Strike)));

        return Build(spot, forward, derivedRate, timeToExpiry,
            strikesUsed: n, worstResidual: worstResidual, rateWasDerived: true);
    }

    /// <summary>
    /// Turns a forward into a carry and packages the result, refusing anything a carry cannot express.
    /// </summary>
    private static ImpliedForwardResult? Build(
        double spot, double forward, double rate, double timeToExpiry,
        int strikesUsed, double worstResidual, bool rateWasDerived)
    {
        // A forward at or below zero has no logarithm and therefore no carry rate. It means the
        // quotes are not prices, not that the carry is very negative.
        if (!double.IsFinite(forward) || forward <= 0) return null;

        double carry = Math.Log(forward / spot) / timeToExpiry;
        if (!double.IsFinite(carry)) return null;

        return new ImpliedForwardResult(
            forward, carry, rate, strikesUsed, worstResidual, rateWasDerived);
    }
}
