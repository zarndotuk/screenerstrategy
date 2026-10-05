using MongoDB.Driver;
using KlseBacktester.Models;

namespace KlseBacktester.Data;

public class MongoContext
{
    private readonly IMongoDatabase _db;

    public MongoContext(string connectionString, string databaseName = "klse_backtest")
    {
        var client = new MongoClient(connectionString);
        _db = client.GetDatabase(databaseName);
        EnsureIndexes();
    }

    public IMongoCollection<StockPrice> Prices =>
        _db.GetCollection<StockPrice>("prices");

    public IMongoCollection<StockInfo> Stocks =>
        _db.GetCollection<StockInfo>("stocks");

    public IMongoCollection<BacktestResult> BacktestResults =>
        _db.GetCollection<BacktestResult>("backtest_results");

    /// <summary>Buy/sell lifecycle records — read by the Next.js app.</summary>
    public IMongoCollection<ActiveSignal> ActiveSignals =>
        _db.GetCollection<ActiveSignal>("active_signals");

    /// <summary>Aggregate per-ticker performance stats — read by the Next.js app.</summary>
    public IMongoCollection<TickerPerformance> PerformanceStats =>
        _db.GetCollection<TickerPerformance>("performance_stats");

    private void EnsureIndexes()
    {
        // Compound index on (ticker, date) — unique, fast range queries
        var priceIndex = new CreateIndexModel<StockPrice>(
            Builders<StockPrice>.IndexKeys
                .Ascending(p => p.Ticker)
                .Ascending(p => p.Date),
            new CreateIndexOptions { Unique = true, Name = "ticker_date_unique" }
        );
        Prices.Indexes.CreateOne(priceIndex);

        // Unique ticker index on stocks collection
        var stockIndex = new CreateIndexModel<StockInfo>(
            Builders<StockInfo>.IndexKeys.Ascending(s => s.Ticker),
            new CreateIndexOptions { Unique = true, Name = "ticker_unique" }
        );
        Stocks.Indexes.CreateOne(stockIndex);

        // Index for querying backtest results by ticker + run date
        var resultIndex = new CreateIndexModel<BacktestResult>(
            Builders<BacktestResult>.IndexKeys
                .Ascending(r => r.Ticker)
                .Descending(r => r.RunDate),
            new CreateIndexOptions { Name = "ticker_rundate" }
        );
        BacktestResults.Indexes.CreateOne(resultIndex);

        // active_signals: ticker + status (most common query pattern)
        var signalIndex = new CreateIndexModel<ActiveSignal>(
            Builders<ActiveSignal>.IndexKeys
                .Ascending(s => s.Ticker)
                .Ascending(s => s.Status),
            new CreateIndexOptions { Name = "signal_ticker_status" }
        );
        ActiveSignals.Indexes.CreateOne(signalIndex);

        // active_signals: buy_date descending (for "last N days" filter)
        var signalDateIndex = new CreateIndexModel<ActiveSignal>(
            Builders<ActiveSignal>.IndexKeys.Descending(s => s.BuyDate),
            new CreateIndexOptions { Name = "signal_buy_date" }
        );
        ActiveSignals.Indexes.CreateOne(signalDateIndex);

        // performance_stats: unique per ticker
        var perfIndex = new CreateIndexModel<TickerPerformance>(
            Builders<TickerPerformance>.IndexKeys.Ascending(p => p.Ticker),
            new CreateIndexOptions { Unique = true, Name = "perf_ticker_unique" }
        );
        PerformanceStats.Indexes.CreateOne(perfIndex);
    }
}
