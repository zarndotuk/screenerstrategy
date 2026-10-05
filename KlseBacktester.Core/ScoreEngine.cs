using KlseBacktester.Models;

namespace KlseBacktester.Core;

/// <summary>
/// Direct C# translation of the Pine Script Score-Based Entry/Exit system.
///
/// Pine Script source:
///   entryMinScore = 7, exitMinScore = 7, cooldownBars = 5
///   EMA 20/50/200, RSI 14, ATR 14, volume SMA 20
///   recentHigh = ta.highest(high, 20)[1]   -- highest of prev 20, excluding current bar
///   recentLow  = ta.lowest(low, 10)[1]
///   marketBull = SPY > EMA50(SPY)
/// </summary>
public class ScoreEngine
{
    public int EntryMinScore { get; init; } = 7;
    public int ExitMinScore  { get; init; } = 7;
    public int CooldownBars  { get; init; } = 5;
    public int ConfirmationLookback { get; init; } = 20;
    public int ConfirmationBars { get; init; } = 5;
    public int ConfirmationThreshold { get; init; } = 5;
    public int RequiredBreakoutConfirmations { get; init; } = 1;
    public decimal ProfitTargetPercent { get; init; } = 20m;
    public int TrendSlopeLookback { get; init; } = 5;
    public decimal MaxEntryAtrExtension { get; init; } = 2m;
    public decimal BreakoutCloseNearHighPercent { get; init; } = 0.70m;
    public decimal TrailingStopAtrMultiplier { get; init; } = 2m;
    public bool RequireTrendQualityForEntry { get; init; } = true;
    public int LongTrendSlopeLookback { get; init; } = 20;
    public int RequiredClosesAboveEma200 { get; init; } = 5;
    public bool AllowSameBarBreakoutEntry { get; init; } = true;
    public int SameBarBreakoutMinScore { get; init; } = 9;
    /// <summary>When true, a qualifying breakout on the setup bar itself confirms the entry (no 1-bar lag).</summary>
    public bool AllowSetupBarBreakout { get; init; } = false;

    /// <summary>
    /// Run the scoring engine over the full price series.
    /// <paramref name="signalStartIndex"/> is the first bar where signals may be emitted —
    /// all bars before it are warmup-only (indicators computed but no signals generated).
    /// Use WarmupCalculator.GetSignalStartIndex() to compute this value.
    /// </summary>
    public List<Signal> Evaluate(
        List<StockPrice> prices,
        decimal[] spyClose,
        int? signalStartIndex = null)
    {
        return EvaluateWithCalls(prices, spyClose, signalStartIndex).Signals;
    }

