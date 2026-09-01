namespace OptionAnalytics;

/// <summary>
/// How greeks are scaled for display. Every value is a pure unit conversion; none of them changes
/// the mathematics.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because scaling is where implementations silently disagree.</b> The raw
/// derivatives are unambiguous: vega is <c>dV/dsigma</c> per one full unit of volatility, theta is
/// <c>dV/dT</c> per year. Almost nobody quotes them that way. Trading platforms quote vega per one
/// volatility POINT (a hundredth of the derivative) and theta per DAY (a 365th or a 252nd of it),
/// and rho per one basis point or per one percent depending on the desk. Two correct
/// implementations can therefore differ by a factor of 100 or 365 and both be right.
/// </para>
/// <para>
/// The mathematics in <see cref="BlackScholesMerton"/> returns the raw derivative always. This
/// record is the only place a convention is applied, so a number's units are always traceable to
/// an explicit choice rather than to a constant buried in a formula.
/// </para>
/// <para>
/// <b>On calendar versus trading days.</b> <see cref="Default"/> divides theta by 365, not 252,
/// because an option decays over calendar time — a weekend costs two days of extinsic value even
/// though no session occurs. Use 252 only if your volatility is quoted in trading-day terms and
/// you want the two to agree; mixing a 365-day theta with a 252-day volatility is a real and
/// common inconsistency.
/// </para>
/// </remarks>
/// <param name="DaysPerYear">
/// Days theta is divided by. 365 (calendar, the default) or 252 (trading) are the usual choices.
/// Must be greater than zero.
/// </param>
/// <param name="VolatilityPointsPerUnit">
/// What one quoted volatility "point" is worth as a fraction. 100 (the default) means vega is
/// reported per 1% of volatility; 1 leaves vega as the raw derivative.
/// </param>
/// <param name="RatePointsPerUnit">
/// What one quoted rate "point" is worth as a fraction. 100 (the default) means rho is reported
/// per 1% of rate; 10,000 reports it per basis point.
/// </param>
public sealed record GreekConventions(
    double DaysPerYear = 365.0,
    double VolatilityPointsPerUnit = 100.0,
    double RatePointsPerUnit = 100.0)
{
    /// <summary>
    /// The conventions most trading platforms use: theta per calendar day, vega per volatility
    /// point, rho per percentage point of rate.
    /// </summary>
    public static GreekConventions Default { get; } = new();

    /// <summary>
    /// The raw derivatives, unscaled: theta per year, vega per unit volatility, rho per unit rate.
    /// </summary>
    /// <remarks>
    /// Use this when comparing against a textbook, against another library's raw output, or when
    /// feeding a risk model that expects derivatives rather than display quantities.
    /// </remarks>
    public static GreekConventions Raw { get; } = new(DaysPerYear: 1.0, VolatilityPointsPerUnit: 1.0, RatePointsPerUnit: 1.0);

    /// <summary>Throws if any divisor would produce a meaningless or infinite scaling.</summary>
    /// <exception cref="ArgumentException">If any value is not finite and greater than zero.</exception>
    public void Validate()
    {
        void Positive(double value, string name)
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentException($"{name} must be finite and greater than zero (was {value}).",
                    nameof(GreekConventions));
        }

        Positive(DaysPerYear, nameof(DaysPerYear));
        Positive(VolatilityPointsPerUnit, nameof(VolatilityPointsPerUnit));
        Positive(RatePointsPerUnit, nameof(RatePointsPerUnit));
    }
}

/// <summary>
/// A complete set of analytic greeks for one contract, as RAW derivatives.
/// </summary>
/// <remarks>
/// Values here are always the unscaled mathematical derivatives — theta per year, vega per unit of
/// volatility, rho per unit of rate. Call <see cref="Scaled"/> to convert to display conventions.
/// Keeping the record in raw units means the numbers compose and differentiate correctly, and the
/// conversion happens once, visibly, at the edge.
/// </remarks>
/// <param name="Price">The option's fair value.</param>
/// <param name="Delta">
/// dV/dS. In [-exp((b-r)T), exp((b-r)T)] -- NOT [-1, 1]. The carry discount factor exceeds one
/// whenever the carry exceeds the rate, so a deep in-the-money delta legitimately exceeds one; a
/// negative dividend yield or a foreign rate below the domestic one both do it.
/// </param>
/// <param name="Gamma">d2V/dS2. Never negative; identical for calls and puts.</param>
/// <param name="Vega">dV/dsigma per ONE FULL UNIT of volatility. Never negative; identical for calls and puts.</param>
/// <param name="Theta">dV/dT as decay, per YEAR.</param>
/// <param name="RhoCarryTracksRate">
/// dV/dr when the carry moves with the rate — the conventional equity and index rho. Correct for
/// <see cref="OptionModel.BlackScholes"/> and <see cref="OptionModel.Merton"/>.
/// </param>
/// <param name="RhoCarryFixed">
/// dV/dr when the carry is held fixed; equals <c>-T * Price</c>. Correct for
/// <see cref="OptionModel.Black76"/>. See <see cref="BlackScholesMerton.RhoCarryFixed"/> for why
/// two rhos exist.
/// </param>
/// <param name="Vanna">d2V/dS dsigma. Identical for calls and puts.</param>
/// <param name="Volga">d2V/dsigma2. Identical for calls and puts.</param>
/// <param name="Charm">d2V/dS dT, per YEAR. How fast delta drifts with time alone.</param>
public sealed record OptionGreeks(
    double Price,
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    double RhoCarryTracksRate,
    double RhoCarryFixed,
    double Vanna,
    double Volga,
    double Charm)
{
    /// <summary>
    /// Returns the same greeks converted to display conventions.
    /// </summary>
    /// <remarks>
    /// Delta, gamma and price are unaffected — they are already in natural units. Theta is divided
    /// by <see cref="GreekConventions.DaysPerYear"/>; vega, vanna and volga by the volatility
    /// scaling; both rhos by the rate scaling; charm by days per year, since it is a per-time
    /// derivative like theta.
    /// </remarks>
    /// <param name="conventions">The scaling to apply. Defaults to <see cref="GreekConventions.Default"/>.</param>
    /// <returns>A new record in the requested units.</returns>
    /// <exception cref="ArgumentException">If <paramref name="conventions"/> holds a non-positive divisor.</exception>
    public OptionGreeks Scaled(GreekConventions? conventions = null)
    {
        var c = conventions ?? GreekConventions.Default;
        c.Validate();

        return this with
        {
            Theta = Theta / c.DaysPerYear,
            Charm = Charm / c.DaysPerYear,
            Vega = Vega / c.VolatilityPointsPerUnit,
            Vanna = Vanna / c.VolatilityPointsPerUnit,
            Volga = Volga / (c.VolatilityPointsPerUnit * c.VolatilityPointsPerUnit),
            RhoCarryTracksRate = RhoCarryTracksRate / c.RatePointsPerUnit,
            RhoCarryFixed = RhoCarryFixed / c.RatePointsPerUnit,
        };
    }
}
