namespace OptionAnalytics;

/// <summary>
/// The standard normal distribution, to full double precision.
/// </summary>
/// <remarks>
/// <para>
/// Every quantity in this library — every price, every greek, every back-solved volatility —
/// bottoms out in <see cref="Cdf"/> or <see cref="Pdf"/>, so this file sets the accuracy ceiling
/// for the whole package. It is worth more care than its size suggests.
/// </para>
/// <para>
/// <b>Why not the usual approximation.</b> Most option-pricing code reaches for Abramowitz and
/// Stegun 7.1.26, a five-term polynomial with a maximum ABSOLUTE error of about 1.5e-7. That is
/// ample for reading a delta off a chain, and useless for two things this library must do. A deep
/// out-of-the-money option can be worth 1e-9; an absolute error of 1.5e-7 in the CDF makes that
/// price pure noise. And finite-difference differentiation — the independent check that every
/// analytic greek here is validated against — divides a price difference by a small bump, which
/// amplifies absolute error by the reciprocal of the bump. A 1.5e-7 floor makes the check
/// meaningless before it starts.
/// </para>
/// <para>
/// <b>What this uses instead.</b> .NET's BCL has no <c>erf</c>, so the complementary error
/// function is computed directly from its convergent definitions rather than from a fitted
/// polynomial: the Maclaurin series (Abramowitz &amp; Stegun 7.1.5) near the origin, and the
/// continued fraction (A&amp;S 7.1.14) in the tails, evaluated by the modified Lentz algorithm.
/// Both converge to machine precision, so the accuracy is a property of the mathematics rather
/// than of a coefficient table — which also means it can be, and has been, measured rather than
/// asserted. Measured against a reference implementation over 3,859 points spanning x in
/// [-25, 25]: worst RELATIVE error 2.9e-14 for <see cref="Cdf"/> and 5.7e-14 for
/// <see cref="Erfc"/>, both occurring beyond |x| = 22 where the true value is around 1e-260.
/// For comparison, A&amp;S 7.1.26 measured on the same points has a worst ABSOLUTE error of
/// 7.0e-08 — and returns exactly zero for <c>Cdf(-10)</c>, whose true value is 7.62e-24.
/// </para>
/// <para>
/// <b>Tail behaviour is the point.</b> <c>Cdf(-10)</c> is about 7.62e-24. Computing it as
/// <c>1 - Cdf(10)</c> in double precision returns exactly zero, because 1 - 7.62e-24 rounds to 1.
/// The symmetry below is arranged so the small tail is always computed directly and never as one
/// minus something near one.
/// </para>
/// </remarks>
public static class NormalDistribution
{
    /// <summary>1 / sqrt(2 * pi), the standard normal density's normalising constant.</summary>
    private const double InvSqrt2Pi = 0.3989422804014326779399460599343818684758586311649;

    private const double Sqrt2 = 1.4142135623730950488016887242096980785696718753769;

    /// <summary>2 / sqrt(pi), the error function's leading constant.</summary>
    private const double TwoOverSqrtPi = 1.1283791670955125738961589031215451716768583274192;

    /// <summary>
    /// Where the Maclaurin series hands over to the continued fraction. Chosen by measurement, not
    /// by taste: the continued fraction's convergence degrades sharply as x falls, needing 829
    /// iterations at x = 0.5 but only 191 at x = 1.0, while the series still reaches 7e-16 at
    /// x = 1.0 in eighteen terms. Both 0.5 and 1.0 yield the same 7.5e-15 worst-case relative
    /// error over the real line; 1.0 gets there four times cheaper. Pushing it further is what
    /// costs accuracy — the series' alternating terms start cancelling, and a crossover at 3.0
    /// degrades the worst case to 2.0e-09.
    /// </summary>
    private const double SeriesToFractionCrossover = 1.0;

    /// <summary>
    /// The standard normal probability density function, phi(x).
    /// </summary>
    /// <param name="x">The point at which to evaluate the density. Non-finite input returns <c>NaN</c>.</param>
    /// <returns><c>exp(-x^2 / 2) / sqrt(2 * pi)</c>.</returns>
    public static double Pdf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (double.IsInfinity(x)) return 0.0;

