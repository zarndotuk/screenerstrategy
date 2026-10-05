using KlseBacktester.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Text.Json;

namespace KlseBacktester.Data;

public class YahooDataFetcher
{
    private readonly MongoContext _mongo;
    private readonly ILogger<YahooDataFetcher> _logger;
    private static readonly HttpClient Http = CreateHttpClient();

    // Yahoo Finance rate limits aggressively, especially around the crumb endpoint.
    private const int DelayBetweenTickersMs = 6000;
    private const int MaxFetchAttempts = 5;
    private const int InitialBootstrapDays = 30;
    private const int HistoricalBackfillChunkDays = 90;
    private const int MinimumBootstrapRows = 5;
    private const int RecentDataFreshnessDays = 7;
    private static readonly TimeSpan InitialRateLimitBackoff = TimeSpan.FromSeconds(30);

    public YahooDataFetcher(MongoContext mongo, ILogger<YahooDataFetcher> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    /// <summary>
    /// Populate (or refresh) price data for all KLSE tickers.
    /// Uses upsert so re-running is safe and idempotent.
    /// </summary>
    public async Task PopulateAllAsync(
        DateTime from,
        DateTime to,
        Action<string, int, int>? onProgress = null)
    {
        await PopulateAsync(KlseTickers.All, from, to, onProgress);
    }

    /// <summary>
    /// Populate (or refresh) price data for the selected KLSE tickers.
    /// Uses upsert so re-running is safe and idempotent.
    /// </summary>
    public async Task PopulateAsync(
        IReadOnlyList<(string Ticker, string Name, string Sector)> tickers,
        DateTime from,
        DateTime to,
        Action<string, int, int>? onProgress = null)
    {
        int total = tickers.Count;

        for (int i = 0; i < total; i++)
        {
            var (symbol, name, sector) = tickers[i];
            onProgress?.Invoke(symbol, i + 1, total);

            try
            {
                await FetchAndStoreTicker(
                    symbol,
                    name,
                    sector,
                    from,
                    to,
                    backfillUntilCovered: total == 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch {Ticker}: {Message}", symbol, ex.Message);
                Console.WriteLine($"  [WARN] {symbol} skipped: {ex.Message}");
            }

            await Task.Delay(DelayBetweenTickersMs);
        }
    }

    /// <summary>Fetch and store a single ticker. Upserts into MongoDB.</summary>
    public async Task FetchAndStoreTicker(
        string ticker, string name, string sector,
        DateTime from, DateTime to,
        bool backfillUntilCovered = false)
    {
        // Upsert configured metadata first. Ticker-only configurations are
        // enriched from Yahoo metadata when price data is fetched below.
        var stockFilter = Builders<StockInfo>.Filter.Eq(s => s.Ticker, ticker);
        var stockUpdate = Builders<StockInfo>.Update
            .Set(s => s.Name, name)
            .Set(s => s.Sector, sector)
            .Set(s => s.LastUpdated, DateTime.UtcNow)
            .SetOnInsert(s => s.Ticker, ticker);

        await _mongo.Stocks.UpdateOneAsync(
            stockFilter, stockUpdate,
            new UpdateOptions { IsUpsert = true });

        var totalWrites = 0;
        var fetchRound = 0;

        while (true)
        {
            var ranges = await GetMissingDateRangesAsync(ticker, from, to);
            if (ranges.Count == 0)
            {
                if (IsTickerOnlyName(ticker, name))
                {
                    var metadata = await GetHistoricalWithRetryAsync(
                        ticker,
                        to.Date.AddDays(-InitialBootstrapDays),
                        to.Date);
                    name = await UpdateStockNameAsync(
                        stockFilter,
                        ticker,
                        name,
                        metadata.Name);
                }

                _logger.LogInformation("Prices already cached for {Ticker} from {From:d} to {To:d}", ticker, from, to);
                break;
            }

            fetchRound++;
            var writesThisRound = 0;

            foreach (var (rangeFrom, rangeTo) in ranges)
            {
                // Fetch OHLCV from Yahoo Finance
                var chart = await GetHistoricalWithRetryAsync(ticker, rangeFrom, rangeTo);
                var candles = chart.Candles;

                if (!candles.Any())
                {
                    _logger.LogWarning("No data returned for {Ticker} from {From:d} to {To:d}", ticker, rangeFrom, rangeTo);
                    continue;
                }

                name = await UpdateStockNameAsync(
                    stockFilter,
                    ticker,
                    name,
                    chart.Name);

                Console.WriteLine(
                    $"  [INFO] {ticker}: fetched {candles.Count} candles " +
                    $"({candles.Min(c => c.Date):yyyy-MM-dd} -> {candles.Max(c => c.Date):yyyy-MM-dd})");

                // Bulk upsert prices
                var writes = new List<WriteModel<StockPrice>>();
                foreach (var candle in candles)
                {
                    var filter = Builders<StockPrice>.Filter.And(
                        Builders<StockPrice>.Filter.Eq(p => p.Ticker, ticker),
                        Builders<StockPrice>.Filter.Eq(p => p.Date, candle.Date)
                    );

                    var price = new StockPrice
                    {
                        Ticker        = ticker,
                        Date          = candle.Date,
                        Open          = candle.Open,
                        High          = candle.High,
                        Low           = candle.Low,
                        Close         = candle.Close,
                        Volume        = candle.Volume,
                        AdjustedClose = candle.AdjustedClose,
                    };

                    writes.Add(new ReplaceOneModel<StockPrice>(filter, price)
                    {
                        IsUpsert = true
                    });
                }

                if (writes.Any())
                {
                    var result = await _mongo.Prices.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false });
                    var inserted = result.Upserts.Count;
                    writesThisRound += inserted;
                    totalWrites += inserted;
                }
            }

            if (!backfillUntilCovered || writesThisRound == 0)
            {
                break;
            }

            Console.WriteLine(
                $"  [INFO] {ticker}: backfill round {fetchRound} stored {writesThisRound} candles; checking next missing range...");

            await Task.Delay(DelayBetweenTickersMs);
        }

        _logger.LogInformation("Upserted {Count} candles for {Ticker}", totalWrites, ticker);
    }

