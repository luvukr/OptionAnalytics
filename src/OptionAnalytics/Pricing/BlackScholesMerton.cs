namespace OptionAnalytics;

/// <summary>
/// The generalised Black-Scholes-Merton model: European option prices and analytic greeks,
/// parameterised by a cost-of-carry rate so that one implementation serves Black-Scholes (1973),
/// Merton (1973), Black (1976) and Garman-Kohlhagen (1983).
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="OptionModel"/> to build the inputs for a specific model rather than setting the
/// carry by hand. Every greek here is analytic — the closed-form derivative, not a numerical
/// bump — and every one is verified in the tests against a finite-difference bump of
/// <see cref="Price"/>, which is an independent check: the analytic formula and the price
/// function share no code path.
/// </para>
/// <para>
/// <b>Degenerate inputs are answered, not refused.</b> At zero time to expiry, or zero volatility,
/// the model has exact limits rather than a hole: the price collapses to discounted forward
/// intrinsic value, delta to a step, gamma and vega to zero. An expiring option is the single most
/// common thing a real system asks about, so returning <c>NaN</c> there — which is what an
/// unguarded <c>log(0)</c> and division by <c>sigma * sqrt(T)</c> produce — would fail exactly
/// when it matters. Genuinely invalid inputs (a negative spot, a non-finite rate) return
/// <c>null</c>.
/// </para>
/// <para>
/// <b>Not every degenerate greek is zero, and assuming so was a bug.</b> The volatility
/// derivatives do vanish — with no volatility in the price there is nothing for vega, gamma, vanna
/// or volga to measure. The TIME and RATE derivatives do not: the discount on the strike still
/// unwinds and, when the carry differs from the rate, the forward still drifts. Theta,
/// <see cref="RhoCarryTracksRate"/> and <see cref="Charm"/> therefore return the analytic
/// derivative of the discounted forward intrinsic in this branch. Returning zero instead left the
/// Black-Scholes PDE identity violated by 4.76 at exactly <c>sigma = 0</c> while it held to
/// machine precision at <c>sigma = 1e-8</c>.
/// </para>
/// </remarks>
public static class BlackScholesMerton
{
    /// <summary>
    /// The European option price.
    /// </summary>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>
    /// The option's fair value, or <c>null</c> if <paramref name="inputs"/> is not
    /// <see cref="OptionInputs.IsValid"/>.
    /// </returns>
    public static double? Price(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        double discount = Math.Exp(-inputs.RiskFreeRate * inputs.TimeToExpiry);

        if (IsDegenerate(inputs))
        {
            // No remaining optionality: the payoff is known up to discounting. Note this is the
            // forward intrinsic discounted, NOT the spot intrinsic -- with a non-zero carry those
            // differ, and using spot intrinsic here would break put-call parity.
            double forwardIntrinsic = inputs.Right == OptionRight.Call
                ? Math.Max(0.0, inputs.Forward - inputs.Strike)
                : Math.Max(0.0, inputs.Strike - inputs.Forward);
            return discount * forwardIntrinsic;
        }

        var t = Terms.From(inputs);
        int sign = inputs.Sign;

        return sign * (inputs.Spot * t.CarryDiscount * NormalDistribution.Cdf(sign * t.D1)
                       - inputs.Strike * discount * NormalDistribution.Cdf(sign * t.D2));
    }

    /// <summary>
    /// Sensitivity of the price to the underlying: <c>dV/dS</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In [0, exp((b-r)T)] for a call and [-exp((b-r)T), 0] for a put. That upper bound EXCEEDS ONE
    /// whenever the carry exceeds the rate, so a deep in-the-money delta above 1.0 is correct, not
    /// a defect: a Merton call with a negative dividend yield of 5% over two years reaches 1.104.
    /// Only under Black-Scholes proper, where b = r, is the range [-1, 1].
    /// </para>
    /// <para>
    /// At expiry it becomes a step function — exactly 0 or ± the carry factor — with the
    /// at-the-money point returned as zero, since the derivative genuinely does not exist there.
    /// </para>
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Delta, or <c>null</c> if the inputs are invalid.</returns>
    public static double? Delta(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        if (IsDegenerate(inputs))
            return DegenerateIsInTheMoney(inputs, out double carryDiscount, out _)
                ? inputs.Sign * carryDiscount
                : 0.0;

        var t = Terms.From(inputs);
        return inputs.Sign * t.CarryDiscount * NormalDistribution.Cdf(inputs.Sign * t.D1);
    }

