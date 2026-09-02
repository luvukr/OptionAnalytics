namespace OptionAnalytics;

/// <summary>
/// A mark-to-market change split into the greek terms that produced it, plus what none of them
/// explains.
/// </summary>
/// <remarks>
/// Every figure is in price units, per unit of the underlying for a single contract or summed over
/// signed quantities for a position. They add up: <see cref="Explained"/> is their sum and
/// <see cref="Residual"/> is what is left of <paramref name="Actual"/> afterwards.
/// </remarks>
/// <param name="Actual">
/// The real change in value, obtained by repricing at both states rather than by summing terms. This
/// is the number the attribution has to explain, and it is exact.
/// </param>
/// <param name="DeltaPnl">
/// <c>Delta * dS</c> — the directional term. On a position sold as harvesting time or volatility,
/// this term dominating is the finding, not a detail.
/// </param>
/// <param name="GammaPnl"><c>0.5 * Gamma * dS^2</c> — always the same sign as the gamma held.</param>
/// <param name="ThetaPnl"><c>Theta * dt</c>, with <c>dt</c> the time ELAPSED in years.</param>
/// <param name="VegaPnl"><c>Vega * dSigma</c>, with the change in volatility as a decimal.</param>
/// <param name="VannaPnl"><c>Vanna * dS * dSigma</c> — spot and volatility moving together.</param>
/// <param name="VolgaPnl"><c>0.5 * Volga * dSigma^2</c> — convexity in volatility.</param>
/// <param name="CharmPnl"><c>Charm * dS * dt</c> — the drift of delta over the interval.</param>
/// <param name="RatePnl">
/// <c>dV/dr * dr</c> with the carry held fixed, which is <c>-T * V</c> exactly.
/// </param>
/// <param name="CarryPnl">
/// <c>dV/db * db</c> — the carry moving independently of the rate, which is what a dividend
/// revision or a change in the futures basis looks like.
/// </param>
public sealed record PnlAttributionResult(
    double Actual,
    double DeltaPnl,
    double GammaPnl,
    double ThetaPnl,
    double VegaPnl,
    double VannaPnl,
    double VolgaPnl,
    double CharmPnl,
    double RatePnl,
    double CarryPnl)
{
    /// <summary>The sum of every attributed term.</summary>
    /// <remarks>
    /// Computed rather than stored, so it cannot drift out of agreement with the terms it is the sum
    /// of. The same is true of <see cref="Residual"/>.
    /// </remarks>
    public double Explained =>
        DeltaPnl + GammaPnl + ThetaPnl + VegaPnl + VannaPnl + VolgaPnl + CharmPnl + RatePnl + CarryPnl;

    /// <summary>
    /// <c>Actual - Explained</c>: the part of the move no greek in the expansion accounts for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a statement about the position, not about the library.</b> The greeks here are
    /// exact analytic derivatives, verified against finite differences, put-call parity and the
    /// Black-Scholes PDE identity. A residual means the second-order expansion did not capture what
    /// happened, and the usual reasons are:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>The move was large.</b> Gamma itself changes over a big move, and the expansion assumes it
    /// does not. This is the dominant cause and it grows with the cube of the move.
    /// </description></item>
    /// <item><description>
    /// <b>The surface moved, not just the level.</b> One vega describes a parallel shift in
    /// volatility. Skew steepening or the term structure twisting is invisible to it.
    /// </description></item>
    /// <item><description>
    /// <b>Higher-order terms.</b> Speed (<c>d3V/dS3</c>) and the third-order cross terms are not in
    /// the expansion.
    /// </description></item>
    /// <item><description>
    /// <b>Marks.</b> A stale closing print on an illiquid strike produces a residual that is really
    /// a data problem.
    /// </description></item>
    /// </list>
    /// <para>
    /// Costs and slippage never appear in any term, because no greek contains them.
    /// </para>
    /// </remarks>
    public double Residual => Actual - Explained;

    /// <summary>
    /// <c>|Residual| / |Actual|</c> — how much of the move went unexplained, as a fraction.
    /// </summary>
    /// <returns>
    /// The fraction, or <c>null</c> when <see cref="Actual"/> is zero or vanishingly small, where the
    /// ratio is meaningless rather than large. Read <see cref="Residual"/> directly in that case.
    /// </returns>
    public double? ResidualShare => Math.Abs(Actual) < 1e-12
        ? null
        : Math.Abs(Residual) / Math.Abs(Actual);

    /// <summary>
    /// The largest attributed term by magnitude, named — the answer to "where did this come from".
    /// </summary>
    /// <remarks>
    /// The point of the whole class. A strategy that claims to sell time decay, whose largest term
    /// is <see cref="DeltaPnl"/>, is a directional bet wearing an options costume — and that is
    /// mechanically visible here rather than a matter of interpretation.
    /// </remarks>
    public (string Name, double Value) LargestTerm
    {
        get
        {
            var terms = new (string Name, double Value)[]
            {
                (nameof(DeltaPnl), DeltaPnl), (nameof(GammaPnl), GammaPnl),
                (nameof(ThetaPnl), ThetaPnl), (nameof(VegaPnl), VegaPnl),
                (nameof(VannaPnl), VannaPnl), (nameof(VolgaPnl), VolgaPnl),
                (nameof(CharmPnl), CharmPnl), (nameof(RatePnl), RatePnl),
                (nameof(CarryPnl), CarryPnl),
            };

            var best = terms[0];
            foreach (var t in terms)
                if (Math.Abs(t.Value) > Math.Abs(best.Value)) best = t;

            return best;
        }
    }
}

