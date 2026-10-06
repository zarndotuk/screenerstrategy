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
    private const int MaxDirectFetchDays = 186;
    private const int MinimumBootstrapRows = 5;
    private const int StartGapToleranceDays = 5;
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

            var calledYahoo = true;
            try
            {
                calledYahoo = await FetchAndStoreTicker(
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

            // Throttle only when Yahoo was actually hit; cached tickers need no delay.
            if (calledYahoo && i < total - 1)
                await Task.Delay(DelayBetweenTickersMs);
        }
    }

    /// <summary>
    /// Fetch and store a single ticker. Upserts into MongoDB.
    /// Returns true if Yahoo was called (false when everything was already cached).
    /// </summary>
    public async Task<bool> FetchAndStoreTicker(
        string ticker, string name, string sector,
        DateTime from, DateTime to,
        bool backfillUntilCovered = false)
    {
        // Never store a session that hasn't closed yet: stored bars are final, so
        // they never need to be fetched again.
        var lastFinal = BursaCalendar.LastCompletedSessionDate();
        if (to.Date > lastFinal)
            to = lastFinal;

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
        var fetchedMetadata = false;

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
                    fetchedMetadata = true;
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
                var candles = chart.Candles
                    .Where(c => c.Date >= rangeFrom.Date && c.Date <= rangeTo.Date)
                    .ToList();

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
        return fetchRound > 0 || fetchedMetadata;
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

    /// <summary>
    /// Fetch daily candles from Yahoo (without storing) and compute the mean daily volume per ticker.
    /// Days with no reported volume count as zero. Tickers with no data or a failed fetch
    /// are returned with a null AverageVolume and an Error message.
    /// </summary>
    public async Task<List<TickerVolumeStat>> GetAverageDailyVolumesAsync(
        IReadOnlyList<(string Ticker, string Name, string Sector)> tickers,
        DateTime from,
        DateTime to,
        Action<string, int, int>? onProgress = null)
    {
        var stats = new List<TickerVolumeStat>(tickers.Count);

        for (int i = 0; i < tickers.Count; i++)
        {
            var (symbol, name, sector) = tickers[i];
            onProgress?.Invoke(symbol, i + 1, tickers.Count);

            try
            {
                var candles = (await GetHistoricalWithRetryAsync(symbol, from, to)).Candles;
                stats.Add(candles.Count == 0
                    ? new TickerVolumeStat(symbol, name, sector, null, 0, "No data returned")
                    : new TickerVolumeStat(symbol, name, sector, candles.Average(c => (double)c.Volume), candles.Count, null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch volume for {Ticker}: {Message}", symbol, ex.Message);
                stats.Add(new TickerVolumeStat(symbol, name, sector, null, 0, ex.Message));
            }

            if (i < tickers.Count - 1)
                await Task.Delay(DelayBetweenTickersMs);
        }

        return stats;
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
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{encodedTicker}" +
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
            var bootstrapFrom = (to.Date - from.Date).TotalDays <= MaxDirectFetchDays
                ? from.Date
                : MaxDate(from.Date, to.Date.AddDays(-InitialBootstrapDays));
            return new List<(DateTime From, DateTime To)> { (bootstrapFrom, to.Date) };
        }

        var ranges = new List<(DateTime From, DateTime To)>();
        // Tolerate weekends/holidays at the start of the window so they don't trigger a refetch every run.
        if (existingFrom.Value.Date > from.Date.AddDays(StartGapToleranceDays))
        {
            var backfillTo = existingFrom.Value.Date.AddDays(-1);
            var backfillFrom = MaxDate(from.Date, backfillTo.AddDays(-HistoricalBackfillChunkDays));
            ranges.Add((backfillFrom, backfillTo));
        }

        // Only fetch sessions after the last stored bar; stored bars are final and never refetched.
        var forwardFrom = existingTo.Value.Date.AddDays(1);
        if (HasWeekday(forwardFrom, to.Date))
        {
            ranges.Add((forwardFrom, to.Date));
        }

        return ranges
            .Where(r => r.From <= r.To)
            .ToList();
    }

    /// <summary>True when [from, to] contains at least one Mon–Fri date (Bursa doesn't trade weekends).</summary>
    private static bool HasWeekday(DateTime from, DateTime to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                return true;
        }

        return false;
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

    public sealed record TickerVolumeStat(
        string Ticker,
        string Name,
        string Sector,
        double? AverageVolume,
        int Bars,
        string? Error);

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
