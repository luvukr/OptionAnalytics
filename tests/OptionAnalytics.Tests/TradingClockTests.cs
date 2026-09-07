using FluentAssertions;
using OptionAnalytics;
using Xunit;

namespace OptionAnalytics.Tests;

public class TradingClockTests
{
    private static readonly TradingSession Session = TradingSession.IndiaEquity;
    private static readonly DateTime Expiry = new(2026, 9, 8, 15, 30, 0);

    private const double Spot = 24_800, Vol = 0.13, Rate = 0.065, Carry = 0.053;

    private static OptionInputs Atm(double t, double vol = Vol)
        => new(Spot, Spot, t, vol, Rate, Carry, OptionRight.Call);

    // ---------------------------------------------------------------- the session

    [Fact]
    public void TheIndiaSessionIsThreeHundredAndSeventyFiveMinutes()
    {
        Session.Open.Should().Be(new TimeOnly(9, 15));
        Session.Close.Should().Be(new TimeOnly(15, 30));
        Session.Minutes.Should().Be(375);
        Session.IsValid.Should().BeTrue();

        new TradingSession(new TimeOnly(15, 30), new TimeOnly(9, 15)).IsValid.Should().BeFalse();
        new TradingSession(new TimeOnly(9, 15), new TimeOnly(9, 15)).IsValid.Should().BeFalse();
    }

    // ---------------------------------------------------------------- expiry day, the whole point

    [Theory]
    [InlineData(9.25, 375)]     // the open
    [InlineData(10.0, 330)]
    [InlineData(11.5, 240)]
    [InlineData(13.0, 150)]
    [InlineData(14.5, 60)]
    [InlineData(15.25, 15)]
    [InlineData(15.5, 0)]       // the bell
    public void TradingMinutesCountDownThroughExpiryDay(double hour, double expected)
    {
        var now = Expiry.Date.AddHours(hour);

        var t = TradingClock.Until(now, Expiry, Session, fullTradingDaysBetween: 0)!;

        t.TradingMinutesRemaining.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void TheNaiveDailyFigureIsZeroAllDayAndTheClockIsNot()
    {
        // The defect this class exists to remove. A whole-day quantity applied to an intraday
        // question reports an expired contract for the entire session.
        var now = Expiry.Date.AddHours(11.5);

        double naive = (Expiry.Date - now.Date).TotalDays / 365.0;
        naive.Should().Be(0.0, "which is the bug, not the library's fault");

        var naivePriced = Atm(naive);
        BlackScholesMerton.Price(naivePriced)!.Value.Should().Be(0.0);
        BlackScholesMerton.Gamma(naivePriced)!.Value.Should().Be(0.0);
        BlackScholesMerton.Vega(naivePriced)!.Value.Should().Be(0.0);
        ImpliedVolatility.Solve(20, naivePriced).Should().BeNull("no time value to invert");

        var t = TradingClock.Until(now, Expiry, Session, 0)!;
        var properlyPriced = Atm(t.CalendarYears);

        BlackScholesMerton.Price(properlyPriced)!.Value.Should().BeApproximately(27.79, 0.01);
        BlackScholesMerton.Vega(properlyPriced)!.Value.Should().BeApproximately(211.4, 0.5);
        BlackScholesMerton.Gamma(properlyPriced)!.Value.Should().BeGreaterThan(0.005);
        ImpliedVolatility.Solve(27.79, properlyPriced).Should().NotBeNull();
    }

    [Fact]
    public void GammaClimbsThroughExpiryDayInsteadOfBeingZero()
    {
        double previous = 0;
        foreach (var hour in new[] { 9.25, 11.5, 13.0, 14.5, 15.25 })
        {
            var t = TradingClock.Until(Expiry.Date.AddHours(hour), Expiry, Session, 0)!;
            double gamma = BlackScholesMerton.Gamma(Atm(t.CalendarYears))!.Value;

            gamma.Should().BeGreaterThan(previous, $"gamma must rise into expiry, at {hour}");
            previous = gamma;
        }

        previous.Should().BeGreaterThan(0.02, "the 0-DTE gamma explosion is real and large");
    }

    // ---------------------------------------------------------------- session boundaries

    [Fact]
    public void AnInstantOutsideTheSessionContributesNothingToTradingTime()
    {
        // Before the open, the whole of today is still ahead.
        TradingClock.Until(Expiry.Date.AddHours(8), Expiry, Session, 0)!
            .TradingMinutesRemaining.Should().Be(375);

        // After the close, today is spent -- so an expiry tomorrow is exactly one session away.
        TradingClock.Until(Expiry.Date.AddHours(17), Expiry.AddDays(1), Session, 0)!
            .TradingMinutesRemaining.Should().Be(375);
    }

    [Fact]
    public void AHolidayTodayContributesNothing()
    {
        var onAHoliday = Expiry.Date.AddDays(-1).AddHours(11);

        TradingClock.Until(onAHoliday, Expiry, Session, 0, nowIsOnATradingDay: false)!
            .TradingMinutesRemaining.Should().Be(375, "only the expiry day's session remains");

        TradingClock.Until(onAHoliday, Expiry, Session, 0, nowIsOnATradingDay: true)!
            .TradingMinutesRemaining.Should().Be(375 + 270, "a normal day would add its afternoon");
    }

    [Fact]
    public void MultipleDaysAreTodaysRemainderPlusWholeSessionsPlusTheExpiryMorning()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0);   // 330 minutes left today

