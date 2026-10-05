using KlseBacktester.Models;
using Spectre.Console;

namespace KlseBacktester.Console;

/// <summary>All rich-console rendering using Spectre.Console.</summary>
public static class ConsoleRenderer
{
    // ═══════════════════════════════════════════════════════════
    //  BUY SIGNALS (recent / open)
    // ═══════════════════════════════════════════════════════════

    public static void RenderOpenBuySignals(List<ActiveSignal> signals, int withinDays)
    {
        AnsiConsole.WriteLine();

        if (!signals.Any())
        {
            AnsiConsole.MarkupLine($"[yellow]No open buy signals in the last {withinDays} days.[/]");
            return;
        }

        var table = new Table()
            .Title($"[bold green]Open Buy Signals — last {withinDays} days[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Ticker[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Sector[/]")
            .AddColumn(new TableColumn("[bold]Buy Date[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Buy Price[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Score[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Stop Loss[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Days Open[/]").RightAligned());

        foreach (var s in signals.OrderByDescending(x => x.BuyDate))
        {
            string scoreColor = s.BuyScore >= 9 ? "green"
                              : s.BuyScore >= 7 ? "yellow"
                              : "red";

            table.AddRow(
                $"[bold cyan]{s.Ticker}[/]",
                Escape(s.Name),
                Escape(s.Sector),
                s.BuyDate.ToString("dd MMM yyyy"),
                $"[yellow]{s.BuyPrice:F3}[/]",
                $"[{scoreColor}]{s.BuyScore}[/]",
                $"[red]{s.StopLoss:F3}[/]",
                s.DaysHeld.ToString()
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]  {signals.Count} stock(s) with active buy signal[/]");
    }

    // ═══════════════════════════════════════════════════════════
    //  CLOSED TRADES
    // ═══════════════════════════════════════════════════════════

    public static void RenderClosedTrades(List<ActiveSignal> closed)
    {
        AnsiConsole.WriteLine();

        if (!closed.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No closed trades yet.[/]");
            return;
        }

        var table = new Table()
            .Title("[bold]Closed Trades[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Ticker[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn(new TableColumn("[bold]Buy Date[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Buy Price[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Sell Date[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Sell Price[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]P&L %[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Days[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Buy Score[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Sell Score[/]").RightAligned());

        foreach (var t in closed)
        {
            string pnlColor   = t.PnlPct >= 0 ? "green" : "red";
            string pnlDisplay = $"[{pnlColor}]{(t.PnlPct >= 0 ? "+" : "")}{t.PnlPct:F2}%[/]";

            table.AddRow(
                $"[bold cyan]{t.Ticker}[/]",
                Escape(t.Name),
                t.BuyDate.ToString("dd MMM yy"),
                $"{t.BuyPrice:F3}",
                t.SellDate?.ToString("dd MMM yy") ?? "-",
                t.SellPrice.HasValue ? $"{t.SellPrice:F3}" : "-",
                pnlDisplay,
                (t.HoldDays ?? 0).ToString(),
                t.BuyScore.ToString(),
                t.SellScore?.ToString() ?? "-"
            );
        }

        AnsiConsole.Write(table);

        // Summary row
        var wins     = closed.Count(t => t.IsWin);
        var losses   = closed.Count(t => !t.IsWin);
        var totalPnl = closed.Sum(t => t.PnlPct ?? 0);
        var winRate  = closed.Any() ? (double)wins / closed.Count * 100 : 0;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"  Trades: [bold]{closed.Count}[/]  " +
            $"Wins: [green]{wins}[/]  Losses: [red]{losses}[/]  " +
            $"Win Rate: [bold]{winRate:F1}%[/]  " +
            $"Total P&L: [{(totalPnl >= 0 ? "green" : "red")}]{(totalPnl >= 0 ? "+" : "")}{totalPnl:F2}%[/]");
    }

    // ═══════════════════════════════════════════════════════════
    //  PERFORMANCE LEADERBOARD
    // ═══════════════════════════════════════════════════════════

    public static void RenderPerformanceLeaderboard(List<TickerPerformance> stats)
    {
        AnsiConsole.WriteLine();

        if (!stats.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No performance data. Run a backtest first.[/]");
            return;
        }

        var table = new Table()
            .Title("[bold]System Performance Leaderboard[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]#[/]")
            .AddColumn("[bold]Ticker[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Sector[/]")
            .AddColumn(new TableColumn("[bold]Trades[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Win %[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Total Return[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Avg/Trade[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Best[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Worst[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Max DD[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Open?[/]").Centered());

        int rank = 1;
        foreach (var p in stats)
        {
            string winColor  = p.WinRate >= 60 ? "green" : p.WinRate >= 45 ? "yellow" : "red";
            string retColor  = p.TotalReturnPct >= 0 ? "green" : "red";
            string retSign   = p.TotalReturnPct >= 0 ? "+" : "";
            string avgSign   = p.AvgReturnPct   >= 0 ? "+" : "";

            table.AddRow(
                rank.ToString(),
                $"[bold cyan]{p.Ticker}[/]",
                Escape(p.Name),
                Escape(p.Sector),
                p.ClosedTrades.ToString(),
                $"[{winColor}]{p.WinRate:F1}%[/]",
                $"[{retColor}]{retSign}{p.TotalReturnPct:F2}%[/]",
                $"{avgSign}{p.AvgReturnPct:F2}%",
                $"[green]+{p.BestTradePct:F2}%[/]",
                $"[red]{p.WorstTradePct:F2}%[/]",
                $"[red]-{p.MaxDrawdownPct:F2}%[/]",
                p.HasOpenPosition ? "[green]●[/]" : "[dim]○[/]"
            );
            rank++;
        }

        AnsiConsole.Write(table);

        // System-wide summary
        var withTrades = stats.Where(s => s.ClosedTrades > 0).ToList();
        if (withTrades.Any())
        {
            AnsiConsole.WriteLine();
            double avgWinRate = withTrades.Average(s => s.WinRate);
            decimal avgReturn = withTrades.Average(s => s.AvgReturnPct);
            int openCount     = stats.Count(s => s.HasOpenPosition);

            AnsiConsole.MarkupLine(
                $"  Tickers tracked: [bold]{stats.Count}[/]  " +
                $"Avg win rate: [bold]{avgWinRate:F1}%[/]  " +
                $"Avg return/trade: [bold]{avgReturn:F2}%[/]  " +
                $"Open positions: [green]{openCount}[/]");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  BACKTEST PROGRESS
    // ═══════════════════════════════════════════════════════════

    public static async Task RunWithProgressAsync(
        string title,
        Func<Action<string, int, int>, Task> work)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[green]{title}[/]");
                await work((ticker, done, total) =>
                {
                    task.Description = $"[green]{title}[/] [dim]{ticker}[/]";
                    task.Value       = (double)done / total * 100;
                });
                task.Value = 100;
            });
    }

    // ═══════════════════════════════════════════════════════════
    //  SINGLE TICKER DETAIL
    // ═══════════════════════════════════════════════════════════

    public static void RenderTickerDetail(
        TickerPerformance? perf,
        List<ActiveSignal> history)
    {
        AnsiConsole.WriteLine();

        if (perf == null)
        {
            AnsiConsole.MarkupLine("[yellow]No performance data for this ticker.[/]");
            return;
        }

        // Stats panel
        var panel = new Panel(
            $"[bold]Win Rate:[/]    {perf.WinRate:F1}%\n" +
            $"[bold]Total Return:[/] {(perf.TotalReturnPct >= 0 ? "+" : "")}{perf.TotalReturnPct:F2}%\n" +
            $"[bold]Avg/Trade:[/]   {(perf.AvgReturnPct >= 0 ? "+" : "")}{perf.AvgReturnPct:F2}%\n" +
            $"[bold]Best Trade:[/]  +{perf.BestTradePct:F2}%\n" +
            $"[bold]Worst Trade:[/]  {perf.WorstTradePct:F2}%\n" +
            $"[bold]Max Drawdown:[/] -{perf.MaxDrawdownPct:F2}%\n" +
            $"[bold]Avg Hold:[/]    {perf.AvgHoldDays:F0} days\n" +
            $"[bold]Open Position:[/] {(perf.HasOpenPosition ? "[green]Yes[/]" : "[dim]No[/]")}")
        {
            Header = new PanelHeader($"[bold cyan] {perf.Ticker} — {Escape(perf.Name)} [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1),
        };
        AnsiConsole.Write(panel);

        // Trade history
        if (history.Any())
        {
            AnsiConsole.WriteLine();
            RenderClosedTrades(history.Where(h => h.Status == SignalStatus.Closed).ToList());

            var open = history.FirstOrDefault(h => h.Status == SignalStatus.Open);
            if (open != null)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"  [green]● Open position:[/] bought [yellow]{open.BuyPrice:F3}[/]" +
                    $" on {open.BuyDate:dd MMM yyyy} (score {open.BuyScore})" +
                    $"  Stop: [red]{open.StopLoss:F3}[/]  Days held: {open.DaysHeld}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════

    private static string Escape(string s) =>
        s.Replace("[", "[[").Replace("]", "]]");

    public static void Header(string text) =>
        AnsiConsole.Write(new FigletText(text).Color(Color.MediumPurple1));

    public static void Info(string msg)  => AnsiConsole.MarkupLine($"[dim]{msg}[/]");
    public static void Ok(string msg)    => AnsiConsole.MarkupLine($"[green]{msg}[/]");
    public static void Warn(string msg)  => AnsiConsole.MarkupLine($"[yellow]{msg}[/]");
    public static void Error(string msg) => AnsiConsole.MarkupLine($"[red]{msg}[/]");
}