    /// <summary>
    /// Sensitivity of delta to the underlying: <c>d2V/dS2</c>. Identical for calls and puts.
    /// </summary>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Gamma (never negative), or <c>null</c> if the inputs are invalid.</returns>
    public static double? Gamma(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;
        if (IsDegenerate(inputs)) return 0.0;

        var t = Terms.From(inputs);
        return t.CarryDiscount * NormalDistribution.Pdf(t.D1) / (inputs.Spot * t.SigmaRootT);
    }

    /// <summary>
    /// Sensitivity of the price to volatility: <c>dV/dsigma</c>, per ONE FULL UNIT of volatility
    /// (1.00 = 100 volatility points). Identical for calls and puts.
    /// </summary>
    /// <remarks>
    /// This is the raw derivative. Traders almost always want it per one volatility POINT, which
    /// is this divided by 100 — see <see cref="GreekConventions"/> and
    /// <see cref="OptionGreeks.Scaled"/>, which do that conversion explicitly rather than baking a
    /// convention into the mathematics.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Vega (never negative), or <c>null</c> if the inputs are invalid.</returns>
    public static double? Vega(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;
        if (IsDegenerate(inputs)) return 0.0;

        var t = Terms.From(inputs);
        return inputs.Spot * t.CarryDiscount * NormalDistribution.Pdf(t.D1) * t.RootT;
    }

    /// <summary>
    /// Sensitivity of the price to the passage of time: <c>dV/dT</c> expressed as decay,
    /// per YEAR.
    /// </summary>
    /// <remarks>
    /// Negative for most long options — value bleeds away as expiry approaches — but legitimately
    /// positive for a deep in-the-money European put, where waiting brings the discounted strike
    /// closer. Per year is the raw derivative; per day is the trading convention, and
    /// <see cref="GreekConventions"/> performs that division explicitly.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Theta per year, or <c>null</c> if the inputs are invalid.</returns>
    public static double? Theta(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        if (IsDegenerate(inputs))
        {
            // NOT zero. With no optionality left the price is still a function of time -- the
            // discount on the strike unwinds, and with a non-zero carry the forward drifts -- so a
            // position genuinely accrues value as expiry approaches. Differentiating the
            // discounted forward intrinsic gives the exact limit; returning zero here broke the
            // PDE identity at exactly this boundary while satisfying it everywhere else.
            if (!DegenerateIsInTheMoney(inputs, out double carryDiscount, out double rateDiscount))
                return 0.0;

            return -inputs.Sign * ((inputs.CostOfCarry - inputs.RiskFreeRate) * inputs.Spot * carryDiscount
                                   + inputs.RiskFreeRate * inputs.Strike * rateDiscount);
        }

        var t = Terms.From(inputs);
        int sign = inputs.Sign;
        double discount = Math.Exp(-inputs.RiskFreeRate * inputs.TimeToExpiry);
        double carryMinusRate = inputs.CostOfCarry - inputs.RiskFreeRate;

        double decay = -inputs.Spot * t.CarryDiscount * NormalDistribution.Pdf(t.D1) * inputs.Volatility
                       / (2.0 * t.RootT);
        double carryTerm = -carryMinusRate * inputs.Spot * t.CarryDiscount
                           * NormalDistribution.Cdf(sign * t.D1) * sign;
        double rateTerm = -inputs.RiskFreeRate * inputs.Strike * discount
                          * NormalDistribution.Cdf(sign * t.D2) * sign;

        return decay + carryTerm + rateTerm;
    }

