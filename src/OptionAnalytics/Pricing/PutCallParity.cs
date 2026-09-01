namespace OptionAnalytics;

/// <summary>
/// Put-call parity: the model-free relationship between a call, a put, the underlying and a bond.
/// </summary>
/// <remarks>
/// <para>
/// <c>C - P = S * exp((b - r) * T) - K * exp(-r * T)</c>
/// </para>
/// <para>
/// <b>This is not a Black-Scholes result.</b> It follows from a static arbitrage — long a call,
/// short a put, and you hold a synthetic forward — so it holds for any model that admits no
/// arbitrage, whatever it assumes about volatility. That makes it the sharpest available check on
/// an implementation: a pricer can be wrong in a way no single price reveals, and still be caught
/// here, because parity constrains the two sides against each other. The tests use it exactly that
/// way.
/// </para>
/// <para>
/// It is also useful in its own right. Given a liquid call and an illiquid put at the same strike
/// and expiry, <see cref="PutFromCall"/> gives the put's arbitrage-free value without needing a
/// volatility for it at all — often a better number than back-solving a wide or stale quote.
/// </para>
/// </remarks>
public static class PutCallParity
{
    /// <summary>
    /// The value of <c>C - P</c> implied by parity, for a call and put sharing strike and expiry.
    /// </summary>
    /// <param name="inputs">
    /// Contract and market state. <see cref="OptionInputs.Right"/> and
    /// <see cref="OptionInputs.Volatility"/> are both irrelevant here and ignored — parity does
    /// not depend on either.
    /// </param>
    /// <returns>
    /// <c>S * exp((b - r) * T) - K * exp(-r * T)</c>, or <c>null</c> if the inputs are invalid.
    /// </returns>
    public static double? ForwardMinusStrike(OptionInputs inputs)
    {
        if (inputs is null || !inputs.IsValid) return null;

        double carryDiscount = Math.Exp((inputs.CostOfCarry - inputs.RiskFreeRate) * inputs.TimeToExpiry);
        double discount = Math.Exp(-inputs.RiskFreeRate * inputs.TimeToExpiry);

        return inputs.Spot * carryDiscount - inputs.Strike * discount;
    }

    /// <summary>
    /// The arbitrage-free put price implied by an observed call price at the same strike and expiry.
    /// </summary>
    /// <param name="callPrice">Observed price of the call.</param>
    /// <param name="inputs">Contract and market state; <c>Right</c> and <c>Volatility</c> are ignored.</param>
    /// <returns>The implied put price, or <c>null</c> if the inputs are invalid.</returns>
    public static double? PutFromCall(double callPrice, OptionInputs inputs)
    {
        double? parity = ForwardMinusStrike(inputs);
        if (parity is null || !double.IsFinite(callPrice)) return null;

        return callPrice - parity.Value;
    }

    /// <summary>
    /// The arbitrage-free call price implied by an observed put price at the same strike and expiry.
    /// </summary>
    /// <param name="putPrice">Observed price of the put.</param>
    /// <param name="inputs">Contract and market state; <c>Right</c> and <c>Volatility</c> are ignored.</param>
    /// <returns>The implied call price, or <c>null</c> if the inputs are invalid.</returns>
    public static double? CallFromPut(double putPrice, OptionInputs inputs)
    {
        double? parity = ForwardMinusStrike(inputs);
        if (parity is null || !double.IsFinite(putPrice)) return null;

        return putPrice + parity.Value;
    }

    /// <summary>
    /// How far an observed call/put pair departs from parity, in price units.
    /// </summary>
    /// <remarks>
    /// A non-zero residual on real quotes is normal and mostly mechanical — bid/ask, a stale leg,
    /// dividends the carry rate does not capture, borrow costs. A LARGE residual usually means one
    /// leg is not a real price, which is worth knowing before back-solving a volatility from it.
    /// This reports the number and takes no view on how large is too large; that depends on the
    /// market and the spread.
    /// </remarks>
    /// <param name="callPrice">Observed call price.</param>
    /// <param name="putPrice">Observed put price at the same strike and expiry.</param>
    /// <param name="inputs">Contract and market state; <c>Right</c> and <c>Volatility</c> are ignored.</param>
    /// <returns>
    /// <c>(C - P) - (S * exp((b - r) * T) - K * exp(-r * T))</c>: positive when the call is rich
    /// relative to the put. <c>null</c> if the inputs are invalid.
    /// </returns>
    public static double? Violation(double callPrice, double putPrice, OptionInputs inputs)
    {
        double? parity = ForwardMinusStrike(inputs);
        if (parity is null || !double.IsFinite(callPrice) || !double.IsFinite(putPrice)) return null;

        return (callPrice - putPrice) - parity.Value;
    }
}