/// <summary>
/// Explains a mark-to-market change as a sum of greek contributions, and measures what is left over.
/// </summary>
/// <remarks>
/// <para>
/// The second-order Taylor expansion of the pricing function in spot, volatility, time, rate and
/// carry:
/// </para>
/// <para>
/// <c>dV ~ Delta*dS + 0.5*Gamma*dS^2 + Theta*dt + Vega*dSigma + Vanna*dS*dSigma
/// + 0.5*Volga*dSigma^2 + Charm*dS*dt + dV/dr*dr + dV/db*db</c>
/// </para>
/// <para>
/// <b>Why this takes market states rather than greeks.</b> Handing it a
/// <see cref="OptionGreeks"/> would invite the one mistake that silently ruins the arithmetic:
/// passing greeks that have been through <see cref="OptionGreeks.Scaled"/>. Those are display
/// quantities — theta per day, vega per volatility point — and the expansion needs true derivatives.
/// A per-day theta in a per-year expansion is wrong by 365. So this computes the greeks itself, in
/// raw units, from the states you supply. There is no way to pass the wrong convention.
/// </para>
/// <para>
/// <b>Greeks are taken at the START state.</b> That is what a risk system knew at the time and what
/// it would have reported, which is the question attribution answers. Evaluating at the midpoint of
/// the interval would shrink the residual — sometimes by an order of magnitude — but it explains the
/// move using information from the end of it, which is a different and less useful question.
/// </para>
/// <para>
/// <b>Measured accuracy, for a 30-day at-the-money NIFTY call over one day.</b> The residual share
/// is not monotonic in the move, and both ends matter:
/// </para>
/// <list type="table">
/// <item><description><c>0.05%</c> move — residual 4.4% of a very small change</description></item>
/// <item><description><c>0.5%</c> move — residual <b>0.04%</b>, the sweet spot</description></item>
/// <item><description><c>2%</c> move — residual 0.6%</description></item>
/// <item><description><c>5%</c> move — residual 5.9%</description></item>
/// <item><description><c>10%</c> move — residual <b>24%</b></description></item>
/// </list>
/// <para>
/// The high end is the expansion failing: gamma itself moves over a gap that size, and the error
/// grows faster than the cube of the move. The low end is different and easy to misread — the
/// expansion is only FIRST order in time, so the missing second time derivative leaves a residual
/// floor of roughly 0.03 price units per day that does not shrink as the market goes quiet. On a
/// still day the share looks bad because the denominator is small, not because the attribution is.
/// Read <see cref="PnlAttributionResult.Residual"/> in price units, not only
/// <see cref="PnlAttributionResult.ResidualShare"/>.
/// </para>
/// <para>
/// <b>This explains a CHANGE IN VALUE, not profit since entry.</b> What you paid is irrelevant to
/// it: a leg's <see cref="StrategyLeg.Premium"/> plays no part, because the premium is a constant and
/// constants have no derivatives. Attribution answers "why did my mark move", not "am I up on the
/// trade".
/// </para>
/// </remarks>
public static class PnlAttribution
{
    /// <summary>
    /// Explains the change in one contract's value between two market states.
    /// </summary>
    /// <param name="start">
    /// The starting state. Greeks are evaluated here.
    /// </param>
    /// <param name="end">
    /// The ending state. Must describe the SAME contract — same strike and same right — differing
    /// only in market variables and in time remaining.
    /// </param>
    /// <returns>
    /// The breakdown, or <c>null</c> if either state is invalid, if the two describe different
    /// contracts, or if time runs BACKWARDS between them. A negative elapsed time almost always
    /// means the arguments were passed the wrong way round, and attributing it would produce a
    /// plausible set of terms with every sign inverted.
    /// </returns>
    public static PnlAttributionResult? Explain(OptionInputs start, OptionInputs end)
    {
        if (start is null || end is null) return null;
        if (!start.IsValid || !end.IsValid) return null;
        if (start.Strike != end.Strike || start.Right != end.Right) return null;

        double elapsed = start.TimeToExpiry - end.TimeToExpiry;
        if (elapsed < 0) return null;

        var greeks = BlackScholesMerton.ComputeGreeks(start);
        double? startPrice = BlackScholesMerton.Price(start);
        double? endPrice = BlackScholesMerton.Price(end);
        if (greeks is null || startPrice is null || endPrice is null) return null;

        return Build(endPrice.Value - startPrice.Value, greeks, start, end, elapsed, quantity: 1.0);
    }

