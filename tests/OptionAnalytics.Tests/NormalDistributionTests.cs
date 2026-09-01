using FluentAssertions;
using Xunit;

namespace OptionAnalytics.Tests;

/// <summary>
/// The accuracy claim in <see cref="NormalDistribution"/>'s doc comment, asserted rather than
/// asserted-in-prose.
/// </summary>
/// <remarks>
/// Reference values are computed independently in arbitrary precision and pasted here, so these
/// tests do not depend on the implementation they are checking.
/// </remarks>
public class NormalDistributionTests
{
    // Phi(x) to 17 significant figures. Independently computed; deliberately includes the deep
    // tail, which is where the usual polynomial approximations fail outright.
    public static TheoryData<double, double> KnownCdfValues => new()
    {
        { -10.0, 7.6198530241605945e-24 },
        { -8.0, 6.2209605742717841e-16 },
        { -6.0, 9.8658764503769814e-10 },
        { -3.0, 1.3498980316300946e-03 },
        { -1.0, 1.5865525393145705e-01 },
        { -0.5, 3.0853753872598689e-01 },
        { 0.0, 5.0000000000000000e-01 },
        { 0.5, 6.9146246127401311e-01 },
        { 1.0, 8.4134474606854293e-01 },
        { 1.959963984540054, 9.7500000000000000e-01 },
        { 3.0, 9.9865010196836990e-01 },
    };

    [Theory]
    [MemberData(nameof(KnownCdfValues))]
    public void Cdf_MatchesIndependentlyComputedValues(double x, double expected)
    {
        NormalDistribution.Cdf(x).Should().BeApproximately(expected, Math.Abs(expected) * 1e-13);
    }

    [Fact]
    public void Cdf_IsAccurateInTheDeepTail_WhereFittedPolynomialsCollapse()
    {
        // Abramowitz & Stegun 7.1.26, the approximation most option-pricing code uses, has a
        // maximum ABSOLUTE error near 1.5e-7. At x = -10 the true probability is 7.6e-24, so an
        // absolute error of that size does not merely lose precision -- A&S returns exactly zero.
        // Deep out-of-the-money prices are computed from exactly this region.
        double deepTail = NormalDistribution.Cdf(-10.0);

        deepTail.Should().BeGreaterThan(0.0, "the probability is small, not zero");
        deepTail.Should().BeApproximately(7.6198530241605945e-24, 7.62e-24 * 1e-13);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.5)]
    [InlineData(7.0)]
    [InlineData(15.0)]
    public void Cdf_IsSymmetric(double x)
    {
        // Phi(x) + Phi(-x) == 1 exactly, to rounding. Fails immediately if either tail is being
        // computed as one-minus-the-other.
        (NormalDistribution.Cdf(x) + NormalDistribution.Cdf(-x))
            .Should().BeApproximately(1.0, 1e-15);
    }

    [Fact]
    public void Cdf_IsMonotonicAcrossTheRealLine()
    {
        double previous = 0.0;
        for (double x = -20; x <= 20; x += 0.01)
        {
            double value = NormalDistribution.Cdf(x);
            value.Should().BeGreaterThanOrEqualTo(previous, $"at x={x}");
            previous = value;
        }
    }

    [Fact]
    public void Erfc_MatchesTheCdfRelationship()
    {
        for (double x = -8; x <= 8; x += 0.05)
        {
            NormalDistribution.Cdf(x)
                .Should().BeApproximately(0.5 * NormalDistribution.Erfc(-x / Math.Sqrt(2.0)),
                    Math.Max(NormalDistribution.Cdf(x) * 1e-14, double.Epsilon));
        }
    }

    [Fact]
    public void ErfAndErfc_SumToOne()
    {
        for (double x = -4; x <= 4; x += 0.05)
            (NormalDistribution.Erf(x) + NormalDistribution.Erfc(x)).Should().BeApproximately(1.0, 1e-15);
    }

    [Fact]
    public void Pdf_MatchesKnownValues()
    {
        NormalDistribution.Pdf(0.0).Should().BeApproximately(0.39894228040143268, 1e-15);
        NormalDistribution.Pdf(1.0).Should().BeApproximately(0.24197072451914337, 1e-15);
        NormalDistribution.Pdf(-1.0).Should().Be(NormalDistribution.Pdf(1.0), "the density is even");
    }

    [Theory]
    [InlineData(1e-12)]
    [InlineData(0.001)]
    [InlineData(0.025)]
    [InlineData(0.5)]
    [InlineData(0.975)]
    [InlineData(0.999)]
    public void InverseCdf_RoundTripsThroughCdf(double p)
    {
        double x = NormalDistribution.InverseCdf(p);

        NormalDistribution.Cdf(x).Should().BeApproximately(p, Math.Max(p * 1e-12, 1e-15));
    }

    [Fact]
    public void InverseCdf_MatchesTheStandardCriticalValue()
    {
        NormalDistribution.InverseCdf(0.975).Should().BeApproximately(1.959963984540054, 1e-12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void InverseCdf_OutsideTheUnitInterval_IsNaN(double p)
    {
        double.IsNaN(NormalDistribution.InverseCdf(p)).Should().BeTrue();
    }

    [Fact]
    public void NonFiniteInputsBehaveSensibly()
    {
        double.IsNaN(NormalDistribution.Cdf(double.NaN)).Should().BeTrue();
        NormalDistribution.Cdf(double.PositiveInfinity).Should().Be(1.0);
        NormalDistribution.Cdf(double.NegativeInfinity).Should().Be(0.0);
        NormalDistribution.Pdf(double.PositiveInfinity).Should().Be(0.0);
        NormalDistribution.Erfc(double.NegativeInfinity).Should().Be(2.0);
    }
}
