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
        services.AddSingleton(_ => new ScoreEngine
        {
            EntryMinScore = 7,
            ExitMinScore = 7,
            CooldownBars = 5,
            RequiredBreakoutConfirmations = 1,
            RequireTrendQualityForEntry = true,
            LongTrendSlopeLookback = 20,
            RequiredClosesAboveEma200 = 5,
            AllowSameBarBreakoutEntry = true,
            SameBarBreakoutMinScore = 9,
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
        var curatedStocks = KlseTickers.All
            .GroupBy(s => s.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(s => s.Ticker, StringComparer.OrdinalIgnoreCase);

        var configured = settings.KlseStocks
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

        return configured.Count > 0 ? configured : curatedStocks.Values.ToList();
    }
}
