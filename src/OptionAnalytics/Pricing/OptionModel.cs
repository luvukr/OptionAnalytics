namespace OptionAnalytics;

/// <summary>
/// Builds <see cref="OptionInputs"/> for each of the published models this library covers, by
/// choosing the cost-of-carry rate that specialises the generalised formula to that model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The design in one paragraph.</b> Black-Scholes, Merton, Black-76 and Garman-Kohlhagen are
/// not four formulas — they are one formula evaluated at four values of a single carry parameter
/// <c>b</c>. That observation is Haug's, and it is the reason this library has one pricing
/// implementation and one set of greeks rather than four of each. It is also why adding a fifth
/// model later is a new factory method here and nothing else: no new pricing code, no new greeks,
/// no new tests of the arithmetic.
/// </para>
/// <list type="table">
/// <listheader><term>carry</term><description>model</description></listheader>
/// <item><term><c>b = r</c></term><description>
/// Black-Scholes (1973) — an equity paying no dividend. The stock drifts at the risk-free rate.
/// </description></item>
/// <item><term><c>b = r - q</c></term><description>
/// Merton (1973) — an equity or index paying a continuous dividend yield <c>q</c>. The dividend
/// leaks out of the price, so the drift is reduced by it.
/// </description></item>
/// <item><term><c>b = 0</c></term><description>
/// Black (1976) — an option on a future. A futures price already embeds the cost of carry, so
/// under the risk-neutral measure it drifts at zero.
/// </description></item>
/// <item><term><c>b = r - rf</c></term><description>
/// Garman-Kohlhagen (1983) — a currency option. The foreign currency earns its own rate
/// <c>rf</c>, which plays exactly the role a dividend yield does.
/// </description></item>
/// </list>
/// <para>
/// <b>What this library does not model.</b> Every model here is European — exercisable only at
/// expiry. That is correct for NSE index options, for most cash-settled index products, and for
/// FX options; it is NOT correct for American-style single-stock options, where early exercise has
/// value that these formulas cannot see and will silently understate. It also assumes constant
/// volatility, so it prices a single point on a smile rather than the smile itself. Both are
/// properties of the models, not omissions from the code.
/// </para>
/// </remarks>
public static class OptionModel
{
    /// <summary>
    /// Black-Scholes (1973): a non-dividend-paying underlying. Carry equals the risk-free rate.
    /// </summary>
    /// <param name="spot">Price of the underlying.</param>
    /// <param name="strike">Strike price.</param>
    /// <param name="timeToExpiry">Years to expiry.</param>
    /// <param name="volatility">Annualised volatility as a decimal.</param>
    /// <param name="riskFreeRate">Continuously compounded annual risk-free rate as a decimal.</param>
    /// <param name="right">Call or put.</param>
    public static OptionInputs BlackScholes(
        double spot, double strike, double timeToExpiry,
        double volatility, double riskFreeRate, OptionRight right)
        => new(spot, strike, timeToExpiry, volatility, riskFreeRate, riskFreeRate, right);

    /// <summary>
    /// Merton (1973): an underlying paying a continuous dividend yield. Carry is
    /// <c>riskFreeRate - dividendYield</c>.
    /// </summary>
    /// <remarks>
    /// This is the right model for a broad equity index quoted on spot, where the index level
    /// drops by the dividends its constituents pay.
    /// </remarks>
    /// <param name="spot">Spot price of the underlying.</param>
    /// <param name="strike">Strike price.</param>
    /// <param name="timeToExpiry">Years to expiry.</param>
    /// <param name="volatility">Annualised volatility as a decimal.</param>
    /// <param name="riskFreeRate">Continuously compounded annual risk-free rate as a decimal.</param>
    /// <param name="dividendYield">Continuously compounded annual dividend yield as a decimal.</param>
    /// <param name="right">Call or put.</param>
    public static OptionInputs Merton(
        double spot, double strike, double timeToExpiry,
        double volatility, double riskFreeRate, double dividendYield, OptionRight right)
        => new(spot, strike, timeToExpiry, volatility, riskFreeRate, riskFreeRate - dividendYield, right);

    /// <summary>
    /// Black (1976): an option on a FUTURE. Carry is zero, and <paramref name="futuresPrice"/>
    /// takes the place of spot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero carry is the entire model. A futures price already contains the cost of carrying the
    /// underlying to delivery, so under the risk-neutral measure it has no drift left; discounting
    /// still happens at the risk-free rate, which is why <paramref name="riskFreeRate"/> is still
    /// required.
    /// </para>
    /// <para>
    /// Passing a futures price to <see cref="BlackScholes"/> instead is a real and easy mistake:
    /// it double-counts the carry and overprices calls. Use this factory and the mistake cannot be
    /// made.
    /// </para>
    /// </remarks>
    /// <param name="futuresPrice">Current price of the underlying FUTURE, not the spot.</param>
    /// <param name="strike">Strike price.</param>
    /// <param name="timeToExpiry">Years to expiry.</param>
    /// <param name="volatility">Annualised volatility as a decimal.</param>
    /// <param name="riskFreeRate">Continuously compounded annual discount rate as a decimal.</param>
    /// <param name="right">Call or put.</param>
    public static OptionInputs Black76(
        double futuresPrice, double strike, double timeToExpiry,
        double volatility, double riskFreeRate, OptionRight right)
        => new(futuresPrice, strike, timeToExpiry, volatility, riskFreeRate, 0.0, right);

    /// <summary>
    /// Garman-Kohlhagen (1983): a currency option. Carry is
    /// <c>domesticRate - foreignRate</c>.
    /// </summary>
    /// <remarks>
    /// The foreign interest rate plays precisely the role a dividend yield plays for an equity:
    /// holding the foreign currency earns it, so it reduces the drift by the same arithmetic.
    /// </remarks>
    /// <param name="spotFxRate">Spot exchange rate, quoted as domestic currency per unit of foreign.</param>
    /// <param name="strike">Strike exchange rate, same quoting convention.</param>
    /// <param name="timeToExpiry">Years to expiry.</param>
    /// <param name="volatility">Annualised volatility of the exchange rate as a decimal.</param>
    /// <param name="domesticRate">Continuously compounded annual domestic rate as a decimal.</param>
    /// <param name="foreignRate">Continuously compounded annual foreign rate as a decimal.</param>
    /// <param name="right">Call or put on the foreign currency.</param>
    public static OptionInputs GarmanKohlhagen(
        double spotFxRate, double strike, double timeToExpiry,
        double volatility, double domesticRate, double foreignRate, OptionRight right)
        => new(spotFxRate, strike, timeToExpiry, volatility, domesticRate, domesticRate - foreignRate, right);
}
