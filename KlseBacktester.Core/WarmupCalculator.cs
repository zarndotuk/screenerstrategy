namespace KlseBacktester.Core;

/// <summary>
/// Calculates how much historical data is needed before signals can be trusted.
///
/// The strategy uses:
///   EMA200  → needs ~600 bars to converge   (3× the period, Wilder rule of thumb)
///   EMA50   → needs ~150 bars
///   EMA20   → needs ~60 bars
///   RSI 14  → needs ~50 bars (Wilder smoothing)
///   ATR 14  → needs ~50 bars
///   SMA20 (volume) → needs 20 bars
///   recentHigh = ta.highest(high, 20)[1] → needs 21 bars
///   recentLow  = ta.lowest(low,  10)[1]  → needs 11 bars
///
/// Dominant constraint: EMA200 convergence = 600 trading bars ≈ 2.4 calendar years.
///
/// We fetch: warmupBars + backtestDays of data.
/// We only emit signals starting from the first bar AFTER the warmup window.
/// </summary>
public static class WarmupCalculator
{
    /// <summary>
    /// Minimum trading bars needed before any indicator is considered reliable.
    /// EMA200 convergence (3× period) dominates at 600 bars.
    /// </summary>
    public const int WarmupBars = 600;

    /// <summary>
    /// Approximate calendar days needed for WarmupBars of trading data.
    /// Bursa Malaysia trades ~252 days/year.
    /// 600 bars ÷ 252 × 365 ≈ 869 calendar days ≈ round up to 900 for safety.
    /// </summary>
    public const int WarmupCalendarDays = 900;

    /// <summary>
    /// Given the desired backtest window, return the fetch start date
    /// (backtest start minus warmup period).
    /// </summary>
    public static DateTime FetchFrom(DateTime backtestFrom)
        => backtestFrom.AddDays(-WarmupCalendarDays);

    /// <summary>
    /// Given a price series and the intended backtest start date,
    /// return the array index where the warmup ends and signals may begin.
    /// This is the LATER of:
    ///   (a) the first bar on or after backtestFrom
    ///   (b) bar index WarmupBars (absolute minimum for indicator convergence)
    /// </summary>
    public static int GetSignalStartIndex(
        IList<DateTime> dates, DateTime backtestFrom)
    {
        // First bar that is on or after the intended backtest start
        int dateIndex = 0;
        for (int i = 0; i < dates.Count; i++)
        {
            if (dates[i] >= backtestFrom)
            {
                dateIndex = i;
                break;
            }
        }

        // Must also be past the absolute warmup bar count
        return Math.Max(dateIndex, WarmupBars);
    }

    /// <summary>
    /// Diagnostic: given total bars fetched, how many are warmup vs usable.
    /// </summary>
    public static (int warmup, int usable) Split(int totalBars)
    {
        int warmup  = Math.Min(WarmupBars, totalBars);
        int usable  = Math.Max(0, totalBars - warmup);
        return (warmup, usable);
    }

    /// <summary>
    /// Per-indicator convergence requirements for display/diagnostics.
    /// </summary>
    public static IEnumerable<(string Indicator, int BarsNeeded, string Note)> Requirements()
    {
        yield return ("EMA200",       600, "3× period — dominant constraint");
        yield return ("EMA50",        150, "3× period");
        yield return ("EMA20",         60, "3× period");
        yield return ("RSI(14)",       50, "Wilder smoothing convergence");
        yield return ("ATR(14)",       50, "Wilder smoothing convergence");
        yield return ("Volume SMA20",  20, "Simple average");
        yield return ("recentHigh20",  21, "Highest(20)[1] needs 21 bars");
        yield return ("recentLow10",   11, "Lowest(10)[1] needs 11 bars");
    }
}
