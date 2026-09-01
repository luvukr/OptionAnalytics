namespace OptionAnalytics;

/// <summary>
/// Risk-neutral probabilities implied by the option-pricing model — what the market's own prices
/// say about how likely an outcome is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number here is a RISK-NEUTRAL probability, not a real-world one.</b> The distinction
/// is not academic. Risk-neutral probabilities are the ones consistent with the prices at which
/// options actually trade, which is exactly what you want when asking "what is this position worth"
/// or "what does the market think". They are NOT the frequencies you would observe: because the
/// underlying's real drift is generally above the risk-free rate and because index options carry a
/// persistent volatility risk premium, the risk-neutral measure systematically overstates the
/// chance of a downside outcome. Using these figures to argue that a short put is a good bet is
/// circular — they were derived from that put's own price.
/// </para>
/// <para>
/// <b>Delta is not the probability of finishing in the money, though it is close.</b> Delta is
/// <c>N(d1)</c>; the probability of expiring in the money is <c>N(d2)</c>, and <c>d2</c> is
/// <c>d1</c> less <c>sigma * sqrt(T)</c>. The approximation is widely taught — Zerodha Varsity
/// among others presents delta this way — and it is a good working rule for short-dated near-the-
/// money contracts, where the gap is small. It widens with time to expiry and with volatility,
/// exactly where an options seller is most likely to be leaning on it. See
/// <see cref="DeltaVersusProbabilityGap"/>, which returns the size of the error rather than
/// asserting it is negligible.
/// </para>
/// </remarks>
public static class OptionProbability
{
    /// <summary>
    /// The risk-neutral probability that the option expires in the money — <c>N(d2)</c> for a
    /// call, <c>N(-d2)</c> for a put.
    /// </summary>
    /// <remarks>
    /// "In the money" here means the terminal price finishes beyond the strike, however
    /// marginally. It is not the probability the trade is profitable: a long option that expires
    /// one point in the money still loses the premium paid. For that, use
    /// <see cref="ProbabilityAbove"/> against the breakeven price rather than the strike.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>
    /// A probability in [0, 1], or <c>null</c> if the inputs are invalid. With no time or no
    /// volatility left the answer collapses to 0 or 1 according to whether the forward is beyond
    /// the strike.
    /// </returns>
    public static double? InTheMoneyAtExpiry(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        if (inputs.TimeToExpiry <= 0 || inputs.Volatility <= 0)
        {
            double gap = inputs.Forward - inputs.Strike;
            if (gap == 0) return 0.0;
            return (inputs.Right == OptionRight.Call ? gap > 0 : gap < 0) ? 1.0 : 0.0;
        }

        double d2 = D2(inputs);
        return NormalDistribution.Cdf(inputs.Sign * d2);
    }

    /// <summary>
    /// The risk-neutral probability that the underlying finishes ABOVE a given level at expiry.
    /// </summary>
    /// <remarks>
    /// The general form behind <see cref="InTheMoneyAtExpiry"/>, and the one to use for a
    /// breakeven rather than a strike. For a long call bought at premium <c>p</c> the breakeven is
    /// <c>strike + p</c>, and the probability of finishing above THAT is the honest answer to
    /// "how often does this trade make money".
    /// </remarks>
    /// <param name="inputs">The contract and market state; <c>Strike</c> and <c>Right</c> are ignored.</param>
    /// <param name="level">The price level to finish above. Must be greater than zero.</param>
    /// <returns>A probability in [0, 1], or <c>null</c> on invalid input.</returns>
    public static double? ProbabilityAbove(OptionInputs inputs, double level)
    {
        if (inputs is null || !inputs.IsValid) return null;
        if (!double.IsFinite(level) || level <= 0) return null;

        if (inputs.TimeToExpiry <= 0 || inputs.Volatility <= 0)
            return inputs.Forward > level ? 1.0 : 0.0;

        double d2 = D2(inputs with { Strike = level });
        return NormalDistribution.Cdf(d2);
    }

