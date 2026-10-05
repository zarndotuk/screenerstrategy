namespace KlseBacktester.Console;

using KlseBacktester.Models;

public class AppSettings
{
    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName          { get; set; } = "klse_backtest";
    public int    DefaultLookbackDays   { get; set; } = 730;   // 2 years
    public int    RecentSignalDays      { get; set; } = 5;
    public List<StockDefinition> KlseStocks { get; set; } = new();
}
