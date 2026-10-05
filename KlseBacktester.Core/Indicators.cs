namespace KlseBacktester.Core;

/// <summary>
/// C# equivalents of Pine Script's built-in ta.* functions.
/// All methods operate on a full series and return an array of the same length.
/// Index 0 = oldest bar, Index N-1 = most recent bar (same convention as Pine).
/// </summary>
public static class Indicators
{
    // ── EMA ──────────────────────────────────────────────────────────────────

    /// <summary>Exponential Moving Average — mirrors ta.ema(source, length)</summary>
    public static decimal[] Ema(decimal[] source, int length)
    {
        var result = new decimal[source.Length];
        decimal multiplier = 2m / (length + 1);

        // Seed with SMA of first `length` bars
        if (source.Length < length) return result;

        decimal seed = source[..length].Average();
        result[length - 1] = seed;

        for (int i = length; i < source.Length; i++)
            result[i] = (source[i] - result[i - 1]) * multiplier + result[i - 1];

        return result;
    }

    // ── SMA ──────────────────────────────────────────────────────────────────

    /// <summary>Simple Moving Average — mirrors ta.sma(source, length)</summary>
    public static decimal[] Sma(decimal[] source, int length)
    {
        var result = new decimal[source.Length];
        for (int i = length - 1; i < source.Length; i++)
        {
            result[i] = source[(i - length + 1)..(i + 1)].Average();
        }
        return result;
    }

    // ── RSI ──────────────────────────────────────────────────────────────────

    /// <summary>Relative Strength Index — mirrors ta.rsi(source, length)</summary>
    public static decimal[] Rsi(decimal[] source, int length)
    {
        var result = new decimal[source.Length];
        if (source.Length < length + 1) return result;

        // Wilder smoothing (same as Pine)
        decimal avgGain = 0, avgLoss = 0;

        for (int i = 1; i <= length; i++)
        {
            decimal change = source[i] - source[i - 1];
            if (change > 0) avgGain += change;
            else avgLoss += Math.Abs(change);
        }
        avgGain /= length;
        avgLoss /= length;

        for (int i = length; i < source.Length; i++)
        {
            if (i > length)
            {
                decimal change = source[i] - source[i - 1];
                decimal gain = change > 0 ? change : 0;
                decimal loss = change < 0 ? Math.Abs(change) : 0;
                avgGain = (avgGain * (length - 1) + gain) / length;
                avgLoss = (avgLoss * (length - 1) + loss) / length;
            }

            result[i] = avgLoss == 0 ? 100 : 100 - (100 / (1 + avgGain / avgLoss));
        }

        return result;
    }

    // ── ATR ──────────────────────────────────────────────────────────────────

    /// <summary>Average True Range — mirrors ta.atr(length)</summary>
    public static decimal[] Atr(decimal[] high, decimal[] low, decimal[] close, int length)
    {
        int n = high.Length;
        var tr = new decimal[n];
        var result = new decimal[n];

        for (int i = 1; i < n; i++)
        {
            decimal hl    = high[i] - low[i];
            decimal hc    = Math.Abs(high[i] - close[i - 1]);
            decimal lc    = Math.Abs(low[i] - close[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }

        // Seed
        if (n < length + 1) return result;
        result[length] = tr[1..(length + 1)].Average();

        // Wilder smoothing
        for (int i = length + 1; i < n; i++)
            result[i] = (result[i - 1] * (length - 1) + tr[i]) / length;

        return result;
    }

    // ── Highest / Lowest ─────────────────────────────────────────────────────

    /// <summary>
    /// Rolling highest value over `length` bars.
    /// Mirrors ta.highest(source, length)[1] when called with offset=1.
    /// </summary>
    public static decimal[] Highest(decimal[] source, int length, int offset = 0)
    {
        var result = new decimal[source.Length];
        for (int i = length - 1; i < source.Length; i++)
        {
            int start = Math.Max(0, i - length - offset + 1);
            int end   = Math.Max(0, i - offset);
            if (start > end) continue;
            result[i] = source[start..(end + 1)].Max();
        }
        return result;
    }

    /// <summary>Rolling lowest value over `length` bars.</summary>
    public static decimal[] Lowest(decimal[] source, int length, int offset = 0)
    {
        var result = new decimal[source.Length];
        for (int i = length - 1; i < source.Length; i++)
        {
            int start = Math.Max(0, i - length - offset + 1);
            int end   = Math.Max(0, i - offset);
            if (start > end) continue;
            result[i] = source[start..(end + 1)].Min();
        }
        return result;
    }

    // ── Crossover helpers ────────────────────────────────────────────────────

    /// <summary>Returns true when series A crosses above series B at index i.</summary>
    public static bool CrossOver(decimal[] a, decimal[] b, int i)
        => i > 0 && a[i] > b[i] && a[i - 1] <= b[i - 1];

    /// <summary>Returns true when series A crosses below series B at index i.</summary>
    public static bool CrossUnder(decimal[] a, decimal[] b, int i)
        => i > 0 && a[i] < b[i] && a[i - 1] >= b[i - 1];
}