    /// <summary>Load sorted daily prices for a ticker from MongoDB.</summary>
    public async Task<List<StockPrice>> GetPricesAsync(
        string ticker, DateTime from, DateTime to)
    {
        var filter = Builders<StockPrice>.Filter.And(
            Builders<StockPrice>.Filter.Eq(p => p.Ticker, ticker),
            Builders<StockPrice>.Filter.Gte(p => p.Date, from),
            Builders<StockPrice>.Filter.Lte(p => p.Date, to)
        );

        return await _mongo.Prices
            .Find(filter)
            .SortBy(p => p.Date)
            .ToListAsync();
    }

    public async Task<StockInfo?> GetStockInfoAsync(string ticker)
    {
        return await _mongo.Stocks
            .Find(Builders<StockInfo>.Filter.Eq(s => s.Ticker, ticker))
            .FirstOrDefaultAsync();
    }

    /// <summary>Fetch Yahoo price history with backoff for transient rate limits.</summary>
    private async Task<YahooChartData> GetHistoricalWithRetryAsync(
        string ticker,
        DateTime from,
        DateTime to)
    {
        for (int attempt = 1; attempt <= MaxFetchAttempts; attempt++)
        {
            try
            {
                return await GetHistoricalFromChartApiAsync(ticker, from, to);
            }
            catch (Exception ex) when (IsRateLimitError(ex) && attempt < MaxFetchAttempts)
            {
                var delay = BackoffDelay(attempt);

                _logger.LogWarning(
                    "Yahoo rate-limited {Ticker} on attempt {Attempt}/{MaxAttempts}. Retrying in {DelaySeconds:n0}s.",
                    ticker,
                    attempt,
                    MaxFetchAttempts,
                    delay.TotalSeconds);

                Console.WriteLine(
                    $"  [WARN] Yahoo rate-limited {ticker}; retrying in {delay.TotalSeconds:n0}s " +
                    $"(attempt {attempt}/{MaxFetchAttempts})");

                await Task.Delay(delay);
            }
        }

        return await GetHistoricalFromChartApiAsync(ticker, from, to);
    }

