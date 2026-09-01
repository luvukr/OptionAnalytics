namespace OptionAnalytics;

/// <summary>A projected price range at a given confidence.</summary>
/// <param name="Lower">Lower bound of the range.</param>
/// <param name="Upper">Upper bound of the range.</param>
/// <param name="Sigmas">How many standard deviations the bounds span.</param>
/// <param name="Confidence">
/// The probability the terminal price falls inside the range, from the normal distribution —
/// about 0.6827 at one sigma, 0.9545 at two, 0.9973 at three.
/// </param>
public sealed record PriceRange(double Lower, double Upper, double Sigmas, double Confidence);

/// <summary>
/// Turns a volatility into an expected price range — the practical question a volatility number
/// exists to answer.
/// </summary>
/// <remarks>
/// <para>
/// A volatility of 12% means nothing at the point of a decision. "NIFTY has a 68% chance of
/// finishing between 24,100 and 25,500 by expiry" is the same fact in a form you can act on: it
/// tells an option writer which strikes are likely to expire worthless, and a position holder
/// where a stop sits outside ordinary noise rather than inside it. Zerodha Varsity's options
/// module builds both applications on exactly this arithmetic.
/// </para>
/// <para>
/// <b>Two constructions, and the difference matters at longer horizons.</b>
/// <see cref="Range"/> uses the lognormal form implied by the same geometric Brownian motion that
/// underlies <see cref="BlackScholesMerton"/> — prices cannot go negative and the distribution is
/// right-skewed. <see cref="SimpleRange"/> uses the arithmetic form taught in Varsity,
/// <c>price * (1 + mean +/- SD)</c>, which is easier to do in your head and agrees closely over a
/// week. They diverge as the horizon lengthens: over a year at 30% volatility the simple lower
/// bound is materially too low, and at high enough volatility it goes negative, which is not a
/// price. Use the simple form for the intraday-to-monthly horizons it was taught for, and the
/// lognormal form beyond that.
/// </para>
/// <para>
/// <b>What this cannot tell you.</b> The range is only as good as the volatility fed in, and a
/// historical volatility is a statement about the past. Realised moves have fatter tails than the
/// normal distribution: three-sigma days occur far more often than 0.27% of the time, so a
/// "99.7%" range is not 99.7% safe in the way the arithmetic suggests. Treat these bounds as a
/// scale for ordinary movement, not a bound on catastrophe — which is precisely why an option
/// writer who sells beyond two sigma still needs a position limit.
/// </para>
/// </remarks>
public static class ExpectedMove
{
    /// <summary>Trading days in a year, the usual annualisation base for equity volatility.</summary>
    public const double DefaultTradingDaysPerYear = 252.0;

    /// <summary>
    /// Rescales a volatility from one horizon to another by the square-root-of-time rule.
    /// </summary>
    /// <remarks>
    /// Variance accumulates linearly in time under independent returns, so volatility — its square
    /// root — scales as the square root of time. This is the step behind "daily volatility times
    /// the square root of the number of days", and it is also why a volatility quoted without its
    /// horizon is meaningless.
    /// </remarks>
    /// <param name="volatility">The volatility to rescale, as a decimal.</param>
    /// <param name="fromPeriods">The horizon it is currently expressed over. Must be greater than zero.</param>
    /// <param name="toPeriods">The horizon to express it over. Must be greater than zero.</param>
    /// <returns>The rescaled volatility, or <c>null</c> if any input is not finite or positive.</returns>
    public static double? Scale(double volatility, double fromPeriods, double toPeriods)
    {
        if (!double.IsFinite(volatility) || volatility < 0) return null;
        if (!double.IsFinite(fromPeriods) || fromPeriods <= 0) return null;
        if (!double.IsFinite(toPeriods) || toPeriods <= 0) return null;

        return volatility * Math.Sqrt(toPeriods / fromPeriods);
    }

    /// <summary>
    /// Converts an annualised volatility to the volatility over a given number of trading days.
    /// </summary>
    /// <param name="annualisedVolatility">Annualised volatility as a decimal.</param>
    /// <param name="days">Number of trading days. Must be greater than zero.</param>
    /// <param name="tradingDaysPerYear">Annualisation base; defaults to 252.</param>
    /// <returns>The volatility over that horizon, or <c>null</c> on invalid input.</returns>
    public static double? OverDays(
        double annualisedVolatility, double days, double tradingDaysPerYear = DefaultTradingDaysPerYear)
        => Scale(annualisedVolatility, tradingDaysPerYear, days);

    /// <summary>
    /// The probability that a normal variate falls within a given number of standard deviations.
    /// </summary>
    /// <param name="sigmas">Number of standard deviations. Must be non-negative.</param>
    /// <returns>The two-sided confidence, or <c>null</c> if <paramref name="sigmas"/> is invalid.</returns>
    public static double? ConfidenceForSigmas(double sigmas)
    {
        if (!double.IsFinite(sigmas) || sigmas < 0) return null;

        return 1.0 - 2.0 * NormalDistribution.Cdf(-sigmas);
    }

