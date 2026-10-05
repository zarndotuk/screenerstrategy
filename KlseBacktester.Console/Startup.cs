using System.Text.Json;
using KlseBacktester.Core;
using KlseBacktester.Data;
using KlseBacktester.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KlseBacktester.Console;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        var settings = LoadSettings();
        WarmupCalculator.Configure(settings.WarmupBars);

        services.AddSingleton(settings);
        services.AddSingleton<IReadOnlyList<(string Ticker, string Name, string Sector)>>(
            ResolveKlseStocks(settings));

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddSingleton(sp =>
            new MongoContext(settings.MongoConnectionString, settings.DatabaseName));

        services.AddSingleton<YahooDataFetcher>();
        services.AddSingleton<SignalRepository>();
        var strategy = settings.Strategy;
        services.AddSingleton(_ => new ScoreEngine
        {
            EntryMinScore = strategy.EntryMinScore,
            ExitMinScore = strategy.ExitMinScore,
            CooldownBars = strategy.CooldownBars,
            RequiredBreakoutConfirmations = strategy.RequiredBreakoutConfirmations,
            RequireTrendQualityForEntry = strategy.RequireTrendQualityForEntry,
            TrendSlopeLookback = strategy.TrendSlopeLookback,
            LongTrendSlopeLookback = strategy.LongTrendSlopeLookback,
            RequiredClosesAboveEma200 = strategy.RequiredClosesAboveEma200,
            AllowSameBarBreakoutEntry = strategy.AllowSameBarBreakoutEntry,
            SameBarBreakoutMinScore = strategy.SameBarBreakoutMinScore,
            AllowSetupBarBreakout = strategy.AllowSetupBarBreakout,
            BreakoutCloseNearHighPercent = strategy.BreakoutCloseNearHighPercent,
        });
        services.AddSingleton<BacktestRunner>();
        services.AddSingleton<BacktesterConsoleApp>();
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new AppSettings();
            }
        }
        catch
        {
            // Fall through to defaults.
        }

        return new AppSettings();
    }

    private static List<(string Ticker, string Name, string Sector)> ResolveKlseStocks(AppSettings settings)
    {
        var configured = NormalizeStocks(settings.KlseStocks);
        if (configured.Count > 0)
            return configured;

        var universe = NormalizeStocks(settings.KlseStockUniverse);
        return universe.Count > 0 ? universe : CuratedStocks().Values.ToList();
    }

    private static Dictionary<string, (string Ticker, string Name, string Sector)> CuratedStocks() =>
        KlseTickers.All
            .GroupBy(s => s.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(s => s.Ticker, StringComparer.OrdinalIgnoreCase);

    public static List<(string Ticker, string Name, string Sector)> NormalizeStocks(IEnumerable<StockDefinition> stocks)
    {
        var curatedStocks = CuratedStocks();

        return stocks
            .Where(s => !string.IsNullOrWhiteSpace(s.Ticker))
            .Select(s =>
            {
                var ticker = s.Ticker.Trim().ToUpperInvariant();
                if (!ticker.EndsWith(".KL", StringComparison.OrdinalIgnoreCase))
                {
                    ticker += ".KL";
                }

                curatedStocks.TryGetValue(ticker, out var curated);

                return (
                    Ticker: ticker,
                    Name: !string.IsNullOrWhiteSpace(s.Name)
                        ? s.Name.Trim()
                        : curated.Name ?? ticker,
                    Sector: !string.IsNullOrWhiteSpace(s.Sector)
                        ? s.Sector.Trim()
                        : curated.Sector ?? "Unknown");
            })
            .GroupBy(s => s.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
