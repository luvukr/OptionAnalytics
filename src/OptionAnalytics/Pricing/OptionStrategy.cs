namespace OptionAnalytics;

/// <summary>One leg of a multi-leg position.</summary>
/// <param name="Right">Call or put.</param>
/// <param name="Strike">The leg's strike. Must be greater than zero.</param>
/// <param name="Quantity">
/// Signed contract count: POSITIVE is long (bought), NEGATIVE is short (sold). Expressed in
/// contracts, not units — multiply by lot size yourself if you want rupee figures per lot.
/// </param>
/// <param name="Premium">
/// Premium per unit paid or received, as a POSITIVE magnitude regardless of direction. Whether it
/// is a debit or a credit is carried by the sign of <paramref name="Quantity"/>, so a leg cannot
/// silently contradict itself.
/// </param>
public sealed record StrategyLeg(OptionRight Right, double Strike, double Quantity, double Premium)
{
    /// <summary>True if strike, quantity and premium are all finite and sensibly signed.</summary>
    public bool IsValid =>
        double.IsFinite(Strike) && Strike > 0 &&
        double.IsFinite(Quantity) && Quantity != 0 &&
        double.IsFinite(Premium) && Premium >= 0;

    /// <summary>The leg's intrinsic value per unit at a given terminal price.</summary>
    public double IntrinsicAt(double underlyingPrice) => Right == OptionRight.Call
        ? Math.Max(0.0, underlyingPrice - Strike)
        : Math.Max(0.0, Strike - underlyingPrice);

    /// <summary>Net cash at entry: negative for a debit paid, positive for a credit received.</summary>
    public double NetCashAtEntry => -Quantity * Premium;
}

/// <summary>The shape of a strategy's profit and loss at expiry.</summary>
/// <param name="NetCashAtEntry">
/// Total cash flow when the position is opened: negative for a net debit, positive for a net
/// credit.
/// </param>
/// <param name="MaxProfit">
/// Greatest profit at expiry, or <c>null</c> if profit is unbounded above.
/// </param>
/// <param name="MaxLoss">
/// Greatest loss at expiry as a NEGATIVE number, or <c>null</c> if loss is unbounded below.
/// </param>
/// <param name="Breakevens">
/// Underlying prices at which profit is exactly zero, ascending. A strategy may have none, one, or
/// several.
/// </param>
public sealed record StrategyProfile(
    double NetCashAtEntry,
    double? MaxProfit,
    double? MaxLoss,
    IReadOnlyList<double> Breakevens);

/// <summary>
/// Payoff, breakevens and aggregate greeks for a multi-leg option position.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is arithmetic over legs you supply. The library has no catalogue of named
/// structures — no <c>IronCondor</c> factory, no <c>BullCallSpread</c> — deliberately. A vertical
/// spread is two legs; naming it adds a vocabulary to learn and a taxonomy to argue about, and
/// the names differ between desks and between textbooks anyway. Build the legs; the payoff does
/// not care what anyone calls the shape.
/// </para>
/// <para>
/// <b>Payoff is at EXPIRY.</b> Every figure below assumes the position is held to expiry and every
/// leg settles at intrinsic value. Before expiry a position is worth its legs' model prices, which
/// can be very different — a short strangle that will expire worthless can still be deeply
/// underwater a week beforehand, and that is what forces a margin call. Use
/// <see cref="NetGreeks"/> and price the legs individually for the path; use this for the
/// destination.
/// </para>
/// <para>
/// <b>What is not modelled.</b> Transaction costs, margin, assignment risk on American-style
/// contracts, and the possibility that a leg cannot be closed at all. A defined-risk structure on
/// paper is only defined-risk if both legs actually trade — the wing of a credit spread is
/// typically the illiquid one, and a wing that cannot be exited turns a bounded loss into a naked
/// position.
/// </para>
/// </remarks>
public static class OptionStrategy
{
    /// <summary>
    /// Profit or loss at expiry for a given terminal price of the underlying.
    /// </summary>
    /// <param name="legs">The position's legs. Must be non-empty and all valid.</param>
    /// <param name="underlyingPrice">Terminal price of the underlying. Must be non-negative.</param>
    /// <returns>
    /// Profit (positive) or loss (negative) per unit of the underlying, or <c>null</c> if any leg
    /// is invalid.
    /// </returns>
    public static double? PayoffAtExpiry(IReadOnlyList<StrategyLeg> legs, double underlyingPrice)
    {
        if (!IsUsable(legs) || !double.IsFinite(underlyingPrice) || underlyingPrice < 0) return null;

        double payoff = 0.0;
        foreach (var leg in legs)
            payoff += leg.Quantity * leg.IntrinsicAt(underlyingPrice) + leg.NetCashAtEntry;

        return payoff;
    }