    /// <summary>
    /// The expected price range under the lognormal model — the same distribution
    /// <see cref="BlackScholesMerton"/> prices against.
    /// </summary>
    /// <remarks>
    /// Bounds are <c>S * exp((drift - sigma^2 / 2) * T +/- k * sigma * sqrt(T))</c>. The
    /// <c>-sigma^2 / 2</c> term is the Ito correction: the median of a lognormal sits below its
    /// mean, so omitting it biases both bounds upward. Bounds from this method are always
    /// positive, which the arithmetic form cannot guarantee.
    /// </remarks>
    /// <param name="spot">Current price. Must be greater than zero.</param>
    /// <param name="annualisedVolatility">Annualised volatility as a decimal. Must be non-negative.</param>
    /// <param name="days">Horizon in trading days. Must be greater than zero.</param>
    /// <param name="sigmas">How many standard deviations to span. Defaults to 1.</param>
    /// <param name="annualisedDrift">
    /// Expected annualised return as a decimal. Defaults to zero — the honest choice, since a
    /// drift estimated from a short history is mostly noise and tilts both bounds.
    /// </param>
    /// <param name="tradingDaysPerYear">Annualisation base; defaults to 252.</param>
    /// <returns>The range, or <c>null</c> on invalid input.</returns>
    public static PriceRange? Range(
        double spot,
        double annualisedVolatility,
        double days,
        double sigmas = 1.0,
        double annualisedDrift = 0.0,
        double tradingDaysPerYear = DefaultTradingDaysPerYear)
    {
        if (!double.IsFinite(spot) || spot <= 0) return null;
        if (!double.IsFinite(annualisedVolatility) || annualisedVolatility < 0) return null;
        if (!double.IsFinite(annualisedDrift)) return null;
        if (!double.IsFinite(days) || days <= 0) return null;
        if (!double.IsFinite(tradingDaysPerYear) || tradingDaysPerYear <= 0) return null;
        if (!double.IsFinite(sigmas) || sigmas < 0) return null;

        double years = days / tradingDaysPerYear;
        double sigmaRootT = annualisedVolatility * Math.Sqrt(years);
        double median = (annualisedDrift - 0.5 * annualisedVolatility * annualisedVolatility) * years;

        return new PriceRange(
            Lower: spot * Math.Exp(median - sigmas * sigmaRootT),
            Upper: spot * Math.Exp(median + sigmas * sigmaRootT),
            Sigmas: sigmas,
            Confidence: ConfidenceForSigmas(sigmas)!.Value);
    }

    /// <summary>
    /// The expected price range in the arithmetic form taught in Zerodha Varsity's options module:
    /// <c>price * (1 + mean +/- SD)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept because it is what a large number of Indian options traders have actually been taught,
    /// and because over the horizons it is used for — a week to an expiry — it agrees with
    /// <see cref="Range"/> closely enough that arguing about it would be pedantry.
    /// </para>
    /// <para>
    /// Note that the drift is ADDED to both bounds: the range is centred on the drifted price, not
    /// on today's price. Varsity's worked example makes this explicit — NIFTY at 8462 with a
    /// 16-day mean of 0.65% and a 16-day standard deviation of 3.567% gives 8818 and 8214, and
    /// only the centred-on-drift reading reproduces the lower figure.
    /// </para>
    /// <para>
    /// <b>Its limits are real.</b> Because it is linear in the standard deviation, a long horizon
    /// or a high volatility drives the lower bound toward and past zero — at 30% annualised
    /// volatility over one year, two sigma puts it below zero, which is not a price. Prefer
    /// <see cref="Range"/> beyond a month or two.
    /// </para>
    /// </remarks>
    /// <param name="spot">Current price. Must be greater than zero.</param>
    /// <param name="dailyVolatility">
    /// Volatility over ONE day, as a decimal — not annualised. Use
    /// <see cref="OverDays"/> with <c>days: 1</c> to convert.
    /// </param>
    /// <param name="days">Horizon in trading days. Must be greater than zero.</param>
    /// <param name="sigmas">How many standard deviations to span. Defaults to 1.</param>
    /// <param name="dailyMeanReturn">Mean return over ONE day, as a decimal. Defaults to zero.</param>
    /// <returns>The range, or <c>null</c> on invalid input.</returns>
    public static PriceRange? SimpleRange(
        double spot,
        double dailyVolatility,
        double days,
        double sigmas = 1.0,
        double dailyMeanReturn = 0.0)
    {
        if (!double.IsFinite(spot) || spot <= 0) return null;
        if (!double.IsFinite(dailyVolatility) || dailyVolatility < 0) return null;
        if (!double.IsFinite(dailyMeanReturn)) return null;
        if (!double.IsFinite(days) || days <= 0) return null;
        if (!double.IsFinite(sigmas) || sigmas < 0) return null;

        double periodSd = dailyVolatility * Math.Sqrt(days);
        double periodMean = dailyMeanReturn * days;

        return new PriceRange(
            Lower: spot * (1.0 + periodMean - sigmas * periodSd),
            Upper: spot * (1.0 + periodMean + sigmas * periodSd),
            Sigmas: sigmas,
            Confidence: ConfidenceForSigmas(sigmas)!.Value);
    }
}