        var t = TradingClock.Until(now, Expiry, Session, fullTradingDaysBetween: 4)!;

        t.TradingMinutesRemaining.Should().Be(330 + 4 * 375 + 375);
        t.SessionMinutes.Should().Be(375);
        t.TradingYears.Should().BeApproximately(2205.0 / (252.0 * 375.0), 1e-12);
        t.CalendarYears.Should().BeApproximately((Expiry - now).TotalDays / 365.0, 1e-12);
    }

    [Fact]
    public void AWeekendHasCalendarTimeButNoTradingTime()
    {
        // The distinction that calendar time cannot express: no further variance can accrue, while
        // the discount factor still has a weekend to unwind.
        var fridayClose = new DateTime(2026, 9, 4, 15, 30, 0);
        var mondayOpen = new DateTime(2026, 9, 7, 9, 15, 0);

        var t = TradingClock.Until(fridayClose, mondayOpen, Session, fullTradingDaysBetween: 0)!;

        t.CalendarYears.Should().BeGreaterThan(0.0);
        (t.CalendarYears * 365).Should().BeApproximately(2.74, 0.01);
        t.TradingMinutesRemaining.Should().Be(0);
        t.TradingYears.Should().Be(0.0);
        t.NoTradingTimeLeft.Should().BeTrue();
    }

    // ---------------------------------------------------------------- the two clocks

    [Fact]
    public void TheVolatilityConversionPreservesTotalVarianceExactly()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0);
        var t = TradingClock.Until(now, Expiry, Session, 4)!;

        double tradingVol = t.VolatilityInTradingTime(Vol)!.Value;

        double calendarVariance = Vol * Vol * t.CalendarYears;
        double tradingVariance = tradingVol * tradingVol * t.TradingYears;

        tradingVariance.Should().BeApproximately(calendarVariance, calendarVariance * 1e-12);

        // Which means the diffusion term is the same on both clocks, to the digit.
        (Vol * Math.Sqrt(t.CalendarYears))
            .Should().BeApproximately(tradingVol * Math.Sqrt(t.TradingYears), 1e-14);
    }

    [Fact]
    public void SwitchingClocksLeavesGammaAloneButMisprisesByChargingInterestOverTheWrongSpan()
    {
        // The measured limitation, pinned. OptionInputs has one TimeToExpiry serving both the
        // diffusion and r*T, so a trading-time value gets the variance right and the discounting
        // wrong. Proof that the gap is entirely discounting: rescaling the rate and carry by the
        // ratio of the clocks reproduces the calendar price to the bit.
        var now = new DateTime(2026, 9, 1, 10, 0, 0);
        var t = TradingClock.Until(now, Expiry, Session, 4)!;
        double tradingVol = t.VolatilityInTradingTime(Vol)!.Value;

        var onCalendar = Atm(t.CalendarYears);
        var onTrading = Atm(t.TradingYears, tradingVol);

        double calendarPrice = BlackScholesMerton.Price(onCalendar)!.Value;
        double tradingPrice = BlackScholesMerton.Price(onTrading)!.Value;

        // Gamma barely moves: the residual is the carry factor, not the diffusion.
        (BlackScholesMerton.Gamma(onTrading)!.Value / BlackScholesMerton.Gamma(onCalendar)!.Value)
            .Should().BeApproximately(1.0, 0.002);

        // The price does move, and by enough to matter.
        double error = (tradingPrice - calendarPrice) / calendarPrice;
        error.Should().BeApproximately(0.0124, 0.001, "measured at 1.24% on this contract");

        // And it is entirely r*T and b*T.
        double ratio = t.CalendarYears / t.TradingYears;
        var rescaled = onTrading with { RiskFreeRate = Rate * ratio, CostOfCarry = Carry * ratio };

        BlackScholesMerton.Price(rescaled)!.Value.Should().BeApproximately(calendarPrice, 1e-9,
            "which proves the diffusion was never the problem");
    }

    [Fact]
    public void TheConversionRefusesWhatItCannotAnnualise()
    {
        var t = TradingClock.Until(new DateTime(2026, 9, 1, 10, 0, 0), Expiry, Session, 4)!;

        t.VolatilityInTradingTime(-0.01).Should().BeNull();
        t.VolatilityInTradingTime(double.NaN).Should().BeNull();
        t.VolatilityInTradingTime(0.0).Should().Be(0.0, "zero volatility converts to zero");

        var spent = TradingClock.Until(Expiry, Expiry, Session, 0)!;
        spent.VolatilityInTradingTime(0.13).Should().BeNull("no trading time to annualise over");
    }

    // ---------------------------------------------------------------- boundaries and refusals

    [Fact]
    public void ExpiryExactlyNowIsZeroOnBothClocksAndThatIsTheTruth()
    {
        var t = TradingClock.Until(Expiry, Expiry, Session, 0)!;

        t.TradingMinutesRemaining.Should().Be(0);
        t.TradingYears.Should().Be(0.0);
        t.CalendarYears.Should().Be(0.0);
        t.NoTradingTimeLeft.Should().BeTrue();
    }

    [Fact]
    public void ExpiryInThePastIsRefused()
    {
        TradingClock.Until(Expiry.AddMinutes(1), Expiry, Session, 0).Should().BeNull();
        TradingClock.Until(Expiry.AddDays(1), Expiry, Session, 0).Should().BeNull();
    }

    [Fact]
    public void UnusableArgumentsAreRefused()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0);

        TradingClock.Until(now, Expiry, null!, 4).Should().BeNull();
        TradingClock.Until(now, Expiry, new TradingSession(new TimeOnly(15, 30), new TimeOnly(9, 15)), 4)
            .Should().BeNull("a session that closes before it opens");
        TradingClock.Until(now, Expiry, Session, -1).Should().BeNull();
        TradingClock.Until(now, Expiry, Session, 4, tradingDaysPerYear: 0).Should().BeNull();
        TradingClock.Until(now, Expiry, Session, 4, tradingDaysPerYear: double.NaN).Should().BeNull();
        TradingClock.Until(now, Expiry, Session, 4, calendarDaysPerYear: 0).Should().BeNull();
        TradingClock.Until(now, Expiry, Session, 4, calendarDaysPerYear: -365).Should().BeNull();
    }

    [Fact]
    public void ConventionsAreParametersNotConstants()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0);

        var on252 = TradingClock.Until(now, Expiry, Session, 4)!;
        var on365 = TradingClock.Until(now, Expiry, Session, 4, tradingDaysPerYear: 365)!;

        on365.TradingYears.Should().BeApproximately(on252.TradingYears * 252.0 / 365.0, 1e-12);
        on365.TradingMinutesRemaining.Should().Be(on252.TradingMinutesRemaining,
            "the minute count is a fact; only the annualisation is a convention");

        var on36525 = TradingClock.Until(now, Expiry, Session, 4, calendarDaysPerYear: 365.25)!;
        on36525.CalendarYears.Should().BeApproximately(on252.CalendarYears * 365.0 / 365.25, 1e-12);
    }

    [Fact]
    public void ACustomSessionIsHonoured()
    {
        // The reason no exchange calendar ships with this package: yours wins.
        var shortDay = new TradingSession(new TimeOnly(10, 0), new TimeOnly(14, 0));
        var expiry = new DateTime(2026, 9, 8, 14, 0, 0);

        var t = TradingClock.Until(expiry.Date.AddHours(11), expiry, shortDay, 0)!;

        t.SessionMinutes.Should().Be(240);
        t.TradingMinutesRemaining.Should().Be(180);
        t.TradingYears.Should().BeApproximately(180.0 / (252.0 * 240.0), 1e-12);
    }
}