    public ScoreEvaluation EvaluateWithCalls(
        List<StockPrice> prices,
        decimal[] spyClose,
        int? signalStartIndex = null)
    {
        int n = prices.Count;
        if (n < WarmupCalculator.WarmupBars)
        {
            Console.WriteLine(
                $"  [WARN] Only {n} bars — need {WarmupCalculator.WarmupBars} warmup bars. " +
                $"Signals may be unreliable.");
        }
        if (n < WarmupCalculator.MinimumBars) return new ScoreEvaluation();  // EMA50 must be seeded

        // ── Build arrays ──
        var close  = prices.Select(p => p.Close).ToArray();
        var high   = prices.Select(p => p.High).ToArray();
        var low    = prices.Select(p => p.Low).ToArray();
        var open   = prices.Select(p => p.Open).ToArray();
        var volume = prices.Select(p => (decimal)p.Volume).ToArray();

        // ── Indicators ──
        var ema20  = Indicators.Ema(close, 20);
        var ema50  = Indicators.Ema(close, 50);
        var ema200 = Indicators.Ema(close, 200);
        var rsi    = Indicators.Rsi(close, 14);
        var atr    = Indicators.Atr(high, low, close, 14);
        var avgVol = Indicators.Sma(volume, 20);

        // recentHigh = ta.highest(high, 20)[1]  — highest of 20 bars BEFORE current
        var recentHigh = Indicators.Highest(high, 20, offset: 1);
        // recentLow  = ta.lowest(low, 10)[1]
        var recentLow  = Indicators.Lowest(low, 10, offset: 1);
        var confirmationBreakoutLevel = Indicators.Highest(high, ConfirmationLookback, offset: 1);
        var confirmationBreakdownLevel = Indicators.Lowest(low, ConfirmationLookback, offset: 1);

        // SPY market filter — align SPY EMA50 to our bar dates
        var spyEma50 = Indicators.Ema(spyClose, 50);

        var evaluation = new ScoreEvaluation();
        bool inTrade       = false;
        int  lastSignalBar = -1;
        decimal entryPrice = 0;
        decimal targetPrice = 0;
        decimal stopLossPrice = 0;
        decimal highestCloseSinceEntry = 0;
        decimal trailingStop = 0;
        int buySetupBar = -1;
        int buySetupScore = 0;
        EntryScoreBreakdown? buySetupBreakdown = null;
        int buyBreakoutBar = -1;
        decimal buyBreakoutLevel = 0;
        int buyBreakoutCount = 0;
        int sellBreakdownBar = -1;
        decimal sellBreakdownLevel = 0;
        bool sellLocked = false;
        bool sellScoreArmed = false;

        // Start loop from signalStartIndex so all indicators have had
        // sufficient history to converge before any signal is emitted.
        int loopStart = Math.Max(signalStartIndex ?? WarmupCalculator.WarmupBars, WarmupCalculator.MinimumBars);
        for (int i = loopStart; i < n; i++)
        {
            bool recentSignal = lastSignalBar >= 0 && (i - lastSignalBar) < CooldownBars;

            // EMA200 is zero until 200 bars exist. Until then EMA50 stands in as the
            // long-trend baseline and the EMA50 > EMA200 condition scores nothing.
            bool ema200Ready = ema200[i] > 0;
            var longEma = ema200Ready ? ema200 : ema50;
            int longSlopeLookback = ema200Ready ? LongTrendSlopeLookback : TrendSlopeLookback;

            // Market bullish: SPY close > SPY EMA50
            bool marketBull = i < spyClose.Length && spyEma50[i] > 0
                ? spyClose[i] > spyEma50[i]
                : true;  // assume bullish if no SPY data

            // ── Entry score ──
            var eb = new EntryScoreBreakdown
            {
                CloseAboveEma20      = close[i] > ema20[i],
                Ema20AboveEma50      = ema20[i] > ema50[i],
                Ema50AboveEma200     = ema200Ready && ema50[i] > ema200[i],
                RsiBetween55And70    = rsi[i] > 55 && rsi[i] < 70,
                VolumeAbove1_5x      = avgVol[i] > 0 && volume[i] > avgVol[i] * 1.5m,
                VolumeAbove2x        = avgVol[i] > 0 && volume[i] > avgVol[i] * 2.0m,
                BreakoutAboveRecentHigh = recentHigh[i] > 0 && close[i] > recentHigh[i],
                CandleBullish        = close[i] > open[i],
                MarketBullish        = marketBull,
            };

            int entryScore = 0;
            entryScore += eb.CloseAboveEma20      ? 1 : 0;
            entryScore += eb.Ema20AboveEma50      ? 1 : 0;
            entryScore += eb.Ema50AboveEma200     ? 1 : 0;
            entryScore += eb.RsiBetween55And70    ? 1 : 0;
            entryScore += eb.VolumeAbove1_5x      ? 1 : 0;
            entryScore += eb.VolumeAbove2x        ? 1 : 0;
            entryScore += eb.BreakoutAboveRecentHigh ? 2 : 0;  // ← worth 2
            entryScore += eb.CandleBullish        ? 1 : 0;
            entryScore += eb.MarketBullish        ? 1 : 0;

            // ── Exit score ──
            var xb = new ExitScoreBreakdown
            {
                CloseBelowEma20     = close[i] < ema20[i],
                Ema20BelowEma50     = ema20[i] < ema50[i],
                RsiBelow55          = rsi[i] < 55,
                RsiBelow50          = rsi[i] < 50,
                BearishVolumeSpike  = avgVol[i] > 0
                                      && volume[i] > avgVol[i] * 1.5m
                                      && close[i] < open[i],
                CloseBelowRecentLow = recentLow[i] > 0 && close[i] < recentLow[i],
                OverboughtExtension = atr[i] > 0 && (close[i] - ema20[i]) > atr[i] * 3,
            };

            int exitScore = 0;
            exitScore += xb.CloseBelowEma20    ? 2 : 0;  // ← worth 2
            exitScore += xb.Ema20BelowEma50    ? 1 : 0;
            exitScore += xb.RsiBelow55         ? 1 : 0;
            exitScore += xb.RsiBelow50         ? 1 : 0;
            exitScore += xb.BearishVolumeSpike ? 2 : 0;  // ← worth 2
            exitScore += xb.CloseBelowRecentLow? 2 : 0;  // ← worth 2
            exitScore += xb.OverboughtExtension? 1 : 0;

            // ── Signal logic ──
            bool ema20Rising = IsRising(ema20, i, TrendSlopeLookback);
            bool ema50Rising = IsRising(ema50, i, TrendSlopeLookback);
            bool longEmaRising = IsRising(longEma, i, longSlopeLookback);
            bool heldAboveLongEma = HasRecentClosesAbove(close, longEma, i, RequiredClosesAboveEma200);
            bool longTrendUp = ema200Ready ? ema50[i] > ema200[i] : ema50Rising;
            bool strongTrend = close[i] > ema20[i]
                               && ema20[i] > ema50[i]
                               && longTrendUp
                               && ema20Rising
                               && ema50Rising;
            bool trendQuality = close[i] > longEma[i]
                                && ema50Rising
                                && longEmaRising
                                && heldAboveLongEma;
            bool notOverextended = atr[i] <= 0
                                   || close[i] - ema20[i] <= atr[i] * MaxEntryAtrExtension;
            bool buyCall = entryScore >= EntryMinScore;
            bool sellCall   = exitScore >= ExitMinScore;
            decimal barRange = high[i] - low[i];
            bool closesNearHigh = barRange <= 0
                                  || (close[i] - low[i]) / barRange >= BreakoutCloseNearHighPercent;
            bool breakout = confirmationBreakoutLevel[i] > 0
                            && avgVol[i] > 0
                            && close[i] > confirmationBreakoutLevel[i]
                            && volume[i] > avgVol[i] * 1.5m
                            && closesNearHigh;
            bool breakdown = confirmationBreakdownLevel[i] > 0
                             && avgVol[i] > 0
                             && close[i] < confirmationBreakdownLevel[i]
                             && volume[i] > avgVol[i] * 1.5m;

            if (buyCall && !inTrade && buySetupBar < 0)
            {
                buySetupBar = i;
                buySetupScore = entryScore;
                buySetupBreakdown = eb;
                buyBreakoutBar = -1;
                buyBreakoutLevel = 0;
                buyBreakoutCount = 0;
            }

            if (sellCall && !buyCall && !inTrade && buySetupBar >= 0)
            {
                buySetupBar = -1;
                buySetupScore = 0;
                buySetupBreakdown = null;
                buyBreakoutBar = -1;
                buyBreakoutLevel = 0;
                buyBreakoutCount = 0;
            }

            bool buyBreakoutAfterSetup = breakout
                                         && buySetupBar >= 0
                                         && (AllowSetupBarBreakout ? i >= buySetupBar : i > buySetupBar);
            bool sameBarBreakoutEntry = AllowSameBarBreakoutEntry
                                        && buyCall
                                        && !inTrade
                                        && entryScore >= SameBarBreakoutMinScore
                                        && eb.BreakoutAboveRecentHigh
                                        && longTrendUp
                                        && !eb.Ema20AboveEma50
                                        && eb.CandleBullish
                                        && eb.VolumeAbove2x;

            if (buyBreakoutAfterSetup)
            {
                buyBreakoutBar = i;
                buyBreakoutLevel = confirmationBreakoutLevel[i];
                buyBreakoutCount++;
            }
            else if (sameBarBreakoutEntry)
            {
                buySetupBar = i;
                buySetupScore = entryScore;
                buySetupBreakdown = eb;
                buyBreakoutBar = i;
                buyBreakoutLevel = recentHigh[i];
            }

            if (breakdown)
            {
                sellBreakdownBar = i;
                sellBreakdownLevel = confirmationBreakdownLevel[i];
                sellLocked = false;
                sellScoreArmed = sellCall;
            }

            bool sellWindow = sellBreakdownBar >= 0
                              && i > sellBreakdownBar
                              && i <= sellBreakdownBar + ConfirmationBars;

            int buyConfirmationScore = buyBreakoutCount + (sameBarBreakoutEntry ? 1 : 0);

            int sellConfirmationScore = 0;
            sellConfirmationScore += sellWindow && close[i] < sellBreakdownLevel ? 2 : 0;
            sellConfirmationScore += sellWindow && high[i] < sellBreakdownLevel ? 2 : 0;
            sellConfirmationScore += close[i] < ema20[i] ? 2 : 0;
            sellConfirmationScore += rsi[i] < 45 ? 1 : 0;
            sellConfirmationScore += avgVol[i] > 0 && volume[i] > avgVol[i] ? 1 : 0;

            bool buyConfirm = buyBreakoutAfterSetup
                              && buyBreakoutCount >= RequiredBreakoutConfirmations;
            buyConfirm = buyConfirm || sameBarBreakoutEntry;
            bool sellConfirm = sellWindow
                               && sellScoreArmed
                               && sellConfirmationScore >= ConfirmationThreshold
                               && !sellLocked;
            bool sellConfirmationExit = inTrade && sellConfirm;

            bool targetHit = inTrade && targetPrice > 0 && high[i] >= targetPrice;
            if (inTrade)
            {
                if (close[i] > highestCloseSinceEntry)
                    highestCloseSinceEntry = close[i];

                if (close[i] > entryPrice && atr[i] > 0)
                {
                    var newTrailingStop = highestCloseSinceEntry - atr[i] * TrailingStopAtrMultiplier;
                    if (newTrailingStop > trailingStop)
                        trailingStop = newTrailingStop;
                }
            }

            bool trailingStopHit = inTrade
                                   && trailingStop > 0
                                   && close[i] <= trailingStop;
            bool stopLossHit = inTrade
                               && stopLossPrice > 0
                               && low[i] <= stopLossPrice;
            bool buySignal = buyConfirm
                             && !inTrade
                             && !recentSignal
                             && marketBull
                             && (!RequireTrendQualityForEntry || trendQuality || sameBarBreakoutEntry);
            bool sellSignal = targetHit || stopLossHit || sellConfirmationExit || trailingStopHit;

            if (buyCall)
            {
                evaluation.Calls.Add(new ScoreCall
                {
                    Date               = prices[i].Date,
                    Type               = SignalType.Buy,
                    Price              = close[i],
                    Score              = entryScore,
                    IsActionableSignal = buySignal,
                    BuyConfirm         = buyConfirm,
                    TrendQuality       = trendQuality,
                    SameBarBreakoutEntry = sameBarBreakoutEntry,
                    EntryBreakdown     = eb,
                });
            }

            if (sellCall)
            {
                evaluation.Calls.Add(new ScoreCall
                {
                    Date               = prices[i].Date,
                    Type               = SignalType.Sell,
                    Price              = close[i],
                    Score              = exitScore,
                    IsActionableSignal = false,
                    ExitBreakdown      = xb,
                });
            }

            if (buyBreakoutAfterSetup || sameBarBreakoutEntry)
            {
                evaluation.Confirmations.Add(new RetestConfirmation
                {
                    Date               = prices[i].Date,
                    Type               = SignalType.Buy,
                    Price              = close[i],
                    Score              = buyConfirmationScore,
                    BreakoutDate       = prices[buyBreakoutBar].Date,
                    Level              = buyBreakoutLevel,
                    BreakoutCount      = buyConfirmationScore,
                    IsActionableSignal = buySignal,
                });
            }

            if (sellConfirm)
            {
                sellLocked = true;
                evaluation.Confirmations.Add(new RetestConfirmation
                {
                    Date               = prices[i].Date,
                    Type               = SignalType.Sell,
                    Price              = close[i],
                    Score              = sellConfirmationScore,
                    BreakoutDate       = prices[sellBreakdownBar].Date,
                    Level              = sellBreakdownLevel,
                    BreakoutCount      = 0,
                    IsActionableSignal = sellConfirmationExit,
                });
            }

            evaluation.Trace.Add(new BarTrace(
                Date:             prices[i].Date,
                Close:            close[i],
                Ema20:            ema20[i],
                Ema50:            ema50[i],
                Rsi:              rsi[i],
                VolumeRatio:      avgVol[i] > 0 ? volume[i] / avgVol[i] : 0,
                EntryScore:       entryScore,
                ExitScore:        exitScore,
                HasSetup:         buySetupBar >= 0,
                Breakout:         breakout,
                TrendQuality:     trendQuality,
                HeldAboveLongEma: heldAboveLongEma,
                BuyConfirm:       buyConfirm,
                InTrade:          inTrade,
                BuySignal:        buySignal,
                SellSignal:       sellSignal && !buySignal));

            if (buySignal)
            {
                inTrade       = true;
                lastSignalBar = i;
                entryPrice = close[i];
                targetPrice = Math.Round(entryPrice * (1 + ProfitTargetPercent / 100m), 4);
                stopLossPrice = atr[i] > 0 ? close[i] - atr[i] * 1.5m : 0;
                highestCloseSinceEntry = close[i];
                trailingStop = 0;

                evaluation.Signals.Add(new Signal
                {
                    Date             = prices[i].Date,
                    Type             = SignalType.Buy,
                    Price            = close[i],
                    Score            = buySetupScore,
                    StopLoss         = stopLossPrice,
                    TargetPrice      = targetPrice,
                    ConfirmationScore = buyConfirmationScore,
                    ConfirmationBreakoutDate = prices[buyBreakoutBar].Date,
                    ConfirmationLevel = buyBreakoutLevel,
                    ConfirmationBreakoutCount = buyBreakoutCount,
                    EntryBreakdown   = buySetupBreakdown,
                });

                buySetupBar = -1;
                buySetupScore = 0;
                buySetupBreakdown = null;
                buyBreakoutBar = -1;
                buyBreakoutLevel = 0;
                buyBreakoutCount = 0;
            }
            else if (sellSignal)
            {
                inTrade       = false;
                lastSignalBar = i;
                var exitPrice = targetHit
                    ? targetPrice
                    : stopLossHit
                        ? stopLossPrice
                        : close[i];
                var signalScore = sellConfirmationExit ? sellConfirmationScore : exitScore;
                var exitReason = targetHit
                    ? $"{ProfitTargetPercent:0.##}% profit target"
                    : stopLossHit
                        ? "ATR stop loss"
                        : sellConfirmationExit
                            ? "Sell confirmation"
                            : "ATR trailing stop";
                var originalTargetPrice = targetPrice;
                entryPrice = 0;
                targetPrice = 0;
                stopLossPrice = 0;
                highestCloseSinceEntry = 0;
                trailingStop = 0;

                evaluation.Signals.Add(new Signal
                {
                    Date            = prices[i].Date,
                    Type            = SignalType.Sell,
                    Price           = exitPrice,
                    Score           = signalScore,
                    TargetPrice     = targetHit ? exitPrice : originalTargetPrice,
                    ExitReason      = exitReason,
                    ExitBreakdown   = xb,
                });
            }

            if (sellBreakdownBar >= 0 && i > sellBreakdownBar + ConfirmationBars)
            {
                sellLocked = false;
                sellScoreArmed = false;
            }
        }

        return evaluation;
    }