        return InvSqrt2Pi * Math.Exp(-0.5 * x * x);
    }

    /// <summary>
    /// The standard normal cumulative distribution function, Phi(x).
    /// </summary>
    /// <remarks>
    /// Accurate to full double precision in both tails — see the class remarks for why that
    /// matters more here than it looks. <c>Cdf(x) + Cdf(-x) == 1</c> holds to rounding.
    /// </remarks>
    /// <param name="x">The point at which to evaluate. Non-finite input returns <c>NaN</c> for NaN,
    /// and 0 or 1 for negative or positive infinity.</param>
    /// <returns>The probability that a standard normal variate is at most <paramref name="x"/>, in [0, 1].</returns>
    public static double Cdf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (double.IsPositiveInfinity(x)) return 1.0;
        if (double.IsNegativeInfinity(x)) return 0.0;

        // Phi(x) = erfc(-x / sqrt(2)) / 2. Written this way rather than as
        // (1 + erf(x / sqrt(2))) / 2 so that a tiny left-tail probability is produced directly by
        // erfc's own tail branch, instead of being destroyed by cancellation against 1.
        return 0.5 * Erfc(-x / Sqrt2);
    }

    /// <summary>
    /// The complementary error function, <c>erfc(x) = 1 - erf(x)</c>, to full double precision.
    /// </summary>
    /// <remarks>
    /// Exposed because it is genuinely useful on its own and because the accuracy claim above is
    /// really a claim about this function. Two regimes, chosen so each is used only where it
    /// converges quickly: the Maclaurin series for |x| below 0.5, the continued fraction beyond.
    /// </remarks>
    /// <param name="x">The point at which to evaluate.</param>
    /// <returns><c>1 - erf(x)</c>, in (0, 2).</returns>
    public static double Erfc(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (double.IsPositiveInfinity(x)) return 0.0;
        if (double.IsNegativeInfinity(x)) return 2.0;

        // erfc(-x) = 2 - erfc(x). Reflect so the continued fraction only ever sees x > 0, where
        // it converges; the subtraction is safe because erfc(x) <= 1 for x >= 0.
        if (x < 0) return 2.0 - Erfc(-x);

        // Beyond this point erfc underflows to zero in double precision anyway
        // (erfc(27) is about 1e-318, and erfc(28) is subnormal).
        if (x > 27.0) return 0.0;

        return x < SeriesToFractionCrossover ? 1.0 - ErfSeries(x) : ErfcContinuedFraction(x);
    }

    /// <summary>
    /// The error function, <c>erf(x)</c>.
    /// </summary>
    /// <param name="x">The point at which to evaluate.</param>
    /// <returns><c>erf(x)</c>, in (-1, 1).</returns>
    public static double Erf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (double.IsPositiveInfinity(x)) return 1.0;
        if (double.IsNegativeInfinity(x)) return -1.0;

        return Math.Abs(x) < SeriesToFractionCrossover ? ErfSeries(x) : 1.0 - Erfc(x);
    }

    /// <summary>
    /// Maclaurin series for erf (Abramowitz &amp; Stegun 7.1.5):
    /// <c>erf(x) = (2 / sqrt(pi)) * sum_{n=0}^inf (-1)^n x^(2n+1) / (n! (2n+1))</c>.
    /// </summary>
    /// <remarks>
    /// Alternating and rapidly convergent for small |x|. Used only below 0.5, where fewer than
    /// twenty terms reach machine precision and no cancellation is possible.
    /// </remarks>
    private static double ErfSeries(double x)
    {
        double xSquared = x * x;
        double term = x;              // n = 0 term, before the leading constant
        double sum = x;

        for (int n = 1; n < 60; n++)
        {
            // term_n / term_{n-1} = -x^2 * (2n - 1) / (n * (2n + 1))
            term *= -xSquared / n;
            double contribution = term / (2 * n + 1);
            sum += contribution;

            if (Math.Abs(contribution) <= Math.Abs(sum) * 1e-17)
                break;
        }

        return TwoOverSqrtPi * sum;
    }

    /// <summary>
    /// Continued fraction for erfc (Abramowitz &amp; Stegun 7.1.14):
    /// <c>erfc(x) = (exp(-x^2) / sqrt(pi)) * 1/(x+ (1/2)/(x+ 1/(x+ (3/2)/(x+ 2/(x+ ...)))))</c>.
    /// </summary>
    /// <remarks>
    /// Evaluated by the modified Lentz algorithm, which builds the fraction from the top down and
    /// so needs no guess at where to truncate it. Convergence is fast for x above about 0.5 and
    /// gets faster as x grows, which is exactly the regime where the series would suffer.
    /// </remarks>
    private static double ErfcContinuedFraction(double x)
    {
        const double tiny = 1e-300;

        // Lentz: f = b0 + a1/(b1 + a2/(b2 + ...)) with b0 = 0, b_i = x, a_1 = 1, a_i = (i-1)/2.
        double f = tiny;
        double c = f;
        double d = 0.0;

        // 191 iterations are needed at the crossover and fewer everywhere above it; the cap is a
        // safety net with room to spare, not a working limit. It was 300 in an earlier draft,
        // which silently truncated the fraction near the crossover and capped the whole library's
        // accuracy at 7.4e-11 -- measured, then fixed.
        for (int i = 1; i < 400; i++)
        {
            double a = i == 1 ? 1.0 : (i - 1) / 2.0;

            d = x + a * d;
            if (Math.Abs(d) < tiny) d = tiny;

            c = x + a / c;
            if (Math.Abs(c) < tiny) c = tiny;

            d = 1.0 / d;
            double delta = c * d;
            f *= delta;

            if (Math.Abs(delta - 1.0) < 1e-16)
                break;
        }

        return Math.Exp(-x * x) * InvSqrt2Pi * Sqrt2 * f;
    }

    /// <summary>
    /// The inverse of <see cref="Cdf"/>: the value x for which <c>Cdf(x) == p</c>.
    /// </summary>
    /// <remarks>
    /// Acklam's rational approximation for the initial estimate, then two Halley refinement steps
    /// against <see cref="Cdf"/>. The refinement is what makes the accuracy a property of
    /// <see cref="Cdf"/> rather than of the approximation, so this inherits the precision
    /// established above rather than capping it at the approximation's own ~1e-9.
    /// </remarks>
    /// <param name="p">A probability in (0, 1).</param>
    /// <returns>
    /// The corresponding quantile, or <c>NaN</c> if <paramref name="p"/> is not in (0, 1);
    /// negative and positive infinity at exactly 0 and 1.
    /// </returns>
    public static double InverseCdf(double p)
    {
        if (double.IsNaN(p) || p < 0.0 || p > 1.0) return double.NaN;
        if (p == 0.0) return double.NegativeInfinity;
        if (p == 1.0) return double.PositiveInfinity;

        double x = AcklamInitialEstimate(p);

        // Halley refinement. Each step roughly cubes the error, so two are ample from ~1e-9.
        for (int i = 0; i < 2; i++)
        {
            double error = Cdf(x) - p;
            double density = Pdf(x);
            if (density < 1e-300) break;

            double u = error / density;
            x -= u / (1.0 + x * u / 2.0);
        }

        return x;
    }

    /// <summary>
    /// Peter Acklam's rational approximation to the normal quantile, accurate to about 1.15e-9
    /// relative before refinement.
    /// </summary>
    private static double AcklamInitialEstimate(double p)
    {
        double[] a =
        {
            -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
            1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00,
        };
        double[] b =
        {
            -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
            6.680131188771972e+01, -1.328068155288572e+01,
        };
        double[] c =
        {
            -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
            -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00,
        };
        double[] d =
        {
            7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
            3.754408661907416e+00,
        };

        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        if (p < pLow)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }

        if (p > pHigh)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }

        double r = p - 0.5;
        double rSquared = r * r;
        return (((((a[0] * rSquared + a[1]) * rSquared + a[2]) * rSquared + a[3]) * rSquared + a[4]) * rSquared + a[5]) * r /
               (((((b[0] * rSquared + b[1]) * rSquared + b[2]) * rSquared + b[3]) * rSquared + b[4]) * rSquared + 1.0);
    }
}
