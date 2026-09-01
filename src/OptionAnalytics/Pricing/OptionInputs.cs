namespace OptionAnalytics;

/// <summary>Whether a contract is a call or a put.</summary>
public enum OptionRight
{
    /// <summary>Right to buy the underlying at the strike.</summary>
    Call = 1,

    /// <summary>Right to sell the underlying at the strike.</summary>
    Put = -1,
}

/// <summary>
/// Everything needed to price a European option, in one immutable value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a record rather than seven parameters.</b> Pricing functions attract parameters over
/// time — a dividend yield, then a foreign rate, then a carry adjustment — and every one added to
/// a positional signature is a breaking change for callers. Passing one <c>with</c>-able value
/// instead means a later addition is a new property with a default, not a new overload. It also
/// makes the common analytical move cheap and obvious: <c>inputs with { Volatility = v }</c> is
/// how the implied-volatility solver walks its search, and <c>inputs with { Spot = s + h }</c> is
/// how the tests bump a price to check a greek by finite difference.
/// </para>
/// <para>
/// <b>The carry rate is the extensibility hinge.</b> See <see cref="CostOfCarry"/> — it is what
/// lets one pricing implementation serve four published models.
/// </para>
/// <para>
/// All rates and the volatility are continuously compounded annual decimals: <c>0.065</c> is
/// 6.5% per annum, not 6.5. Time is in years.
/// </para>
/// </remarks>
/// <param name="Spot">
/// Price of the underlying. For an option on a future this is the FUTURES price, and
/// <see cref="CostOfCarry"/> must be zero — see <see cref="OptionModel.Black76"/>.
/// </param>
/// <param name="Strike">The contract's strike price.</param>
/// <param name="TimeToExpiry">
/// Time remaining to expiry, in years. Zero is a valid, meaningful input: every price collapses to
/// intrinsic value and every greek to its limit.
/// </param>
/// <param name="Volatility">
/// Annualised volatility of the underlying's log returns, as a decimal (<c>0.20</c> for 20%).
/// Zero is valid and collapses the price to discounted intrinsic value.
/// </param>
/// <param name="RiskFreeRate">
/// The continuously compounded annual rate used to DISCOUNT the payoff. Always the domestic
/// risk-free rate, in every model — it is <see cref="CostOfCarry"/> that varies.
/// </param>
/// <param name="CostOfCarry">
/// The drift of the underlying under the pricing measure — the rate at which holding the
/// underlying accrues, net of what it costs to hold. This single parameter is what makes the
/// library extensible: see <see cref="OptionModel"/>, which sets it to reproduce Black-Scholes
/// (1973), Merton (1973), Black (1976) and Garman-Kohlhagen (1983) from one implementation.
/// </param>
/// <param name="Right">Call or put.</param>
public sealed record OptionInputs(
    double Spot,
    double Strike,
    double TimeToExpiry,
    double Volatility,
    double RiskFreeRate,
    double CostOfCarry,
    OptionRight Right)
{
    /// <summary>True if every input is finite and the non-negative ones are non-negative.</summary>
    /// <remarks>
    /// <para>
    /// Rates and the carry may legitimately be negative — negative policy rates existed for a
    /// decade, and a carry below zero is ordinary for a dividend yield above the risk-free rate.
    /// Spot, strike, time and volatility may not be.
    /// </para>
    /// <para>
    /// <b><see cref="Right"/> is checked against the two defined members.</b> An enum in .NET is a
    /// suggestion, not a constraint: <c>(OptionRight)99</c> compiles, and since neither member is
    /// zero, <c>default</c> and a JSON payload with the field missing both produce an undefined
    /// value. Left unchecked, that value flows into <see cref="Sign"/> and multiplies through every
    /// formula — a cast of 99 prices a 100-strike at-the-money call at 482.83 rather than
    /// refusing. A fabricated price that looks like a price is the worst possible failure here, so
    /// this rejects the input instead.
    /// </para>
    /// </remarks>
    public bool IsValid =>
        (Right == OptionRight.Call || Right == OptionRight.Put) &&
        double.IsFinite(Spot) && Spot > 0 &&
        double.IsFinite(Strike) && Strike > 0 &&
        double.IsFinite(TimeToExpiry) && TimeToExpiry >= 0 &&
        double.IsFinite(Volatility) && Volatility >= 0 &&
        double.IsFinite(RiskFreeRate) &&
        double.IsFinite(CostOfCarry);

    /// <summary>
    /// The forward price of the underlying at expiry implied by the carry rate,
    /// <c>Spot * exp(CostOfCarry * TimeToExpiry)</c>.
    /// </summary>
    /// <remarks>
    /// Under Black-76 the carry is zero, so the forward is the futures price itself — which is the
    /// whole content of that model.
    /// </remarks>
    public double Forward => Spot * Math.Exp(CostOfCarry * TimeToExpiry);

    /// <summary>Intrinsic value at the current spot, undiscounted and never negative.</summary>
    public double IntrinsicValue => Right == OptionRight.Call
        ? Math.Max(0.0, Spot - Strike)
        : Math.Max(0.0, Strike - Spot);

    /// <summary>+1 for a call, -1 for a put — the sign that appears throughout the formulas.</summary>
    internal int Sign => (int)Right;
}
