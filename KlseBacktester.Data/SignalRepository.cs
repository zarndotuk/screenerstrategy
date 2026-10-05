using KlseBacktester.Models;
using MongoDB.Driver;

namespace KlseBacktester.Data;

/// <summary>
/// All MongoDB operations for active_signals and performance_stats collections.
/// Designed so the Next.js app can query the same collections directly.
/// </summary>
public class SignalRepository
{
    private readonly MongoContext _mongo;

    public SignalRepository(MongoContext mongo) => _mongo = mongo;

    // ═══════════════════════════════════════════════════════════
    //  ACTIVE SIGNALS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Open a new buy position. Safe to call multiple times — will not
    /// create a duplicate if an open signal for this ticker already exists.
    /// </summary>
    public async Task OpenBuyAsync(
        string ticker, string name, string sector,
        DateTime buyDate, decimal buyPrice, int buyScore, decimal stopLoss,
        decimal? targetPrice = null)
    {
        // Prevent duplicate open positions for the same ticker
        var existing = await GetOpenSignalAsync(ticker);
        if (existing != null) return;

        var signal = new ActiveSignal
        {
            Ticker      = ticker,
            Name        = name,
            Sector      = sector,
            BuyDate     = buyDate,
            BuyPrice    = buyPrice,
            BuyScore    = buyScore,
            StopLoss    = stopLoss,
            TargetPrice = targetPrice,
            Status      = SignalStatus.Open,
            LastUpdated = DateTime.UtcNow,
        };

        await _mongo.ActiveSignals.InsertOneAsync(signal);
    }

    /// <summary>
    /// Close an open position with sell data and compute P&L.
    /// </summary>
    public async Task CloseSellAsync(
        string ticker, DateTime sellDate, decimal sellPrice, int sellScore,
        string? exitReason = null)
    {
        var open = await GetOpenSignalAsync(ticker);
        if (open == null) return;

        decimal pnlPct = Math.Round((sellPrice - open.BuyPrice) / open.BuyPrice * 100, 2);
        decimal pnlAbs = Math.Round(sellPrice - open.BuyPrice, 4);
        int holdDays   = (int)(sellDate - open.BuyDate).TotalDays;

        var update = Builders<ActiveSignal>.Update
            .Set(s => s.SellDate,    sellDate)
            .Set(s => s.SellPrice,   sellPrice)
            .Set(s => s.SellScore,   sellScore)
            .Set(s => s.ExitReason,  exitReason)
            .Set(s => s.Status,      SignalStatus.Closed)
            .Set(s => s.PnlPct,      pnlPct)
            .Set(s => s.PnlAbs,      pnlAbs)
            .Set(s => s.HoldDays,    holdDays)
            .Set(s => s.LastUpdated, DateTime.UtcNow);

        await _mongo.ActiveSignals.UpdateOneAsync(
            Builders<ActiveSignal>.Filter.And(
                Builders<ActiveSignal>.Filter.Eq(s => s.Ticker, ticker),
                Builders<ActiveSignal>.Filter.Eq(s => s.Status, SignalStatus.Open)),
            update);
    }

    /// <summary>Get the current open (unsold) position for a ticker, or null.</summary>
    public async Task<ActiveSignal?> GetOpenSignalAsync(string ticker)
    {
        return await _mongo.ActiveSignals
            .Find(Builders<ActiveSignal>.Filter.And(
                Builders<ActiveSignal>.Filter.Eq(s => s.Ticker, ticker),
                Builders<ActiveSignal>.Filter.Eq(s => s.Status, SignalStatus.Open)))
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// All stocks with an open buy signal — optionally filtered to those
    /// whose buy signal fired within the last <paramref name="withinDays"/> days.
    /// </summary>
    public async Task<List<ActiveSignal>> GetOpenSignalsAsync(int? withinDays = null)
    {
        var filter = Builders<ActiveSignal>.Filter
            .Eq(s => s.Status, SignalStatus.Open);

        if (withinDays.HasValue)
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-withinDays.Value);
            filter = Builders<ActiveSignal>.Filter.And(
                filter,
                Builders<ActiveSignal>.Filter.Gte(s => s.BuyDate, cutoff));
        }

        return await _mongo.ActiveSignals
            .Find(filter)
            .SortByDescending(s => s.BuyDate)
            .ToListAsync();
    }

    /// <summary>All closed (sold) signals, newest first.</summary>
    public async Task<List<ActiveSignal>> GetClosedSignalsAsync(int limit = 100)
    {
        return await _mongo.ActiveSignals
            .Find(Builders<ActiveSignal>.Filter.Eq(s => s.Status, SignalStatus.Closed))
            .SortByDescending(s => s.SellDate)
            .Limit(limit)
            .ToListAsync();
    }

    /// <summary>All signals (open + closed) for a ticker, newest first.</summary>
    public async Task<List<ActiveSignal>> GetByTickerAsync(string ticker)
    {
        return await _mongo.ActiveSignals
            .Find(Builders<ActiveSignal>.Filter.Eq(s => s.Ticker, ticker))
            .SortByDescending(s => s.BuyDate)
            .ToListAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  PERFORMANCE STATS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Upsert aggregate stats for a ticker (called after each backtest run).</summary>
    public async Task UpsertPerformanceAsync(TickerPerformance stats)
    {
        var filter = Builders<TickerPerformance>.Filter.Eq(p => p.Ticker, stats.Ticker);
        await _mongo.PerformanceStats.ReplaceOneAsync(filter, stats,
            new ReplaceOptions { IsUpsert = true });
    }

    /// <summary>All tickers ranked by total return, descending.</summary>
    public async Task<List<TickerPerformance>> GetAllPerformanceAsync()
    {
        return await _mongo.PerformanceStats
            .Find(Builders<TickerPerformance>.Filter.Empty)
            .SortByDescending(p => p.TotalReturnPct)
            .ToListAsync();
    }

    /// <summary>Only tickers that currently have an open position.</summary>
    public async Task<List<TickerPerformance>> GetOpenPositionPerformanceAsync()
    {
        return await _mongo.PerformanceStats
            .Find(Builders<TickerPerformance>.Filter.Eq(p => p.HasOpenPosition, true))
            .SortByDescending(p => p.TotalReturnPct)
            .ToListAsync();
    }

    /// <summary>Performance for a single ticker.</summary>
    public async Task<TickerPerformance?> GetTickerPerformanceAsync(string ticker)
    {
        return await _mongo.PerformanceStats
            .Find(Builders<TickerPerformance>.Filter.Eq(p => p.Ticker, ticker))
            .FirstOrDefaultAsync();
    }

    public async Task<BacktestResult?> GetLatestBacktestResultAsync(string ticker)
    {
        return await _mongo.BacktestResults
            .Find(Builders<BacktestResult>.Filter.Eq(r => r.Ticker, ticker))
            .SortByDescending(r => r.RunDate)
            .FirstOrDefaultAsync();
    }
}