    /// <summary>
    /// The strategy's payoff shape: net cash, bounds, and breakevens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payoff of any combination of European options is piecewise linear in the terminal price,
    /// with kinks only at the strikes. That makes this exact rather than sampled: evaluating at
    /// every strike, at zero, and beyond the highest strike captures every vertex, and the slopes
    /// of the two outer segments determine whether profit or loss is bounded. Breakevens are then
    /// solved on each segment directly rather than searched for.
    /// </para>
    /// </remarks>
    /// <param name="legs">The position's legs. Must be non-empty and all valid.</param>
    /// <returns>The profile, or <c>null</c> if any leg is invalid.</returns>
    public static StrategyProfile? Profile(IReadOnlyList<StrategyLeg> legs)
    {
        if (!IsUsable(legs)) return null;

        double netCash = legs.Sum(l => l.NetCashAtEntry);

        // Vertices: zero, every strike, and a point beyond the last strike to read the far slope.
        // The payoff is piecewise linear with kinks only at strikes, so these capture every vertex.
        var strikes = legs.Select(l => l.Strike).Distinct().OrderBy(k => k).ToList();

        // Two adjacent segments meeting at a root each report it, so near-duplicates must be
        // merged. The threshold has to scale with the price: a fixed 1e-9 is far tighter than the
        // rounding noise in a root solved near NIFTY's 25,000 -- which would emit the same
        // breakeven twice -- and far looser than the gap between distinct roots on a
        // sub-microcent instrument, which would silently drop one.
        double dedupTolerance = Math.Max(1e-12, strikes[^1] * 1e-9);

        var vertices = new List<double> { 0.0 };
        vertices.AddRange(strikes);
        vertices.Add(strikes[^1] + Math.Max(1.0, strikes[^1]));

        var payoffs = vertices.Select(v => PayoffAtExpiry(legs, v)!.Value).ToList();

        // Slope above the highest strike, where only calls are still live. This ALONE decides
        // whether the payoff is unbounded, because the underlying can rise without limit but
        // cannot fall below zero -- so the downside is always bounded, and its extreme is the
        // payoff at a price of zero, which is already a vertex.
        double highSlope = legs.Where(l => l.Right == OptionRight.Call).Sum(l => l.Quantity);

        double? maxProfit = highSlope > 0 ? null : payoffs.Max();
        double? maxLoss = highSlope < 0 ? null : payoffs.Min();

        var breakevens = new List<double>();
        for (int i = 0; i < vertices.Count - 1; i++)
        {
            double x0 = vertices[i], x1 = vertices[i + 1];
            double y0 = payoffs[i], y1 = payoffs[i + 1];

            if (y0 == 0.0) Add(breakevens, x0);

            // A strict sign change inside the segment: solve the line for its root.
            if (y0 != 0.0 && y1 != 0.0 && y0 > 0 != y1 > 0)
                Add(breakevens, x0 + (x1 - x0) * (-y0) / (y1 - y0));
        }

        if (payoffs[^1] == 0.0) Add(breakevens, vertices[^1]);

        // The final ray extends beyond the sampled range and may cross zero out there.
        if (highSlope != 0)
        {
            double xEnd = vertices[^1], yEnd = payoffs[^1];
            if (yEnd != 0 && Math.Sign(yEnd) != Math.Sign(highSlope))
                Add(breakevens, xEnd - yEnd / highSlope);
        }

        breakevens.Sort();
        return new StrategyProfile(netCash, maxProfit, maxLoss, breakevens);

        void Add(List<double> list, double value)
        {
            if (value >= 0 && !list.Any(v => Math.Abs(v - value) < dedupTolerance)) list.Add(value);
        }
    }

    /// <summary>
    /// The position's aggregate greeks: each leg's greeks scaled by its signed quantity and summed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a position tradeable rather than a collection of contracts. A short
    /// strangle's two legs each have a delta; only their sum tells you whether the position is
    /// directional. Because quantity is signed, a short leg contributes negative gamma and
    /// negative vega automatically.
    /// </para>
    /// <para>
    /// All legs must share the same underlying, expiry and volatility for the sum to mean anything.
    /// Greeks from different expiries do not add — theta from a weekly and theta from a monthly are
    /// different quantities wearing the same name — and this method cannot detect that, because a
    /// single <see cref="OptionInputs"/> describes one expiry by construction. Aggregate a
    /// calendar spread only if you know what you are doing with the result.
    /// </para>
    /// </remarks>
    /// <param name="legs">The position's legs. Must be non-empty and all valid.</param>
    /// <param name="market">
    /// Market state shared by every leg. <see cref="OptionInputs.Strike"/> and
    /// <see cref="OptionInputs.Right"/> are taken from each leg and ignored here.
    /// </param>
    /// <returns>The summed greeks, or <c>null</c> if the legs or market state are invalid.</returns>
    public static OptionGreeks? NetGreeks(IReadOnlyList<StrategyLeg> legs, OptionInputs market)
    {
        if (!IsUsable(legs) || market is null || !market.IsValid) return null;

        double price = 0, delta = 0, gamma = 0, vega = 0, theta = 0;
        double rhoTracks = 0, rhoFixed = 0, vanna = 0, volga = 0, charm = 0;

        foreach (var leg in legs)
        {
            var g = BlackScholesMerton.ComputeGreeks(
                market with { Strike = leg.Strike, Right = leg.Right });
            if (g is null) return null;

            price += leg.Quantity * g.Price;
            delta += leg.Quantity * g.Delta;
            gamma += leg.Quantity * g.Gamma;
            vega += leg.Quantity * g.Vega;
            theta += leg.Quantity * g.Theta;
            rhoTracks += leg.Quantity * g.RhoCarryTracksRate;
            rhoFixed += leg.Quantity * g.RhoCarryFixed;
            vanna += leg.Quantity * g.Vanna;
            volga += leg.Quantity * g.Volga;
            charm += leg.Quantity * g.Charm;
        }

        return new OptionGreeks(price, delta, gamma, vega, theta, rhoTracks, rhoFixed, vanna, volga, charm);
    }

    private static bool IsUsable(IReadOnlyList<StrategyLeg>? legs)
        => legs is { Count: > 0 } && legs.All(l => l is not null && l.IsValid);
}
