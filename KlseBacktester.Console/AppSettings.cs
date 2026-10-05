namespace KlseBacktester.Console;

using KlseBacktester.Models;

public class AppSettings
{
    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName          { get; set; } = "klse_backtest";
    public int    DefaultLookbackDays   { get; set; } = 730;   // 2 years
    public int    RecentSignalDays      { get; set; } = 5;
    public int    WarmupBars            { get; set; } = 50;    // min 50 (EMA50 seed); 600 for full EMA200 convergence
    public int    PopulateMonths        { get; set; } = 3;
    public int    ActivityLookbackDays  { get; set; } = 365;
    public int    ActiveStockCount      { get; set; } = 50;
    public List<StockDefinition> KlseStocks { get; set; } = new();
    public List<StockDefinition> KlseStockUniverse { get; set; } = new();
    public StrategySettings Strategy { get; set; } = new();
}

public class StrategySettings
{
    public int     EntryMinScore                 { get; set; } = 7;
    public int     ExitMinScore                  { get; set; } = 7;
    public int     CooldownBars                  { get; set; } = 5;
    public int     RequiredBreakoutConfirmations { get; set; } = 1;
    public bool    RequireTrendQualityForEntry   { get; set; } = true;
    public int     TrendSlopeLookback            { get; set; } = 5;
    public int     LongTrendSlopeLookback        { get; set; } = 20;
    public int     RequiredClosesAboveEma200     { get; set; } = 5;
    public bool    AllowSameBarBreakoutEntry     { get; set; } = true;
    public int     SameBarBreakoutMinScore       { get; set; } = 9;
    public bool    AllowSetupBarBreakout         { get; set; } = false;
    public decimal BreakoutCloseNearHighPercent  { get; set; } = 0.70m;
}