    /// <summary>
    /// Explains the change in a multi-leg position's value between two market states.
    /// </summary>
    /// <remarks>
    /// Every term is linear in the greeks and in quantity, so a position's attribution is the sum of
    /// its legs' — computed leg by leg, at each leg's own strike and right, then added with signs.
    /// A short leg contributes negative gamma and negative theta automatically.
    /// <para>
    /// All legs must share the one underlying and the one expiry that <paramref name="start"/>
    /// describes. Greeks from different expiries do not add, and nothing here can detect that a
    /// caller mixed them.
    /// </para>
    /// </remarks>
    /// <param name="legs">The position. Must be non-empty and every leg valid.</param>
    /// <param name="start">
    /// Starting market state shared by every leg. Its <c>Strike</c> and <c>Right</c> are taken from
    /// each leg and ignored here.
    /// </param>
    /// <param name="end">Ending market state, on the same terms.</param>
    /// <returns>The summed breakdown, or <c>null</c> on invalid input or backwards time.</returns>
    public static PnlAttributionResult? ExplainPosition(
        IReadOnlyList<StrategyLeg> legs, OptionInputs start, OptionInputs end)
    {
        if (legs is null || legs.Count == 0 || legs.Any(l => l is null || !l.IsValid)) return null;
        if (start is null || end is null || !start.IsValid || !end.IsValid) return null;

        double elapsed = start.TimeToExpiry - end.TimeToExpiry;
        if (elapsed < 0) return null;

        double actual = 0, delta = 0, gamma = 0, theta = 0, vega = 0;
        double vanna = 0, volga = 0, charm = 0, rate = 0, carry = 0;

        foreach (var leg in legs)
        {
            var legStart = start with { Strike = leg.Strike, Right = leg.Right };
            var legEnd = end with { Strike = leg.Strike, Right = leg.Right };

            var one = Explain(legStart, legEnd);
            if (one is null) return null;

            actual += leg.Quantity * one.Actual;
            delta += leg.Quantity * one.DeltaPnl;
            gamma += leg.Quantity * one.GammaPnl;
            theta += leg.Quantity * one.ThetaPnl;
            vega += leg.Quantity * one.VegaPnl;
            vanna += leg.Quantity * one.VannaPnl;
            volga += leg.Quantity * one.VolgaPnl;
            charm += leg.Quantity * one.CharmPnl;
            rate += leg.Quantity * one.RatePnl;
            carry += leg.Quantity * one.CarryPnl;
        }

        return new PnlAttributionResult(
            actual, delta, gamma, theta, vega, vanna, volga, charm, rate, carry);
    }

    /// <summary>Assembles the terms from a greek set and the moves between two states.</summary>
    private static PnlAttributionResult Build(
        double actual, OptionGreeks g, OptionInputs start, OptionInputs end,
        double elapsed, double quantity)
    {
        double dS = end.Spot - start.Spot;
        double dSigma = end.Volatility - start.Volatility;
        double dRate = end.RiskFreeRate - start.RiskFreeRate;
        double dCarry = end.CostOfCarry - start.CostOfCarry;

        // dV/db, the sensitivity to the carry with the rate held fixed. The library exposes the two
        // rhos rather than this, but they determine it: RhoCarryTracksRate is dV/dr WITH the carry
        // following, which is dV/dr + dV/db, and RhoCarryFixed is dV/dr alone. The difference is
        // therefore dV/db, and it reduces to T * S * Delta.
        double carrySensitivity = g.RhoCarryTracksRate - g.RhoCarryFixed;

        return new PnlAttributionResult(
            Actual: quantity * actual,
            DeltaPnl: quantity * g.Delta * dS,
            GammaPnl: quantity * 0.5 * g.Gamma * dS * dS,
            ThetaPnl: quantity * g.Theta * elapsed,
            VegaPnl: quantity * g.Vega * dSigma,
            VannaPnl: quantity * g.Vanna * dS * dSigma,
            VolgaPnl: quantity * 0.5 * g.Volga * dSigma * dSigma,
            CharmPnl: quantity * g.Charm * dS * elapsed,
            RatePnl: quantity * g.RhoCarryFixed * dRate,
            CarryPnl: quantity * carrySensitivity * dCarry);
    }
}