    /// <summary>
    /// First index in [from, index] where the series is seeded (non-zero). Indicator arrays are
    /// zero until their period has elapsed, so on short histories the lookback window is
    /// clamped to the seeded part instead of failing outright.
    /// </summary>
    private static int FirstSeeded(decimal[] series, int from, int index)
    {
        from = Math.Max(0, from);
        while (from < index && series[from] <= 0)
            from++;
        return from;
    }

    /// <summary>True when the series rose over the lookback (clamped to its seeded bars).</summary>
    private static bool IsRising(decimal[] series, int index, int lookback)
    {
        int from = FirstSeeded(series, index - lookback, index);
        return from < index && series[index] > series[from];
    }

    /// <summary>
    /// True when the last <paramref name="bars"/> closes are above the baseline. On short
    /// histories the window is clamped to bars where the baseline is seeded (minimum 2 bars).
    /// </summary>
    private static bool HasRecentClosesAbove(
        decimal[] close,
        decimal[] baseline,
        int index,
        int bars)
    {
        if (bars <= 1)
            return close[index] > baseline[index];

        int from = FirstSeeded(baseline, index - bars + 1, index);
        if (index - from + 1 < 2)
            return false;

        for (int i = from; i <= index; i++)
        {
            if (close[i] <= baseline[i])
                return false;
        }

        return true;
    }
}