    /// <summary>
    /// The risk-neutral probability that the underlying finishes BELOW a given level at expiry.
    /// </summary>
    /// <remarks>
    /// <b>Not simply one minus <see cref="ProbabilityAbove"/>.</b> Wherever the distribution is
    /// continuous the two do sum to one, because finishing exactly at the level has probability
    /// zero. In the degenerate case they do not: with no time or no volatility left the terminal
    /// price is a single known point, and if that point IS the level then the underlying finishes
    /// neither above nor below it. Deriving this as <c>1 - Above</c> turned that atom into
    /// certainty and reported a probability of 1 for finishing below a level the price is sitting
    /// exactly on — while <see cref="InTheMoneyAtExpiry"/> correctly reported 0 for the
    /// corresponding put. Strict inequalities on both sides keep the two consistent.
    /// </remarks>
    /// <param name="inputs">The contract and market state; <c>Strike</c> and <c>Right</c> are ignored.</param>
    /// <param name="level">The price level to finish below. Must be greater than zero.</param>
    /// <returns>A probability in [0, 1], or <c>null</c> on invalid input.</returns>
    public static double? ProbabilityBelow(OptionInputs inputs, double level)
    {
        if (inputs is null || !inputs.IsValid) return null;
        if (!double.IsFinite(level) || level <= 0) return null;

        if (inputs.TimeToExpiry <= 0 || inputs.Volatility <= 0)
            return inputs.Forward < level ? 1.0 : 0.0;

        double? above = ProbabilityAbove(inputs, level);
        return above is null ? null : 1.0 - above.Value;
    }

    /// <summary>
    /// The risk-neutral probability that the underlying TOUCHES a level at any point before
    /// expiry, rather than merely finishing beyond it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roughly twice the probability of finishing beyond it, and that factor of two is the point.
    /// A stop placed where price has a 15% chance of closing through it has closer to a 30% chance
    /// of being hit intraday at some point first — which is why stops set from an
    /// expiry-distribution look far safer than they behave. Anyone sizing a stop from
    /// <see cref="ExpectedMove.Range"/> alone is reading the wrong number.
    /// </para>
    /// <para>
    /// Derived from the reflection principle for Brownian motion with drift. It assumes continuous
    /// monitoring, so it OVERSTATES the chance of a touch for anyone who only checks at the close,
    /// and it takes no account of gaps, which understate it in the other direction.
    /// </para>
    /// </remarks>
    /// <param name="inputs">The contract and market state; <c>Strike</c> and <c>Right</c> are ignored.</param>
    /// <param name="barrier">The level to touch. Must be greater than zero.</param>
    /// <returns>
    /// A probability in [0, 1], or <c>null</c> on invalid input. Returns 1 if the barrier is
    /// already touched — that is, if it sits at the current spot.
    /// </returns>
    public static double? ProbabilityOfTouching(OptionInputs inputs, double barrier)
    {
        if (inputs is null || !inputs.IsValid) return null;
        if (!double.IsFinite(barrier) || barrier <= 0) return null;

        if (barrier == inputs.Spot) return 1.0;

        if (inputs.TimeToExpiry <= 0 || inputs.Volatility <= 0)
        {
            // No time and no diffusion: the only reachable point is the forward.
            bool up = barrier > inputs.Spot;
            return (up ? inputs.Forward >= barrier : inputs.Forward <= barrier) ? 1.0 : 0.0;
        }

        double sigma = inputs.Volatility;
        double t = inputs.TimeToExpiry;
        double sigmaRootT = sigma * Math.Sqrt(t);
        double sigmaSquared = sigma * sigma;

        // A volatility small enough that its square underflows leaves no diffusion to speak of,
        // and would divide by zero below. Answer it deterministically rather than arithmetically.
        if (sigmaRootT <= 0 || sigmaSquared <= 0)
        {
            bool rising = barrier > inputs.Spot;
            return (rising ? inputs.Forward >= barrier : inputs.Forward <= barrier) ? 1.0 : 0.0;
        }

        // Drift of log-price under the pricing measure.
        double mu = inputs.CostOfCarry - 0.5 * sigmaSquared;
        double logDistance = Math.Log(barrier / inputs.Spot);

        // Reflection principle: P(max over [0,T] >= b) for arithmetic BM with drift mu.
        // For a barrier below spot the same formula applies to the mirrored process.
        double sign = logDistance > 0 ? 1.0 : -1.0;
        double a = Math.Abs(logDistance);

        double first = NormalDistribution.Cdf((-a + sign * mu * t) / sigmaRootT);

        // The second term is exp(k) * N(z), a product that is mathematically at most 1 -- but
        // evaluating it that way overflows. With a low volatility and a strong drift, k passes 709
        // and exp(k) becomes +Infinity while N(z) has already underflowed to exactly zero, and
        // Infinity * 0 is NaN. That NaN then survives Math.Clamp and propagates silently through
        // every downstream calculation, which is a far worse failure than any wrong number. So the
        // product is formed in log space, where both factors stay representable.
        double k = 2.0 * sign * mu * a / sigmaSquared;
        double z = (-a - sign * mu * t) / sigmaRootT;
        double second = Math.Exp(k + LogCdf(z));

        // The clamp absorbs floating-point rounding at the boundaries only. It is not load-bearing:
        // the sum is a probability by construction, so a clamp that visibly fires would mean the
        // formula above is wrong, not that the clamp is doing its job.
        return Math.Clamp(first + second, 0.0, 1.0);
    }