    private static async Task<YahooChartData> GetHistoricalFromChartApiAsync(
        string ticker,
        DateTime from,
        DateTime to)
    {
        var period1 = ToUnixSeconds(from.Date);
        var period2 = ToUnixSeconds(to.Date.AddDays(1));
        var encodedTicker = Uri.EscapeDataString(ticker);
        var url = IsRecentRange(from, to)
            ? $"https://query1.finance.yahoo.com/v8/finance/chart/{encodedTicker}" +
              "?range=1mo&interval=1d&events=history&includeAdjustedClose=true"
            : $"https://query1.finance.yahoo.com/v8/finance/chart/{encodedTicker}" +
              $"?period1={period1}&period2={period2}&interval=1d&events=history&includeAdjustedClose=true";

        using var response = await Http.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Yahoo chart API returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {ticker}: {json}");
        }

        return ParseChartResponse(ticker, json);
    }

    private static YahooChartData ParseChartResponse(string ticker, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement
            .GetProperty("chart")
            .GetProperty("result");

        if (result.GetArrayLength() == 0)
        {
            return new YahooChartData(Array.Empty<PriceCandle>(), null);
        }

        var firstResult = result[0];
        var stockName = GetStockName(firstResult);
        if (!firstResult.TryGetProperty("timestamp", out var timestamps))
        {
            return new YahooChartData(Array.Empty<PriceCandle>(), stockName);
        }

        var quote = firstResult
            .GetProperty("indicators")
            .GetProperty("quote")[0];

        var opens = quote.GetProperty("open");
        var highs = quote.GetProperty("high");
        var lows = quote.GetProperty("low");
        var closes = quote.GetProperty("close");
        var volumes = quote.GetProperty("volume");
        var adjustedCloses = GetAdjustedCloseArray(firstResult);

        var candles = new List<PriceCandle>();
        for (var i = 0; i < timestamps.GetArrayLength(); i++)
        {
            if (!TryGetDecimal(opens[i], out var open)
                || !TryGetDecimal(highs[i], out var high)
                || !TryGetDecimal(lows[i], out var low)
                || !TryGetDecimal(closes[i], out var close))
            {
                continue;
            }

            var adjustedClose = adjustedCloses.HasValue
                && i < adjustedCloses.Value.GetArrayLength()
                && TryGetDecimal(adjustedCloses.Value[i], out var adj)
                    ? adj
                    : close;

            var volume = TryGetInt64(volumes[i], out var parsedVolume)
                ? parsedVolume
                : 0;

            candles.Add(new PriceCandle(
                Date: DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime.Date,
                Open: open,
                High: high,
                Low: low,
                Close: close,
                Volume: volume,
                AdjustedClose: adjustedClose));
        }

        return new YahooChartData(candles, stockName);
    }

    private static string? GetStockName(JsonElement result)
    {
        if (!result.TryGetProperty("meta", out var meta))
        {
            return null;
        }

        foreach (var propertyName in new[] { "longName", "shortName" })
        {
            if (meta.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }

        return null;
    }

    private static bool IsTickerOnlyName(string ticker, string name) =>
        string.IsNullOrWhiteSpace(name)
        || string.Equals(ticker, name, StringComparison.OrdinalIgnoreCase);

    private async Task<string> UpdateStockNameAsync(
        FilterDefinition<StockInfo> stockFilter,
        string ticker,
        string currentName,
        string? yahooName)
    {
        if (!IsTickerOnlyName(ticker, currentName) || string.IsNullOrWhiteSpace(yahooName))
        {
            return currentName;
        }

        var resolvedName = yahooName.Trim();
        await _mongo.Stocks.UpdateOneAsync(
            stockFilter,
            Builders<StockInfo>.Update
                .Set(s => s.Name, resolvedName)
                .Set(s => s.LastUpdated, DateTime.UtcNow));

        return resolvedName;
    }

    private static JsonElement? GetAdjustedCloseArray(JsonElement result)
    {
        var indicators = result.GetProperty("indicators");
        if (!indicators.TryGetProperty("adjclose", out var adjustedClose)
            || adjustedClose.GetArrayLength() == 0
            || !adjustedClose[0].TryGetProperty("adjclose", out var values))
        {
            return null;
        }

        return values;
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        return element.ValueKind != JsonValueKind.Null
            && element.TryGetDecimal(out value);
    }

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind != JsonValueKind.Null
            && element.TryGetInt64(out value);
    }

    private static long ToUnixSeconds(DateTime date)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    private static bool IsRecentRange(DateTime from, DateTime to)
    {
        return (to.Date - from.Date).TotalDays <= InitialBootstrapDays + 1;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/125.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static bool IsRateLimitError(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan BackoffDelay(int attempt)
    {
        var multiplier = Math.Pow(2, attempt - 1);
        var jitterSeconds = Random.Shared.Next(0, 10);
        return TimeSpan.FromSeconds(InitialRateLimitBackoff.TotalSeconds * multiplier + jitterSeconds);
    }

    private async Task<List<(DateTime From, DateTime To)>> GetMissingDateRangesAsync(
        string ticker,
        DateTime from,
        DateTime to)
    {
        var existingCount = await _mongo.Prices
            .CountDocumentsAsync(Builders<StockPrice>.Filter.Eq(p => p.Ticker, ticker));
        var existingFrom = await GetBoundaryDateAsync(ticker, ascending: true);
        var existingTo = await GetBoundaryDateAsync(ticker, ascending: false);

        if (existingCount < MinimumBootstrapRows || !existingFrom.HasValue || !existingTo.HasValue)
        {
            var bootstrapFrom = MaxDate(from.Date, to.Date.AddDays(-InitialBootstrapDays));
            return new List<(DateTime From, DateTime To)> { (bootstrapFrom, to.Date) };
        }

        var ranges = new List<(DateTime From, DateTime To)>();
        if (existingFrom.Value.Date > from.Date)
        {
            var backfillTo = existingFrom.Value.Date.AddDays(-1);
            var backfillFrom = MaxDate(from.Date, backfillTo.AddDays(-HistoricalBackfillChunkDays));
            ranges.Add((backfillFrom, backfillTo));
        }

        if (existingTo.Value.Date < to.Date.AddDays(-RecentDataFreshnessDays))
        {
            ranges.Add((existingTo.Value.Date.AddDays(1), to.Date));
        }

        return ranges
            .Where(r => r.From <= r.To)
            .ToList();
    }

    private static DateTime MaxDate(DateTime first, DateTime second)
    {
        return first > second ? first : second;
    }

    private async Task<DateTime?> GetBoundaryDateAsync(string ticker, bool ascending)
    {
        var query = _mongo.Prices
            .Find(Builders<StockPrice>.Filter.Eq(p => p.Ticker, ticker));

        var price = ascending
            ? await query.SortBy(p => p.Date).FirstOrDefaultAsync()
            : await query.SortByDescending(p => p.Date).FirstOrDefaultAsync();

        return price?.Date;
    }

    private sealed record YahooChartData(
        IReadOnlyList<PriceCandle> Candles,
        string? Name);

    private sealed record PriceCandle(
        DateTime Date,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        long Volume,
        decimal AdjustedClose);
}
