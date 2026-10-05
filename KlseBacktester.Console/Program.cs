using KlseBacktester.Console;
using Microsoft.Extensions.DependencyInjection;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services);

        await using var serviceProvider = services.BuildServiceProvider();

        var app = serviceProvider.GetRequiredService<BacktesterConsoleApp>();

        if (args.Any(arg => string.Equals(arg, "populate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "populate-db", StringComparison.OrdinalIgnoreCase)))
        {
            await app.PopulateDbAsync();
            return;
        }

        var backtestArgIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "backtest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "run-backtest", StringComparison.OrdinalIgnoreCase));
        if (backtestArgIndex >= 0)
        {
            var ticker = args.Skip(backtestArgIndex + 1)
                .FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
            await app.RunBacktestAsync(ticker);
            return;
        }

        if (args.Any(arg => string.Equals(arg, "open-positions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "open", StringComparison.OrdinalIgnoreCase)))
        {
            await app.ShowOpenPositionsAsync();
            return;
        }

        if (args.Any(arg => string.Equals(arg, "closed-trades", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "closed", StringComparison.OrdinalIgnoreCase)))
        {
            await app.ShowClosedTradesAsync();
            return;
        }

        if (args.Length >= 3
            && string.Equals(args[0], "calls", StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParse(args[2], out var callDate))
        {
            await app.ShowCallsForDateAsync(args[1], callDate);
            return;
        }

        if (args.Length >= 2
            && string.Equals(args[0], "trades", StringComparison.OrdinalIgnoreCase))
        {
            await app.ShowBacktestTradesAsync(args[1]);
            return;
        }

        await app.RunAsync();
    }
}