    /// <summary>
    /// <c>log(N(z))</c>, remaining finite where <c>N(z)</c> itself underflows to zero.
    /// </summary>
    /// <remarks>
    /// Below roughly <c>z = -38</c> the normal CDF is smaller than the smallest positive double, so
    /// <c>Math.Log(Cdf(z))</c> returns negative infinity and loses the magnitude entirely. The
    /// standard asymptotic expansion keeps it: <c>log N(z) = -z^2/2 - log(-z) - log(sqrt(2*pi))</c>
    /// with relative error of order <c>1/z^2</c>, which is ample precision when the alternative is
    /// no value at all.
    /// </remarks>
    private static double LogCdf(double z)
    {
        double cdf = NormalDistribution.Cdf(z);
        if (cdf > 0) return Math.Log(cdf);

        return -0.5 * z * z - Math.Log(-z) - 0.5 * Math.Log(2.0 * Math.PI);
    }

    /// <summary>
    /// How far the widely used "delta equals probability of expiring in the money" rule is off, for
    /// this contract.
    /// </summary>
    /// <remarks>
    /// Returns <c>|delta| - P(in the money)</c>, which is always non-negative for a call and
    /// reported as a magnitude here. The rule is a good one for short-dated near-the-money
    /// options and degrades with time and volatility — this returns the number rather than a
    /// verdict, so a caller can decide whether it matters at their horizon.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>The absolute gap, or <c>null</c> if the inputs are invalid.</returns>
    public static double? DeltaVersusProbabilityGap(OptionInputs inputs)
    {
        double? delta = BlackScholesMerton.Delta(inputs);
        double? itm = InTheMoneyAtExpiry(inputs);

        if (delta is null || itm is null) return null;

        // Delta carries the carry discount factor; strip it so the comparison is like for like.
        double carryDiscount = Math.Exp((inputs.CostOfCarry - inputs.RiskFreeRate) * inputs.TimeToExpiry);
        double undiscountedDelta = Math.Abs(delta.Value) / carryDiscount;

        return Math.Abs(undiscountedDelta - itm.Value);
    }

    private static double D2(OptionInputs i)
    {
        double sigmaRootT = i.Volatility * Math.Sqrt(i.TimeToExpiry);
        double d1 = (Math.Log(i.Spot / i.Strike)
                     + (i.CostOfCarry + 0.5 * i.Volatility * i.Volatility) * i.TimeToExpiry) / sigmaRootT;
        return d1 - sigmaRootT;
    }
}