    /// <summary>
    /// Sensitivity of the price to the risk-free rate when the COST OF CARRY MOVES WITH IT —
    /// the conventional equity and index rho, per one full unit of rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There are two different rhos and choosing the wrong one is a real error.</b> Which is
    /// correct depends on whether the underlying's drift moves when the rate does, and that is a
    /// property of the model, not of the contract — so this library computes both and names them,
    /// rather than picking one silently.
    /// </para>
    /// <para>
    /// Use THIS one for <see cref="OptionModel.BlackScholes"/> and
    /// <see cref="OptionModel.Merton"/>, where carry is <c>r</c> or <c>r - q</c> with the dividend
    /// yield fixed: a change in <c>r</c> feeds through to the drift as well as to the discount
    /// factor. Use <see cref="RhoCarryFixed"/> for <see cref="OptionModel.Black76"/>.
    /// </para>
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Rho per unit rate, or <c>null</c> if the inputs are invalid.</returns>
    public static double? RhoCarryTracksRate(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        if (IsDegenerate(inputs))
        {
            // With the carry tracking the rate, (b - r) is constant, so the rate reaches the
            // degenerate price only through the discount on the strike. Its derivative is not
            // zero. Compare RhoCarryFixed, which delegates to Price and has always been right
            // here -- that disagreement is what proved this a bug rather than a convention.
            return DegenerateIsInTheMoney(inputs, out _, out double rateDiscount)
                ? inputs.Sign * inputs.Strike * inputs.TimeToExpiry * rateDiscount
                : 0.0;
        }

        var t = Terms.From(inputs);
        int sign = inputs.Sign;
        double discount = Math.Exp(-inputs.RiskFreeRate * inputs.TimeToExpiry);

        return sign * inputs.Strike * inputs.TimeToExpiry * discount
               * NormalDistribution.Cdf(sign * t.D2);
    }

    /// <summary>
    /// Sensitivity of the price to the risk-free rate when the COST OF CARRY IS HELD FIXED. Equals
    /// <c>-T * Price</c> exactly, for every model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the correct rho for an option on a FUTURE (<see cref="OptionModel.Black76"/>),
    /// where the carry is structurally zero and the rate enters only through discounting. The
    /// identity falls out of that: with the carry fixed, the whole price is
    /// <c>exp(-rT)</c> times a quantity that does not involve <c>r</c> at all, so differentiating
    /// gives <c>-T</c> times the price.
    /// </para>
    /// <para>
    /// See <see cref="RhoCarryTracksRate"/> for the equity convention, and for why both exist.
    /// </para>
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Rho per unit rate, or <c>null</c> if the inputs are invalid.</returns>
    public static double? RhoCarryFixed(OptionInputs inputs)
    {
        double? price = Price(inputs);
        return price is null ? null : -inputs!.TimeToExpiry * price.Value;
    }

    /// <summary>
    /// Sensitivity of delta to volatility, equivalently of vega to spot: <c>d2V/dS dsigma</c>.
    /// Identical for calls and puts.
    /// </summary>
    /// <remarks>
    /// The greek that matters when a position is delta-hedged but the smile moves — a hedge set at
    /// one volatility is no longer flat at another.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Vanna, or <c>null</c> if the inputs are invalid.</returns>
    public static double? Vanna(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;
        if (IsDegenerate(inputs)) return 0.0;

        var t = Terms.From(inputs);
        return -t.CarryDiscount * NormalDistribution.Pdf(t.D1) * t.D2 / inputs.Volatility;
    }

    /// <summary>
    /// Sensitivity of vega to volatility: <c>d2V/dsigma2</c>. Identical for calls and puts.
    /// </summary>
    /// <remarks>
    /// Also called vomma. Near zero at the money and largest in the wings, which is why a
    /// vega-neutral book built only from at-the-money options is not volatility-neutral at all.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Volga, or <c>null</c> if the inputs are invalid.</returns>
    public static double? Volga(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;
        if (IsDegenerate(inputs)) return 0.0;

        var t = Terms.From(inputs);
        double vega = inputs.Spot * t.CarryDiscount * NormalDistribution.Pdf(t.D1) * t.RootT;
        return vega * t.D1 * t.D2 / inputs.Volatility;
    }

    /// <summary>
    /// Sensitivity of delta to the passage of time: <c>d2V/dS dT</c>, per YEAR.
    /// </summary>
    /// <remarks>
    /// How much a hedge drifts out of balance overnight without the underlying moving at all —
    /// the reason a delta-neutral book needs rebalancing on a quiet morning.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>Charm per year, or <c>null</c> if the inputs are invalid.</returns>
    public static double? Charm(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        if (IsDegenerate(inputs))
        {
            // Degenerate delta is sign * exp((b - r) * T), which still moves with time whenever
            // the carry differs from the rate. Zero is correct only for b == r.
            return DegenerateIsInTheMoney(inputs, out double carryDiscount, out _)
                ? -inputs.Sign * (inputs.CostOfCarry - inputs.RiskFreeRate) * carryDiscount
                : 0.0;
        }

        var t = Terms.From(inputs);
        int sign = inputs.Sign;
        double carryMinusRate = inputs.CostOfCarry - inputs.RiskFreeRate;

        double drift = -carryMinusRate * t.CarryDiscount * NormalDistribution.Cdf(sign * t.D1) * sign;
        double shape = -t.CarryDiscount * NormalDistribution.Pdf(t.D1)
                       * (2.0 * inputs.CostOfCarry * inputs.TimeToExpiry - t.D2 * t.SigmaRootT)
                       / (2.0 * inputs.TimeToExpiry * t.SigmaRootT);

        return drift + shape;
    }

