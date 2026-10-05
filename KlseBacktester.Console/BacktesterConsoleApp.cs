using KlseBacktester.Core;
using KlseBacktester.Data;
using KlseBacktester.Models;
using Spectre.Console;

namespace KlseBacktester.Console;

public class BacktesterConsoleApp
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<(string Ticker, string Name, string Sector)> _klseStocks;
    private readonly YahooDataFetcher _fetcher;
    private readonly SignalRepository _signalRepo;
    private readonly ScoreEngine _engine;
    private readonly BacktestRunner _runner;

    public BacktesterConsoleApp(
        AppSettings settings,
        IReadOnlyList<(string Ticker, string Name, string Sector)> klseStocks,
        YahooDataFetcher fetcher,
        SignalRepository signalRepo,
        ScoreEngine engine,
        BacktestRunner runner)
    {
        _settings = settings;
        _klseStocks = klseStocks;
        _fetcher = fetcher;
        _signalRepo = signalRepo;
        _engine = engine;
        _runner = runner;
    }

    public async Task RunAsync()
    {
        ConsoleRenderer.Header("KLSE Backtest");
        AnsiConsole.MarkupLine("[dim]Score-Based Entry/Exit System | Bursa Malaysia[/]\n");

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What would you like to do?[/]")
                    .PageSize(12)
                    .AddChoices(
                        "1. Populate DB  - fetch price history for configured KLSE stocks",
                        "2. Run Backtest - fetch data and backtest configured KLSE stocks",
                        "3. Buy Signals  - stocks with active buy signal (last N days)",
                        "4. Performance  - leaderboard ranked by total return",
                        "5. Closed Trades - view all completed trades with P&L",
                        "6. Ticker Detail - drill into a specific stock",
                        "7. Data Quality - check bar counts & warmup status per ticker",
                        "8. Settings     - view current configuration",
                        "0. Exit"));

            AnsiConsole.WriteLine();

            switch (choice[0])
            {
                case '1':
                    await PopulateDbAsync();
                    break;
                case '2':
                    await RunBacktestAsync(promptForTicker: true);
                    break;
                case '3':
                    await ShowBuySignals();
                    break;
                case '4':
                    await ShowLeaderboard();
                    break;
                case '5':
                    await ShowClosedTrades();
                    break;
                case '6':
                    await ShowTickerDetail();
                    break;
                case '7':
                    await ShowDataQuality();
                    break;
                case '8':
                    ShowSettings();
                    break;
                case '0':
                    AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
                    return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Press any key to return to menu...[/]");
            System.Console.ReadKey(true);
            AnsiConsole.Clear();
            ConsoleRenderer.Header("KLSE Backtest");
            AnsiConsole.MarkupLine("[dim]Score-Based Entry/Exit System | Bursa Malaysia[/]\n");
        }
    }

    public async Task PopulateDbAsync()
    {
        var backtestFrom = DateTime.Today.AddDays(-_settings.DefaultLookbackDays);
        var backtestTo = DateTime.Today;
        var fetchFrom = WarmupCalculator.FetchFrom(backtestFrom);
        var warmupDays = (int)(backtestFrom - fetchFrom).TotalDays;

        AnsiConsole.MarkupLine(
            $"Fetching [bold]{_klseStocks.Count}[/] configured KLSE tickers");
        AnsiConsole.Write(new Panel(
            $"Backtest window:  [yellow]{backtestFrom:dd MMM yyyy}[/] -> [yellow]{backtestTo:dd MMM yyyy}[/]\n" +
            $"Fetch from:       [yellow]{fetchFrom:dd MMM yyyy}[/] (+ {warmupDays} days warmup)\n" +
            $"Warmup bars:      [yellow]{WarmupCalculator.WarmupBars} trading bars[/] for EMA200 convergence\n" +
            $"Total fetch span: [yellow]{(int)(backtestTo - fetchFrom).TotalDays} calendar days[/]")
        {
            Header = new PanelHeader("[bold] Data Fetch Plan [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1),
        });

        AnsiConsole.WriteLine();

        await ConsoleRenderer.RunWithProgressAsync("Fetching prices", async onProgress =>
        {
            await _fetcher.PopulateAsync(_klseStocks, fetchFrom, backtestTo, onProgress);
        });

        ConsoleRenderer.Ok($"\nDone. Prices stored in MongoDB '[bold]{_settings.DatabaseName}[/]'.");
    }

    public async Task RunBacktestAsync(string? ticker = null, bool promptForTicker = false)
    {
        var selectedStocks = ResolveBacktestStocks(ticker, promptForTicker);
        if (selectedStocks.Count == 0)
            return;

        var backtestFrom = DateTime.Today.AddDays(-_settings.DefaultLookbackDays);
        var backtestTo = DateTime.Today;
        var fetchFrom = WarmupCalculator.FetchFrom(backtestFrom);

        AnsiConsole.MarkupLine(
            $"Running backtest on [bold]{selectedStocks.Count}[/] configured KLSE ticker(s) | " +
            $"Signal window: [yellow]{backtestFrom:dd MMM yyyy}[/] -> [yellow]{backtestTo:dd MMM yyyy}[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Warmup: {WarmupCalculator.WarmupBars} bars pre-fetched " +
            "so EMA200 is converged before first signal.[/]\n");

        await ConsoleRenderer.RunWithProgressAsync("Fetching latest prices", async onProgress =>
        {
            await _fetcher.PopulateAsync(selectedStocks, fetchFrom, backtestTo, onProgress);
        });

        AnsiConsole.WriteLine();

        var results = new List<BacktestResult>();

        await ConsoleRenderer.RunWithProgressAsync("Running backtest", async onProgress =>
        {
            results = await _runner.RunAsync(selectedStocks, backtestFrom, backtestTo, onProgress);
        });

        var ran = results.Count;
        var withClosedTrades = results.Count(r => r.TotalTrades > 0);
        var openPos = results.Count(r => r.OpenTrade != null);
        var allTrades = results.SelectMany(r => r.Trades).Where(t => t.PnlPercent.HasValue).ToList();
        var wins = allTrades.Count(t => t.IsWin == true);
        var buyCalls = results.SelectMany(r => r.Calls).Count(c => c.Type == SignalType.Buy);
        var sellCalls = results.SelectMany(r => r.Calls).Count(c => c.Type == SignalType.Sell);
        var buyConfirms = results.SelectMany(r => r.Confirmations).Count(c => c.Type == SignalType.Buy);
        var sellConfirms = results.SelectMany(r => r.Confirmations).Count(c => c.Type == SignalType.Sell);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            $"Tickers processed:  [bold]{ran}[/]\n" +
            $"Buy / Sell calls:   [green]{buyCalls}[/] / [red]{sellCalls}[/]\n" +
            $"Confirmed calls:    [green]{buyConfirms}[/] / [red]{sellConfirms}[/]\n" +
            $"With closed trades: [bold]{withClosedTrades}[/]\n" +
            $"Open positions now: [green]{openPos}[/]\n" +
            $"Closed trades:      [bold]{allTrades.Count}[/]\n" +
            $"Wins / Losses:      [green]{wins}[/] / [red]{allTrades.Count - wins}[/]\n" +
            $"System win rate:    [bold]{(allTrades.Any() ? (double)wins / allTrades.Count * 100 : 0):F1}%[/]")
        {
            Header = new PanelHeader("[bold green] Backtest Complete [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1),
        });
    }

    private IReadOnlyList<(string Ticker, string Name, string Sector)> ResolveBacktestStocks(
        string? ticker,
        bool promptForTicker)
    {
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            var selected = FindConfiguredStock(ticker);
            if (selected.HasValue)
                return [selected.Value];

            ConsoleRenderer.Warn($"Ticker '{ticker}' is not configured in appsettings.json.");
            ConsoleRenderer.Info("Configured tickers: " + string.Join(", ", _klseStocks.Select(s => s.Ticker)));
            return [];
        }

        if (!promptForTicker || _klseStocks.Count <= 1)
            return _klseStocks;

        var allChoice = "All configured tickers";
        var choices = new List<string> { allChoice };
        choices.AddRange(_klseStocks.Select(FormatStockChoice));

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Run backtest for which ticker?[/]")
                .PageSize(Math.Min(12, choices.Count))
                .AddChoices(choices));

        if (choice == allChoice)
            return _klseStocks;

        var tickerFromChoice = choice.Split(' ', StringSplitOptions.RemoveEmptyEntries).First();
        var stock = FindConfiguredStock(tickerFromChoice);
        return stock.HasValue ? [stock.Value] : [];
    }

    private (string Ticker, string Name, string Sector)? FindConfiguredStock(string input)
    {
        var normalized = NormalizeTicker(input);
        foreach (var stock in _klseStocks)
        {
            if (string.Equals(stock.Ticker, normalized, StringComparison.OrdinalIgnoreCase)
                || stock.Name.Contains(input.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return stock;
            }
        }

        return null;
    }

    private static string FormatStockChoice((string Ticker, string Name, string Sector) stock) =>
        $"{stock.Ticker} - {stock.Name}";

    public async Task ShowOpenPositionsAsync(int withinDays = 3650)
    {
        var signals = await _signalRepo.GetOpenSignalsAsync(withinDays: withinDays);
        ConsoleRenderer.RenderOpenBuySignals(signals, withinDays);
    }

    public async Task ShowClosedTradesAsync(int limit = 100)
    {
        var closed = await _signalRepo.GetClosedSignalsAsync(limit);
        ConsoleRenderer.RenderClosedTrades(closed);
    }

    public async Task ShowCallsForDateAsync(string ticker, DateTime date)
    {
        ticker = NormalizeTicker(ticker);
        var result = await _signalRepo.GetLatestBacktestResultAsync(ticker);

        if (result == null)
        {
            ConsoleRenderer.Warn($"No backtest result found for {ticker}.");
            return;
        }

        var calls = result.Calls.Where(c => c.Date.Date == date.Date).ToList();
        var confirmations = result.Confirmations.Where(c => c.Date.Date == date.Date).ToList();
        var signals = result.Signals.Where(s => s.Date.Date == date.Date).ToList();
        var openTrade = result.Trades
            .FirstOrDefault(t => t.EntryDate.Date <= date.Date
                && (!t.ExitDate.HasValue || t.ExitDate.Value.Date >= date.Date));

        AnsiConsole.Write(new Panel(
            $"Ticker:       [cyan]{ticker}[/]\n" +
            $"Date:         [yellow]{date:dd MMM yyyy}[/]\n" +
            $"Run date:     [yellow]{result.RunDate:dd MMM yyyy HH:mm} UTC[/]\n" +
            $"Raw calls:    [bold]{calls.Count}[/]\n" +
            $"Confirmations:[bold]{confirmations.Count}[/]\n" +
            $"Trade signals:[bold]{signals.Count}[/]\n" +
            $"Open trade:   {(openTrade == null ? "[dim]No[/]" : $"[green]Yes[/] from {openTrade.EntryDate:dd MMM yyyy} @ {openTrade.EntryPrice:F3}, target {openTrade.TargetPrice:F3}")}")
        {
            Header = new PanelHeader("[bold] Signal Diagnostics [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1),
        });

        foreach (var call in calls)
        {
            AnsiConsole.MarkupLine(
                $"CALL   {call.Type,-4} price [yellow]{call.Price:F3}[/] score [bold]{call.Score}[/] actionable [bold]{call.IsActionableSignal}[/]");
            if (call.Type == SignalType.Buy)
            {
                AnsiConsole.MarkupLine(
                    $"       gates: confirm {call.BuyConfirm}, trendQuality {call.TrendQuality}, sameBar {call.SameBarBreakoutEntry}");
            }
            if (call.EntryBreakdown != null)
            {
                var b = call.EntryBreakdown;
                AnsiConsole.MarkupLine(
                    $"       entry: ema20 {b.CloseAboveEma20}, stack20/50 {b.Ema20AboveEma50}, stack50/200 {b.Ema50AboveEma200}, rsi {b.RsiBetween55And70}, vol1.5 {b.VolumeAbove1_5x}, vol2 {b.VolumeAbove2x}, breakout {b.BreakoutAboveRecentHigh}, candle {b.CandleBullish}, market {b.MarketBullish}");
            }
            if (call.ExitBreakdown != null)
            {
                var b = call.ExitBreakdown;
                AnsiConsole.MarkupLine(
                    $"       exit: below20 {b.CloseBelowEma20}, stack20/50 {b.Ema20BelowEma50}, rsi55 {b.RsiBelow55}, rsi50 {b.RsiBelow50}, bearvol {b.BearishVolumeSpike}, recentlow {b.CloseBelowRecentLow}, extended {b.OverboughtExtension}");
            }
        }

        foreach (var confirmation in confirmations)
        {
            AnsiConsole.MarkupLine(
                $"CONF   {confirmation.Type,-4} price [yellow]{confirmation.Price:F3}[/] score [bold]{confirmation.Score}[/] actionable [bold]{confirmation.IsActionableSignal}[/]");
        }

        foreach (var signal in signals)
        {
            AnsiConsole.MarkupLine(
                $"SIGNAL {signal.Type,-4} price [yellow]{signal.Price:F3}[/] score [bold]{signal.Score}[/] reason {signal.ExitReason ?? "-"}");
        }
    }

    public async Task ShowBacktestTradesAsync(string ticker)
    {
        ticker = NormalizeTicker(ticker);
        var result = await _signalRepo.GetLatestBacktestResultAsync(ticker);

        if (result == null)
        {
            ConsoleRenderer.Warn($"No backtest result found for {ticker}.");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Trades for {ticker}[/] [dim](run {result.RunDate:dd MMM yyyy HH:mm} UTC)[/]");
        foreach (var trade in result.Trades)
        {
            var pnl = trade.PnlPercent.HasValue
                ? $"{(trade.PnlPercent >= 0 ? "+" : "")}{trade.PnlPercent:F2}%"
                : "open";
            AnsiConsole.MarkupLine(
                $"{trade.EntryDate:dd MMM yyyy} @ [yellow]{trade.EntryPrice:F3}[/] -> " +
                $"{(trade.ExitDate.HasValue ? trade.ExitDate.Value.ToString("dd MMM yyyy") : "open")} " +
                $"@ {(trade.ExitPrice.HasValue ? trade.ExitPrice.Value.ToString("F3") : "-")}  " +
                $"[bold]{pnl}[/]  {trade.ExitReason ?? "-"}");
        }
    }

    private async Task ShowBuySignals()
    {
        var days = AnsiConsole.Prompt(
            new TextPrompt<int>($"Show buy signals from last how many days? [dim](default {_settings.RecentSignalDays})[/]")
                .DefaultValue(_settings.RecentSignalDays)
                .ValidationErrorMessage("[red]Enter a positive number[/]")
                .Validate(d => d > 0));

        var signals = await _signalRepo.GetOpenSignalsAsync(withinDays: days);
        ConsoleRenderer.RenderOpenBuySignals(signals, days);
    }

    private async Task ShowLeaderboard()
    {
        var filter = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Show:")
                .AddChoices(
                    "All tickers",
                    "Only tickers with open position"));

        var stats = filter.StartsWith("Only")
            ? await _signalRepo.GetOpenPositionPerformanceAsync()
            : await _signalRepo.GetAllPerformanceAsync();

        ConsoleRenderer.RenderPerformanceLeaderboard(stats);
    }

    private async Task ShowClosedTrades()
    {
        var limit = AnsiConsole.Prompt(
            new TextPrompt<int>("Show last how many closed trades? [dim](default 50)[/]")
                .DefaultValue(50)
                .Validate(n => n > 0));

        var closed = await _signalRepo.GetClosedSignalsAsync(limit);
        ConsoleRenderer.RenderClosedTrades(closed);
    }

    private async Task ShowTickerDetail()
    {
        var ticker = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter ticker symbol [dim](e.g. 1155.KL)[/]:")
                .ValidationErrorMessage("[red]Ticker cannot be empty[/]")
                .Validate(t => !string.IsNullOrWhiteSpace(t)));

        ticker = ticker.Trim().ToUpper();

        var perf = await _signalRepo.GetTickerPerformanceAsync(ticker);
        var history = await _signalRepo.GetByTickerAsync(ticker);
        ConsoleRenderer.RenderTickerDetail(perf, history);
    }

    private static string NormalizeTicker(string ticker)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        return ticker.EndsWith(".KL", StringComparison.OrdinalIgnoreCase)
            ? ticker
            : ticker + ".KL";
    }

    private async Task ShowDataQuality()
    {
        var backtestFrom = DateTime.Today.AddDays(-_settings.DefaultLookbackDays);
        var fetchFrom = WarmupCalculator.FetchFrom(backtestFrom);
        var to = DateTime.Today;

        AnsiConsole.MarkupLine(
            "[dim]Checking bar counts in DB for fetch window " +
            $"{fetchFrom:dd MMM yyyy} -> {to:dd MMM yyyy}[/]\n");

        var table = new Table()
            .Title("[bold]Data Quality Check[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Ticker[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn(new TableColumn("[bold]Bars in DB[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Warmup Req.[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Usable Bars[/]").RightAligned())
            .AddColumn("[bold]Status[/]");

        var ok = 0;
        var warn = 0;
        var fail = 0;

        foreach (var (symbol, name, _) in _klseStocks)
        {
            var prices = await _fetcher.GetPricesAsync(symbol, fetchFrom, to);
            var bars = prices.Count;
            var (_, usable) = WarmupCalculator.Split(bars);

            string status;
            string statusColor;
            if (bars >= WarmupCalculator.WarmupBars)
            {
                status = "OK";
                statusColor = "green";
                ok++;
            }
            else if (bars >= 300)
            {
                status = "Partial";
                statusColor = "yellow";
                warn++;
            }
            else
            {
                status = "Insufficient";
                statusColor = "red";
                fail++;
            }

            table.AddRow(
                $"[cyan]{Markup.Escape(symbol)}[/]",
                Markup.Escape(name.Length > 25 ? name[..25] + "..." : name),
                bars.ToString(),
                WarmupCalculator.WarmupBars.ToString(),
                usable > 0 ? usable.ToString() : "[red]0[/]",
                $"[{statusColor}]{status}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"  [green]Good: {ok}[/]   [yellow]Partial: {warn}[/]   [red]Insufficient: {fail}[/]");

        if (fail > 0 || warn > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[yellow]Tip:[/] Run [bold]Populate DB[/] first to fetch full history. " +
                "Partial tickers will still produce signals but EMA200 may not be fully converged.");
        }

        AnsiConsole.WriteLine();
        var reqTable = new Table()
            .Title("[bold dim]Indicator Warmup Requirements[/]")
            .Border(TableBorder.Simple)
            .AddColumn("Indicator")
            .AddColumn(new TableColumn("Bars Needed").RightAligned())
            .AddColumn("Note");

        foreach (var (ind, bars, note) in WarmupCalculator.Requirements())
        {
            reqTable.AddRow(
                Markup.Escape(ind),
                bars.ToString(),
                $"[dim]{Markup.Escape(note)}[/]");
        }

        AnsiConsole.Write(reqTable);
    }

    private void ShowSettings()
    {
        AnsiConsole.Write(new Panel(
            $"MongoDB:            [yellow]{_settings.MongoConnectionString}[/]\n" +
            $"Database:           [yellow]{_settings.DatabaseName}[/]\n" +
            $"Backtest window:    [yellow]{_settings.DefaultLookbackDays} days[/]\n" +
            $"Fetch window:       [yellow]{_settings.DefaultLookbackDays + WarmupCalculator.WarmupCalendarDays} days[/] (includes warmup)\n" +
            $"Warmup bars:        [yellow]{WarmupCalculator.WarmupBars}[/] (EMA200 convergence)\n" +
            $"Signal filter:      [yellow]last {_settings.RecentSignalDays} days[/]\n" +
            $"KLSE tickers:       [yellow]{_klseStocks.Count} configured[/]\n" +
            $"Entry score min:    [yellow]>= {_engine.EntryMinScore}[/]\n" +
            $"Exit score min:     [yellow]>= {_engine.ExitMinScore}[/]\n" +
            $"Cooldown bars:      [yellow]{_engine.CooldownBars}[/]")
        {
            Header = new PanelHeader("[bold] Configuration [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1),
        });
        AnsiConsole.MarkupLine("[dim]Edit appsettings.json to change MongoDB/window settings and KLSE stock selection.[/]");
    }
}
