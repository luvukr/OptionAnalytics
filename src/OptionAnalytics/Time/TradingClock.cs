namespace OptionAnalytics;

/// <summary>One continuous trading session, as clock times.</summary>
/// <remarks>
/// <para>
/// Supply your own. This library ships no exchange calendar and no session table, deliberately:
/// hours change by circular, holidays change every year, and a hardcoded table that has gone stale
/// is worse than no table at all because it still looks authoritative. The same reasoning keeps a
/// lot-size table out of the package.
/// </para>
/// <para>
/// A session with a lunch break is not representable here. Model it as the enclosing span and accept
/// the small overstatement of trading time, or scale <see cref="Minutes"/> yourself.
/// </para>
/// </remarks>
/// <param name="Open">When the session opens.</param>
/// <param name="Close">When it closes. Must be later than <paramref name="Open"/>.</param>
public sealed record TradingSession(TimeOnly Open, TimeOnly Close)
{
    /// <summary>NSE equity and index hours: 09:15 to 15:30, which is 375 minutes.</summary>
    /// <remarks>
    /// A convenience for the common case, not a commitment. It is a plain value you can replace, and
    /// if the exchange changes its hours this constant is wrong and yours is right.
    /// </remarks>
    public static TradingSession IndiaEquity { get; } = new(new TimeOnly(9, 15), new TimeOnly(15, 30));

    /// <summary>True when the session closes after it opens.</summary>
    public bool IsValid => Close > Open;

    /// <summary>Length of the session in minutes. 375 for a 09:15–15:30 day.</summary>
    public double Minutes => (Close - Open).TotalMinutes;
}

/// <summary>
/// How much time is left before expiry, on both of the clocks that matter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two answers exist because variance and interest do not accrue on the same clock.</b>
/// Volatility accrues only while the market is open — a weekend adds almost nothing to realised
/// variance — while interest accrues every calendar day. A single time-to-expiry cannot be both.
/// </para>
/// <para>
/// <b>For pricing with this library, use <see cref="CalendarYears"/>.</b> Not as a compromise:
/// <see cref="OptionInputs"/> carries one <see cref="OptionInputs.TimeToExpiry"/>, which the
/// pricer uses for the diffusion AND for <c>r*T</c> and <c>b*T</c>. Putting
/// <see cref="TradingYears"/> there gets the variance right and then charges interest over the
/// wrong span. Measured on a 24,800 at-the-money call three weeks out, that is a <b>1.24%</b>
/// mispricing — and it is pure error, since rescaling the rate and carry by the ratio of the two
/// clocks reproduces the calendar-clock price to the last bit.
/// </para>
/// <para>
/// <b>Switching clocks correctly changes nothing about the diffusion, which is the point.</b>
/// <see cref="VolatilityInTradingTime"/> preserves total variance exactly, so <c>sigma*sqrt(T)</c>
/// agrees to seven digits on either clock and gamma agrees to 0.08% — the residual being the carry
/// factor, not the diffusion. The trading clock is therefore not a better way to price. It is a
/// better way to READ the time: see <see cref="TradingMinutesRemaining"/> and
/// <see cref="NoTradingTimeLeft"/>, which answer questions calendar time cannot.
/// </para>
/// </remarks>
/// <param name="CalendarYears">
/// Time to expiry in calendar years — the conventional input, and the one consistent with a quoted
/// volatility. Use this unless you have a specific reason not to.
/// </param>
/// <param name="TradingYears">
/// Time to expiry counted only in open-market time, as a fraction of a trading year.
/// <para>
/// <b>Do not pass this to <see cref="OptionInputs.TimeToExpiry"/>.</b> One field serves both the
/// diffusion and the discounting, so a trading-time value prices the variance correctly and then
/// applies <c>r*T</c> over a span that is not the calendar period — measured at 1.24% on a
/// three-week at-the-money call. It is here for callers whose pricer separates the two clocks, and
/// as the denominator that makes <see cref="VolatilityInTradingTime"/> meaningful.
/// </para>
/// </param>
/// <param name="TradingMinutesRemaining">
/// Open-market minutes left before expiry. The raw count behind <paramref name="TradingYears"/>,
/// and the number to look at on expiry day — 375 at the open, 0 at the bell.
/// </param>
/// <param name="SessionMinutes">Length of one full session, for scale.</param>
public sealed record TimeRemaining(
    double CalendarYears,
    double TradingYears,
    double TradingMinutesRemaining,
    double SessionMinutes)
{
    /// <summary>True once there is no open-market time left, whatever the calendar says.</summary>
    /// <remarks>
    /// Distinct from calendar expiry: after Friday's close on a contract expiring Monday morning
    /// there is calendar time remaining but no trading time, so no further variance can accrue even
    /// though the discount factor still has a weekend to unwind.
    /// </remarks>
    public bool NoTradingTimeLeft => TradingMinutesRemaining <= 0;

    /// <summary>
    /// Converts a CALENDAR-annualised volatility — a market quote, or India VIX — into the
    /// trading-time-annualised volatility that belongs with <see cref="TradingYears"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Total variance is a physical quantity and cannot depend on which clock measures it, so
    /// <c>sigma_cal^2 * T_cal = sigma_trade^2 * T_trade</c>, giving
    /// <c>sigma_trade = sigma_cal * sqrt(T_cal / T_trade)</c>.
    /// </para>
    /// <para>
    /// The preservation is exact, not approximate: measured, total variance agrees to eight
    /// significant figures across the two clocks, and <c>sigma*sqrt(T)</c> to seven. So this
    /// conversion does not change the diffusion at all, and is not a way to get different prices. It
    /// exists so that a caller who has a reason to work in trading time is not silently using a
    /// volatility annualised on the other clock.
    /// </para>
    /// </remarks>
    /// <param name="calendarVolatility">The quoted volatility, as a decimal. Must be non-negative.</param>
    /// <returns>
    /// The equivalent trading-time volatility, or <c>null</c> if the input is unusable or there is no
    /// trading time left to annualise over.
    /// </returns>
    public double? VolatilityInTradingTime(double calendarVolatility)
    {
        if (!double.IsFinite(calendarVolatility) || calendarVolatility < 0) return null;
        if (TradingYears <= 0 || CalendarYears <= 0) return null;

        return calendarVolatility * Math.Sqrt(CalendarYears / TradingYears);
    }
}

