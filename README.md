# KLSE Backtest System

Score-based entry/exit strategy backtester for Bursa Malaysia stocks.
C# port of a Pine Script (TradingView) indicator — entry/exit score ≥ 7.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- MongoDB running locally on port 27017 (or update `appsettings.json`)

Quick MongoDB via Docker:
```bash
docker run -d --name mongo -p 27017:27017 mongo:7
```

---

## Setup

```bash
cd KlseBacktester
dotnet restore
dotnet build
dotnet run --project KlseBacktester.Console
```

---

## Menu Options

| # | Option | Description |
|---|--------|-------------|
| 1 | **Populate DB** | Fetches 2 years of daily OHLCV from Yahoo Finance for all KLSE tickers + SPY |
| 2 | **Run Backtest** | Applies the score strategy to all tickers; persists buy/sell lifecycle to MongoDB |
| 3 | **Buy Signals** | Lists stocks with an **open (unsold) buy signal** within the last N days |
| 4 | **Performance** | Leaderboard ranked by total return across all closed trades |
| 5 | **Closed Trades** | All completed buy→sell round-trips with buy price, sell price, P&L % |
| 6 | **Ticker Detail** | Full history for a single stock |
| 7 | **Settings** | View current config (edit `appsettings.json` to change) |

---

## MongoDB Collections

Designed so the **Next.js app** can query these directly:

| Collection | Description |
|---|---|
| `prices` | Daily OHLCV per ticker — indexed on `(ticker, date)` |
| `stocks` | Ticker metadata (name, sector, last_updated) |
| `active_signals` | **Buy/sell lifecycle** — one doc per trade. `status: "Open"` = in position, `status: "Closed"` = exited with P&L |
| `performance_stats` | Aggregate stats per ticker: win rate, total return, avg hold days, max drawdown |
| `backtest_results` | Raw signal/trade arrays per backtest run (audit trail) |

### `active_signals` document shape (for Next.js)
```json
{
  "ticker": "1155.KL",
  "name": "Malayan Banking (Maybank)",
  "sector": "Finance",
  "buy_date": "2024-03-15T00:00:00Z",
  "buy_price": 9.35,
  "buy_score": 8,
  "stop_loss": 9.01,
  "sell_date": "2024-04-02T00:00:00Z",
  "sell_price": 9.82,
  "sell_score": 7,
  "status": "Closed",
  "pnl_pct": 5.03,
  "pnl_abs": 0.47,
  "hold_days": 18,
  "last_updated": "2024-06-01T08:00:00Z"
}
```

---

## Entry Score Logic (Pine Script → C#)

| Condition | Points |
|---|---|
| Close > EMA20 | +1 |
| EMA20 > EMA50 | +1 |
| EMA50 > EMA200 | +1 |
| RSI between 55–70 | +1 |
| Volume > 1.5× avg | +1 |
| Volume > 2× avg | +1 |
| **Close breaks above 20-bar high** | **+2** |
| Bullish candle (close > open) | +1 |
| Market bullish (SPY > EMA50) | +1 |
| **Minimum to trigger BUY** | **≥ 7** |

## Exit Score Logic

| Condition | Points |
|---|---|
| **Close < EMA20** | **+2** |
| EMA20 < EMA50 | +1 |
| RSI < 55 | +1 |
| RSI < 50 | +1 |
| **Bearish volume spike** (vol > 1.5× avg AND close < open) | **+2** |
| **Close breaks below 10-bar low** | **+2** |
| Overbought extension (close − EMA20 > ATR × 3) | +1 |
| **Minimum to trigger SELL** | **≥ 7** |

---

## Configuration (`appsettings.json`)

```json
{
  "MongoConnectionString": "mongodb://localhost:27017",
  "DatabaseName": "klse_backtest",
  "DefaultLookbackDays": 730,
  "RecentSignalDays": 5
}
```

`RecentSignalDays` controls the default filter for the **Buy Signals** screen.

---

## KLSE Ticker List

Edit `KlseBacktester.Models/KlseTickers.cs` to add/remove tickers.
All tickers use Yahoo Finance's `.KL` suffix (e.g. `1155.KL` = Maybank).
