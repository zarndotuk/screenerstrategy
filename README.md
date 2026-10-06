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
| 1 | **Populate DB** | Fetches the last `PopulateMonths` (default 3) months of daily OHLCV from Yahoo Finance for the configured `KlseStocks` (no indicator warmup) |
| 2 | **Run Backtest** | Applies the score strategy to all tickers; persists buy/sell lifecycle to MongoDB |
| 3 | **Buy Signals** | Lists stocks with an **open (unsold) buy signal** within the last N days |
| 4 | **Performance** | Leaderboard ranked by total return across all closed trades |
| 5 | **Closed Trades** | All completed buy→sell round-trips with buy price, sell price, P&L % |
| 6 | **Ticker Detail** | Full history for a single stock |
| 7 | **Data Quality** | Bar counts & warmup status per ticker |
| 8 | **Settings** | View current config (edit `appsettings.json` to change) |
| 9 | **Screen Active** | Ranks `KlseStockUniverse` by average daily volume over `ActivityLookbackDays` and rewrites `KlseStocks` with the top `ActiveStockCount` |

### Command line

```bash
# Rank the universe, keep the most active stocks, then fetch 3 months of prices for them
dotnet run --project KlseBacktester.Console -- screen-active populate
```

`screen-active` (or `screen`) and `populate` can also be run on their own.

```bash
# Per-bar indicators and buy gates (setup, breakout, trend quality, confirm) for one stock
dotnet run --project KlseBacktester.Console -- diagnose 0225
```

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
  "RecentSignalDays": 5,
  "WarmupBars": 50,
  "PopulateMonths": 3,
  "ActivityLookbackDays": 365,
  "ActiveStockCount": 50,
  "KlseStocks": [],
  "KlseStockUniverse": [ { "Ticker": "8869.KL", "Name": "...", "Sector": "..." } ]
}
```

`RecentSignalDays` controls the default filter for the **Buy Signals** screen.

- `KlseStockUniverse` is the full candidate list used by **Screen Active**.
- `KlseStocks` is the working list used by Populate / Backtest. It is overwritten by **Screen Active**
  (in both the project's `appsettings.json` and the `bin/` copy). When empty, the universe is used.
- Populate stores only `PopulateMonths` of prices. Backtests refresh the same window (they don't fetch older history).
- `WarmupBars` (default 50, minimum 50) is how many stored bars are skipped before signals may fire.
  Until a stock has 200 bars, EMA50 stands in for EMA200 in the trend gate and the
  "EMA50 > EMA200" entry point scores 0 (max entry score 9 instead of 10).
  Set `WarmupBars` to 600 and raise `PopulateMonths` (~30) for full EMA200 convergence.
- `Strategy` holds the ScoreEngine parameters. Two are tuned for earlier entries:
  - `AllowSetupBarBreakout: true` lets a breakout on the same bar as the score setup confirm the buy (no 1-bar lag).
  - `BreakoutCloseNearHighPercent: 0.5` accepts breakout bars closing in the top half of their range (was 0.70).
- Flat zero-volume bars (Bursa holidays reported by Yahoo) are dropped before scoring.
- Prices are fetched incrementally: only sessions after the last stored bar (and older gaps) are requested.
  A session is stored only once it is final (after 18:00 MYT), so stored bars are never refetched.

---

## KLSE Ticker List

Edit `KlseBacktester.Models/KlseTickers.cs` to add/remove tickers.
All tickers use Yahoo Finance's `.KL` suffix (e.g. `1155.KL` = Maybank).