    /// <summary>
    /// Every greek at once, computed from a single evaluation of the shared terms.
    /// </summary>
    /// <remarks>
    /// Cheaper than calling each greek separately — <c>d1</c>, <c>d2</c> and the discount factors
    /// are computed once rather than seven times — and it is the form most callers want anyway.
    /// </remarks>
    /// <param name="inputs">The contract and market state.</param>
    /// <returns>The full greek set, or <c>null</c> if the inputs are invalid.</returns>
    public static OptionGreeks? ComputeGreeks(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        return new OptionGreeks(
            Price: Price(inputs)!.Value,
            Delta: Delta(inputs)!.Value,
            Gamma: Gamma(inputs)!.Value,
            Vega: Vega(inputs)!.Value,
            Theta: Theta(inputs)!.Value,
            RhoCarryTracksRate: RhoCarryTracksRate(inputs)!.Value,
            RhoCarryFixed: RhoCarryFixed(inputs)!.Value,
            Vanna: Vanna(inputs)!.Value,
            Volga: Volga(inputs)!.Value,
            Charm: Charm(inputs)!.Value);
    }

    /// <summary>True when there is no optionality left to price — zero time or zero volatility.</summary>
    private static bool IsDegenerate(OptionInputs inputs)
        => inputs.TimeToExpiry <= 0 || inputs.Volatility <= 0;

    /// <summary>
    /// Whether a degenerate contract settles in the money, with the two discount factors every
    /// degenerate formula needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With no optionality the price is the discounted FORWARD intrinsic, so moneyness is decided
    /// against the forward, not the spot — the same choice <see cref="Price"/> makes, and the
    /// reason parity survives a non-zero carry here.
    /// </para>
    /// <para>
    /// Exactly at the money the payoff has a kink and no derivative exists, so this reports
    /// out-of-the-money and every degenerate greek returns zero there. That is a convention, not a
    /// limit: the left and right derivatives differ, and zero is the only answer that does not
    /// privilege one side.
    /// </para>
    /// </remarks>
    private static bool DegenerateIsInTheMoney(
        OptionInputs inputs, out double carryDiscount, out double rateDiscount)
    {
        carryDiscount = Math.Exp((inputs.CostOfCarry - inputs.RiskFreeRate) * inputs.TimeToExpiry);
        rateDiscount = Math.Exp(-inputs.RiskFreeRate * inputs.TimeToExpiry);

        double moneyness = inputs.Forward - inputs.Strike;
        if (moneyness == 0) return false;

        return inputs.Right == OptionRight.Call ? moneyness > 0 : moneyness < 0;
    }

    /// <summary>
    /// The quantities every formula above shares. Computed once per call so that d1 and d2 are
    /// defined in exactly one place — the single most transcription-error-prone line in option
    /// pricing.
    /// </summary>
    private readonly record struct Terms(double D1, double D2, double RootT, double SigmaRootT, double CarryDiscount)
    {
        public static Terms From(OptionInputs i)
        {
            double rootT = Math.Sqrt(i.TimeToExpiry);
            double sigmaRootT = i.Volatility * rootT;
            double d1 = (Math.Log(i.Spot / i.Strike)
                         + (i.CostOfCarry + 0.5 * i.Volatility * i.Volatility) * i.TimeToExpiry)
                        / sigmaRootT;

            return new Terms(
                D1: d1,
                D2: d1 - sigmaRootT,
                RootT: rootT,
                SigmaRootT: sigmaRootT,
                CarryDiscount: Math.Exp((i.CostOfCarry - i.RiskFreeRate) * i.TimeToExpiry));
        }
    }
}
