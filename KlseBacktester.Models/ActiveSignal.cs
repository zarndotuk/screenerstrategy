using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace KlseBacktester.Models;

public enum SignalStatus
{
    /// <summary>Buy signal fired, not yet sold.</summary>
    Open,
    /// <summary>Sell signal fired — position closed.</summary>
    Closed,
}

/// <summary>
/// One record per stock trade lifecycle: buy → (sell).
/// Persisted in the "active_signals" collection.
/// The Next.js app reads this collection directly.
/// </summary>
public class ActiveSignal
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? Id { get; set; }

    [BsonElement("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("sector")]
    public string Sector { get; set; } = string.Empty;

    // ── Buy side ──
    [BsonElement("buy_date")]
    public DateTime BuyDate { get; set; }

    [BsonElement("buy_price")]
    public decimal BuyPrice { get; set; }

    [BsonElement("buy_score")]
    public int BuyScore { get; set; }

    [BsonElement("stop_loss")]
    public decimal StopLoss { get; set; }

    [BsonElement("target_price")]
    public decimal? TargetPrice { get; set; }

    // ── Sell side (null until closed) ──
    [BsonElement("sell_date")]
    public DateTime? SellDate { get; set; }

    [BsonElement("sell_price")]
    public decimal? SellPrice { get; set; }

    [BsonElement("sell_score")]
    public int? SellScore { get; set; }

    [BsonElement("exit_reason")]
    public string? ExitReason { get; set; }

    // ── Status ──
    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public SignalStatus Status { get; set; } = SignalStatus.Open;

    // ── Computed P&L (populated on close) ──
    [BsonElement("pnl_pct")]
    public decimal? PnlPct { get; set; }

    [BsonElement("pnl_abs")]
    public decimal? PnlAbs { get; set; }

    [BsonElement("hold_days")]
    public int? HoldDays { get; set; }

    // ── Meta ──
    [BsonElement("last_updated")]
    public DateTime LastUpdated { get; set; }

    // ── Convenience (not stored) ──
    [BsonIgnore]
    public bool IsWin => PnlPct.HasValue && PnlPct > 0;

    [BsonIgnore]
    public int DaysHeld => SellDate.HasValue
        ? (int)(SellDate.Value - BuyDate).TotalDays
        : (int)(DateTime.UtcNow - BuyDate).TotalDays;
}

/// <summary>
/// Aggregate performance stats per ticker, stored in "performance_stats".
/// Rebuilt on every backtest run.
/// </summary>
public class TickerPerformance
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? Id { get; set; }

    [BsonElement("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("sector")]
    public string Sector { get; set; } = string.Empty;

    [BsonElement("total_signals")]
    public int TotalSignals { get; set; }

    [BsonElement("closed_trades")]
    public int ClosedTrades { get; set; }

    [BsonElement("wins")]
    public int Wins { get; set; }

    [BsonElement("losses")]
    public int Losses { get; set; }

    [BsonElement("win_rate")]
    public double WinRate { get; set; }

    [BsonElement("total_return_pct")]
    public decimal TotalReturnPct { get; set; }

    [BsonElement("avg_return_pct")]
    public decimal AvgReturnPct { get; set; }

    [BsonElement("best_trade_pct")]
    public decimal BestTradePct { get; set; }

    [BsonElement("worst_trade_pct")]
    public decimal WorstTradePct { get; set; }

    [BsonElement("avg_hold_days")]
    public double AvgHoldDays { get; set; }

    [BsonElement("max_drawdown_pct")]
    public decimal MaxDrawdownPct { get; set; }

    [BsonElement("last_buy_date")]
    public DateTime? LastBuyDate { get; set; }

    [BsonElement("last_buy_score")]
    public int LastBuyScore { get; set; }

    [BsonElement("has_open_position")]
    public bool HasOpenPosition { get; set; }

    [BsonElement("last_updated")]
    public DateTime LastUpdated { get; set; }
}