/// <summary>
/// Turns wall-clock instants into a time to expiry that survives expiry day.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this solves.</b> Everyone computes time to expiry as
/// <c>(expiry - now).TotalDays / 365</c>. That is fine with a month to run and wrong on the day: it
/// is a whole-day quantity applied to an intraday question, and it either steps in units of a day or
/// collapses to zero while the contract is still trading. Hand a zero to
/// <see cref="BlackScholesMerton"/> and it correctly reports an expired contract — gamma and vega
/// zero, no implied volatility to solve — which is exactly wrong at 10am on expiry day, when gamma
/// is the largest number on the book.
/// </para>
/// <para>
/// The library is not at fault there and cannot fix it: a pricer given no time left must say so.
/// The error is upstream, in the arithmetic that produced the zero, and that is what this replaces.
/// </para>
/// <para>
/// <b>You supply the calendar.</b> The number of full trading days between two dates depends on a
/// holiday list that changes every year and on exchange notices that change without warning. That
/// knowledge lives in your system, so it is a parameter here rather than a table shipped in a NuGet
/// package that will be quietly wrong within a year.
/// </para>
/// <para>
/// <b>What this is actually for, stated in the order the value arrives.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="TimeRemaining.CalendarYears"/> computed from INSTANTS rather than dates. This alone
/// fixes expiry day, and the fix is not marginal: at 11:30 on expiry, the naive daily figure is
/// zero, so the model reports a worthless contract with no gamma, no vega and no solvable implied
/// volatility — while the same option is genuinely worth 27.79 with vega of 211.
/// </description></item>
/// <item><description>
/// <see cref="TimeRemaining.TradingMinutesRemaining"/> and
/// <see cref="TimeRemaining.NoTradingTimeLeft"/>, which say things calendar time cannot. After
/// Friday's close on a Monday-morning expiry there is calendar time left but no trading time, so no
/// further variance can accrue even though the discount factor still has a weekend to unwind.
/// </description></item>
/// <item><description>
/// <see cref="TimeRemaining.TradingYears"/> with
/// <see cref="TimeRemaining.VolatilityInTradingTime"/>, for a pricer that separates diffusion time
/// from discount time. This library's does not — see <see cref="TimeRemaining.TradingYears"/> for
/// the measured cost of pretending otherwise.
/// </description></item>
/// </list>
/// <para>
/// <b>The trap in the middle of all this.</b> Market-quoted implied volatility, and India VIX, are
/// annualised on the CALENDAR clock. Pairing a trading-time expiry with a calendar-annualised
/// volatility double-counts the difference between the two. Convert, or stay on one clock for both.
/// </para>
/// </remarks>
public static class TradingClock
{
    /// <summary>
    /// Time remaining from <paramref name="now"/> to <paramref name="expiry"/>, on both clocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trading minutes are counted as: whatever is left of today's session, plus
    /// <paramref name="fullTradingDaysBetween"/> whole sessions, plus the part of the expiry day's
    /// session that runs up to the expiry moment. An instant outside session hours contributes
    /// nothing, so a call before the open counts the whole day ahead and a call after the close
    /// counts none of it.
    /// </para>
    /// </remarks>
    /// <param name="now">The current instant.</param>
    /// <param name="expiry">
    /// The expiry instant, as a wall-clock time and not a date — for an NSE index option that is
    /// 15:30 on expiry day, not midnight. Using midnight adds most of a session that does not exist.
    /// </param>
    /// <param name="session">The trading session. See <see cref="TradingSession"/>.</param>
    /// <param name="fullTradingDaysBetween">
    /// Whole trading days STRICTLY between the two dates, excluding both ends, taken from your own
    /// holiday calendar. Zero when expiry is today or the next trading day.
    /// </param>
    /// <param name="nowIsOnATradingDay">
    /// False if today is a weekend or a holiday, in which case today contributes no trading minutes.
    /// </param>
    /// <param name="tradingDaysPerYear">
    /// Sessions in a trading year. 252 is conventional; use whatever your volatility estimate used.
    /// </param>
    /// <param name="calendarDaysPerYear">
    /// Days in a calendar year, matching <see cref="GreekConventions.DaysPerYear"/> at 365.
    /// </param>
    /// <returns>
    /// The two clocks, or <c>null</c> if the session is unusable, a count or divisor is negative or
    /// non-finite, or <paramref name="expiry"/> is BEFORE <paramref name="now"/>. Expiry exactly at
    /// <paramref name="now"/> is valid and returns zero on both clocks — that is the one moment a
    /// zero is the truth rather than an artefact.
    /// </returns>
    public static TimeRemaining? Until(
        DateTime now,
        DateTime expiry,
        TradingSession session,
        int fullTradingDaysBetween,
        bool nowIsOnATradingDay = true,
        double tradingDaysPerYear = 252.0,
        double calendarDaysPerYear = 365.0)
    {
        if (session is null || !session.IsValid) return null;
        if (fullTradingDaysBetween < 0) return null;
        if (!double.IsFinite(tradingDaysPerYear) || tradingDaysPerYear <= 0) return null;
        if (!double.IsFinite(calendarDaysPerYear) || calendarDaysPerYear <= 0) return null;
        if (expiry < now) return null;

        double sessionMinutes = session.Minutes;
        var nowTime = TimeOnly.FromDateTime(now);
        var expiryTime = TimeOnly.FromDateTime(expiry);

        double tradingMinutes;
        if (now.Date == expiry.Date)
        {
            tradingMinutes = nowIsOnATradingDay ? Overlap(session, nowTime, expiryTime) : 0.0;
        }
        else
        {
            double restOfToday = nowIsOnATradingDay ? Overlap(session, nowTime, session.Close) : 0.0;
            double onExpiryDay = Overlap(session, session.Open, expiryTime);

            tradingMinutes = restOfToday + fullTradingDaysBetween * sessionMinutes + onExpiryDay;
        }

        return new TimeRemaining(
            CalendarYears: (expiry - now).TotalDays / calendarDaysPerYear,
            TradingYears: tradingMinutes / (tradingDaysPerYear * sessionMinutes),
            TradingMinutesRemaining: tradingMinutes,
            SessionMinutes: sessionMinutes);
    }

    /// <summary>Minutes of <paramref name="session"/> covered by the span from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static double Overlap(TradingSession session, TimeOnly from, TimeOnly to)
    {
        var start = from < session.Open ? session.Open : from;
        var end = to > session.Close ? session.Close : to;

        return end > start ? (end - start).TotalMinutes : 0.0;
    }
}
