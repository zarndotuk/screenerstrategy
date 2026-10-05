using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace KlseBacktester.Models;

public enum SignalType { Buy, Sell }

public class ScoreCall
{
    public DateTime Date { get; set; }
    public SignalType Type { get; set; }
    public decimal Price { get; set; }
    public int Score { get; set; }
    public bool IsActionableSignal { get; set; }
    public bool BuyConfirm { get; set; }
    public bool TrendQuality { get; set; }
    public bool SameBarBreakoutEntry { get; set; }

    public EntryScoreBreakdown? EntryBreakdown { get; set; }
    public ExitScoreBreakdown? ExitBreakdown { get; set; }
}

public class RetestConfirmation
{
    public DateTime Date { get; set; }
    public SignalType Type { get; set; }
    public decimal Price { get; set; }
    public int Score { get; set; }
    public DateTime BreakoutDate { get; set; }
    public decimal Level { get; set; }
    public int BreakoutCount { get; set; }
    public bool IsActionableSignal { get; set; }
}

public class ScoreEvaluation
{
    public List<Signal> Signals { get; set; } = new();
    public List<ScoreCall> Calls { get; set; } = new();
    public List<RetestConfirmation> Confirmations { get; set; } = new();
}

public class Signal
{
    public DateTime Date { get; set; }
    public SignalType Type { get; set; }
    public decimal Price { get; set; }
    public int Score { get; set; }
    public decimal StopLoss { get; set; }
    public int? ConfirmationScore { get; set; }
    public DateTime? ConfirmationBreakoutDate { get; set; }
    public decimal? ConfirmationLevel { get; set; }
    public int? ConfirmationBreakoutCount { get; set; }
    public decimal? TargetPrice { get; set; }
    public string? ExitReason { get; set; }

    // Breakdown for debugging — mirrors Pine script scores
    public EntryScoreBreakdown? EntryBreakdown { get; set; }
    public ExitScoreBreakdown? ExitBreakdown { get; set; }
}

public class EntryScoreBreakdown
{
    public bool CloseAboveEma20 { get; set; }
    public bool Ema20AboveEma50 { get; set; }
    public bool Ema50AboveEma200 { get; set; }
    public bool RsiBetween55And70 { get; set; }
    public bool VolumeAbove1_5x { get; set; }
    public bool VolumeAbove2x { get; set; }
    public bool BreakoutAboveRecentHigh { get; set; }    // worth 2
    public bool CandleBullish { get; set; }
    public bool MarketBullish { get; set; }
}

public class ExitScoreBreakdown
{
    public bool CloseBelowEma20 { get; set; }           // worth 2
    public bool Ema20BelowEma50 { get; set; }
    public bool RsiBelow55 { get; set; }
    public bool RsiBelow50 { get; set; }
    public bool BearishVolumeSpike { get; set; }        // worth 2
    public bool CloseBelowRecentLow { get; set; }       // worth 2
    public bool OverboughtExtension { get; set; }
}

public class Trade
{
    public DateTime EntryDate { get; set; }
    public decimal EntryPrice { get; set; }
    public int EntryScore { get; set; }

    public DateTime? ExitDate { get; set; }
    public decimal? ExitPrice { get; set; }
    public int ExitScore { get; set; }
    public string? ExitReason { get; set; }
    public decimal? TargetPrice { get; set; }

    public decimal StopLoss { get; set; }

    public decimal? PnlPercent =>
        ExitPrice.HasValue
            ? Math.Round((ExitPrice.Value - EntryPrice) / EntryPrice * 100, 2)
            : null;

    public bool? IsWin => PnlPercent.HasValue ? PnlPercent > 0 : null;
    public bool StoppedOut { get; set; }
}

public class BacktestResult
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? Id { get; set; }

    [BsonElement("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [BsonElement("run_date")]
    public DateTime RunDate { get; set; }

    [BsonElement("from_date")]
    public DateTime FromDate { get; set; }

    [BsonElement("to_date")]
    public DateTime ToDate { get; set; }

    [BsonElement("trades")]
    public List<Trade> Trades { get; set; } = new();

    [BsonElement("signals")]
    public List<Signal> Signals { get; set; } = new();

    [BsonElement("calls")]
    public List<ScoreCall> Calls { get; set; } = new();

    [BsonElement("confirmations")]
    public List<RetestConfirmation> Confirmations { get; set; } = new();

    // ── Summary stats ──
    [BsonElement("total_trades")]
    public int TotalTrades => Trades.Count(t => t.ExitDate.HasValue);

    [BsonElement("win_rate")]
    public double WinRate => TotalTrades == 0 ? 0
        : Math.Round((double)Trades.Count(t => t.IsWin == true) / TotalTrades * 100, 1);

    [BsonElement("total_return_pct")]
    public decimal TotalReturnPct =>
        Trades.Where(t => t.PnlPercent.HasValue).Sum(t => t.PnlPercent!.Value);

    [BsonElement("avg_pnl_pct")]
    public decimal AvgPnlPct => TotalTrades == 0 ? 0
        : Math.Round(TotalReturnPct / TotalTrades, 2);

    [BsonElement("max_drawdown_pct")]
    public decimal MaxDrawdownPct { get; set; }

    [BsonElement("open_trade")]
    public Trade? OpenTrade { get; set; }
}
