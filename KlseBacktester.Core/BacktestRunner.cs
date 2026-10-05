using KlseBacktester.Data;
using KlseBacktester.Models;
using MongoDB.Driver;

namespace KlseBacktester.Core;

public class BacktestRunner
{
    private readonly ScoreEngine       _engine;
    private readonly YahooDataFetcher  _fetcher;
    private readonly MongoContext      _mongo;
    private readonly SignalRepository  _signals;

    public BacktestRunner(
        ScoreEngine engine,
        YahooDataFetcher fetcher,
        MongoContext mongo,
        SignalRepository signals)
    {
        _engine  = engine;
        _fetcher = fetcher;
        _mongo   = mongo;
        _signals = signals;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SINGLE TICKER BACKTEST
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run full backtest for one ticker.
    ///
    /// Data strategy:
    ///   fetchFrom  = backtestFrom - 900 calendar days (warmup period)
    ///   fetchTo    = backtestTo
    ///   Signals only emitted from backtestFrom onwards (after EMA200 converges).
    ///
    /// This means:
    ///   - EMA200 has ~600 trading bars to converge before first signal
    ///   - RSI, ATR, volume SMA are all stable
    ///   - recentHigh/Low windows are fully populated
    /// </summary>
    public async Task<BacktestResult> RunAsync(
        string ticker, DateTime backtestFrom, DateTime backtestTo,
        string name = "", string sector = "")
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, ticker, StringComparison.OrdinalIgnoreCase))
        {
            var stockInfo = await _fetcher.GetStockInfoAsync(ticker);
            if (!string.IsNullOrWhiteSpace(stockInfo?.Name))
            {
                name = stockInfo.Name;
            }

            if (string.IsNullOrWhiteSpace(sector) && !string.IsNullOrWhiteSpace(stockInfo?.Sector))
            {
                sector = stockInfo.Sector;
            }
        }

        var (prices, evaluation) = await EvaluateAsync(ticker, backtestFrom, backtestTo);
        var signals = evaluation.Signals;
        var (warmup, usable) = WarmupCalculator.Split(prices.Count);

        // ── Build trades + persist signal lifecycle ──
        var trades = await BuildTradesAndPersistAsync(signals, ticker, name, sector);

        // ── Persist performance stats ──
        var perf = BuildPerformance(ticker, name, sector, trades, signals, warmup, usable);
        await _signals.UpsertPerformanceAsync(perf);

        // ── Persist raw result ──
        var result = new BacktestResult
        {
            Ticker         = ticker,
            RunDate        = DateTime.UtcNow,
            FromDate       = backtestFrom,   // report the intended window, not the fetch window
            ToDate         = backtestTo,
            Signals        = signals,
            Calls          = evaluation.Calls,
            Confirmations  = evaluation.Confirmations,
            Trades         = trades,
            MaxDrawdownPct = perf.MaxDrawdownPct,
            OpenTrade      = trades.FirstOrDefault(t => !t.ExitDate.HasValue),
        };

        var filter = Builders<BacktestResult>.Filter.And(
            Builders<BacktestResult>.Filter.Eq(r => r.Ticker, ticker),
            Builders<BacktestResult>.Filter.Gte(r => r.FromDate, backtestFrom),
            Builders<BacktestResult>.Filter.Lte(r => r.ToDate, backtestTo));

        await _mongo.BacktestResults.ReplaceOneAsync(filter, result,
            new ReplaceOptions { IsUpsert = true });

        return result;
    }

    /// <summary>
    /// Load stored prices and run the score engine without persisting anything.
    /// </summary>
    public async Task<(List<StockPrice> Prices, ScoreEvaluation Evaluation)> EvaluateAsync(
        string ticker, DateTime backtestFrom, DateTime backtestTo)
    {
        // Extend fetch window back by warmup period
        DateTime fetchFrom = WarmupCalculator.FetchFrom(backtestFrom);

        // Yahoo emits flat zero-volume bars on Bursa holidays (e.g. 31 Aug); they aren't
        // trading sessions and would distort volume averages and breakout windows.
        var prices = (await _fetcher.GetPricesAsync(ticker, fetchFrom, backtestTo))
            .Where(p => !(p.Volume == 0 && p.High == p.Low))
            .ToList();

        if (prices.Count < WarmupCalculator.WarmupBars)
        {
            // Not enough for full warmup — warn but continue with what we have
            // (some stocks listed more recently may have less history)
            Console.WriteLine(
                $"  [WARN] {ticker}: {prices.Count} bars fetched " +
                $"(recommended: {WarmupCalculator.WarmupBars}). " +
                $"Signals from early in the window may be unreliable.");
        }

        if (prices.Count < WarmupCalculator.MinimumBars)
            throw new InvalidOperationException(
                $"{ticker}: only {prices.Count} bars — too few to run any indicators.");

        // Determine where signals may start (after warmup)
        var dates          = prices.Select(p => p.Date).ToList();
        int signalStartIdx = WarmupCalculator.GetSignalStartIndex(dates, backtestFrom);

        // KLSE-only runs do not fetch or persist SPY. An empty proxy array makes
        // ScoreEngine treat the market filter as bullish.
        var marketProxyClose = Array.Empty<decimal>();

        // ── Score engine — signals only fire after warmup ──
        return (prices, _engine.EvaluateWithCalls(prices, marketProxyClose, signalStartIdx));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ALL TICKERS
    // ═══════════════════════════════════════════════════════════════════════

    public async Task<List<BacktestResult>> RunAllAsync(
        DateTime backtestFrom, DateTime backtestTo,
        Action<string, int, int>? onProgress = null)
    {
        return await RunAsync(KlseTickers.All, backtestFrom, backtestTo, onProgress);
    }

    public async Task<List<BacktestResult>> RunAsync(
        IReadOnlyList<(string Ticker, string Name, string Sector)> tickers,
        DateTime backtestFrom, DateTime backtestTo,
        Action<string, int, int>? onProgress = null)
    {
        var results = new List<BacktestResult>();

        for (int i = 0; i < tickers.Count; i++)
        {
            var (symbol, stockName, sector) = tickers[i];
            onProgress?.Invoke(symbol, i + 1, tickers.Count);

            try
            {
                var r = await RunAsync(symbol, backtestFrom, backtestTo, stockName, sector);
                results.Add(r);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [SKIP] {symbol}: {ex.Message}");
            }
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<List<Trade>> BuildTradesAndPersistAsync(
        List<Signal> signals, string ticker, string name, string sector)
    {
        var trades   = new List<Trade>();
        Trade? openTrade = null;

        // Full re-backtest = rebuild signal history from scratch
        await _mongo.ActiveSignals.DeleteManyAsync(
            Builders<ActiveSignal>.Filter.Eq(s => s.Ticker, ticker));

        foreach (var sig in signals)
        {
            if (sig.Type == SignalType.Buy)
            {
                await _signals.OpenBuyAsync(
                    ticker, name, sector,
                    sig.Date, sig.Price, sig.Score, sig.StopLoss,
                    sig.TargetPrice);

                openTrade = new Trade
                {
                    EntryDate  = sig.Date,
                    EntryPrice = sig.Price,
                    EntryScore = sig.Score,
                    StopLoss   = sig.StopLoss,
                    TargetPrice = sig.TargetPrice,
                };
            }
            else if (sig.Type == SignalType.Sell && openTrade != null)
            {
                await _signals.CloseSellAsync(
                    ticker, sig.Date, sig.Price, sig.Score,
                    sig.ExitReason);

                openTrade.ExitDate  = sig.Date;
                openTrade.ExitPrice = sig.Price;
                openTrade.ExitScore = sig.Score;
                openTrade.ExitReason = sig.ExitReason;
                openTrade.TargetPrice = sig.TargetPrice ?? openTrade.TargetPrice;
                trades.Add(openTrade);
                openTrade = null;
            }
        }

        if (openTrade != null)
            trades.Add(openTrade);

        return trades;
    }

    private static TickerPerformance BuildPerformance(
        string ticker, string name, string sector,
        List<Trade> trades, List<Signal> signals,
        int warmupBars, int usableBars)
    {
        var closed    = trades.Where(t => t.ExitDate.HasValue).ToList();
        var wins      = closed.Where(t => t.IsWin == true).ToList();
        var pnls      = closed.Select(t => t.PnlPercent!.Value).ToList();
        var openTrade = trades.FirstOrDefault(t => !t.ExitDate.HasValue);
        var lastBuy   = signals.Where(s => s.Type == SignalType.Buy)
                               .OrderByDescending(s => s.Date)
                               .FirstOrDefault();

        return new TickerPerformance
        {
            Ticker           = ticker,
            Name             = name,
            Sector           = sector,
            TotalSignals     = signals.Count,
            ClosedTrades     = closed.Count,
            Wins             = wins.Count,
            Losses           = closed.Count - wins.Count,
            WinRate          = closed.Count == 0 ? 0
                               : Math.Round((double)wins.Count / closed.Count * 100, 1),
            TotalReturnPct   = pnls.Any() ? Math.Round(pnls.Sum(), 2) : 0,
            AvgReturnPct     = pnls.Any() ? Math.Round(pnls.Average(), 2) : 0,
            BestTradePct     = pnls.Any() ? pnls.Max() : 0,
            WorstTradePct    = pnls.Any() ? pnls.Min() : 0,
            AvgHoldDays      = closed.Any()
                               ? Math.Round(closed.Average(t =>
                                   (t.ExitDate!.Value - t.EntryDate).TotalDays), 1)
                               : 0,
            MaxDrawdownPct   = ComputeMaxDrawdown(trades),
            LastBuyDate      = lastBuy?.Date,
            LastBuyScore     = lastBuy?.Score ?? 0,
            HasOpenPosition  = openTrade != null,
            LastUpdated      = DateTime.UtcNow,
        };
    }

    private static decimal ComputeMaxDrawdown(List<Trade> trades)
    {
        decimal peak = 0, cumulative = 0, maxDd = 0;
        foreach (var t in trades.Where(t => t.PnlPercent.HasValue))
        {
            cumulative += t.PnlPercent!.Value;
            if (cumulative > peak) peak = cumulative;
            decimal dd = peak - cumulative;
            if (dd > maxDd) maxDd = dd;
        }
        return Math.Round(maxDd, 2);
    }
}
