# StochReclaim30 — NinjaTrader strategy backups

Durable snapshots of the NinjaScript strategy, kept here (outside temp/OneDrive-CFA)
so they survive scratchpad cleanup.

Live file location:
`C:\Users\YOU\OneDrive\Documents\NinjaTrader 8\bin\Custom\Strategies\StochReclaim30.cs`

## Versions

| File | Date | Hash | What it is |
|------|------|------|-----------|
| `StochReclaim30_2026-07-15_pre-session_v3-watchdog_2F4842DD.cs` | 2026-07-15 | `2F4842DD` | **The version running BEFORE the 2026-07-20 changes.** News guard + feed watchdog + TestFireOnce. NO Discord trade pings, NO account profit target. This is the clean rollback point. |
| `StochReclaim30_2026-07-20_current_v3.3_FD597BAA.cs` | 2026-07-20 | `FD597BAA` | Superseded. v3.2 entry/exit Discord pings + v3.3 account profit target ($1,250 eval stop, real-time-only). |
| `StochReclaim30_2026-07-22_funded_stop-when-green_43D8DD16.cs` | 2026-07-22 | `43D8DD16` | Superseded same day (budget was 500). `StopWhenGreen` (renamed from StopAfterWin): locks the day once dayPnl>0 (net green), not on any win. |
| `StochReclaim30_2026-07-22_funded_budget300_159D4485.cs` | 2026-07-22 | `159D4485` | Was live until 2026-08-08. Same as 43D8DD16 but DayMaxLossUsd lowered 500→300 (~5 ct, red day ~−$300 ≈ 30% of the $1k DD). Defaults: DayMaxProfitUsd 440, DayMaxLossUsd 300, MaxContracts 10, StopWhenGreen true, AccountTargetUsd 0, AccountBankedUsd 0. **Carries the phantom-P&L bug — do not redeploy.** |
| `StochReclaim30_2026-08-08_pnl-fix.cs` | 2026-08-08 | `16701BC1` | **P&L accounting fix. Not yet deployed.** Identical parameters to `159D4485`; logic changes only. Fixes `dayPnl` counting NinjaTrader's *simulated* historical-replay fills as real profit. |

## The 2026-08-08 P&L fix — what it repairs

`SystemPerformance.AllTrades` holds both real fills and the simulated ones NT books
while replaying chart history on strategy enable. `realtimeStartTradeIndex` existed to
separate them but was used **only** by `CurrentAccountProfit()`. `dayPnl` summed from
`tradeCountAtDayStart`, which is set on `Bars.IsFirstBarOfSession` — and that fires
during the historical replay too, so `dayPnl` included phantom trades.

Observed on **2026-07-16**: diagnostic logged `dayPnl 800 / dayTrades 2` while the
broker database recorded **zero executions that day**.

This was never merely cosmetic. `dayPnl` feeds position sizing
(`budget = DayMaxLossUsd + dayPnl`, `room = DayMaxProfitUsd - dayPnl`) and
`StopWhenGreen`. A phantom +$800 exceeds the $440 daily cap, drives `n < 1`, and
**locks the bot out of a real trading session** — which is consistent with 7/16
showing no broker trades at all.

Three changes:
1. On `State.Realtime`, reset `dayPnl`/`dayTrades`/`dayWon` and set
   `tradeCountAtDayStart = realtimeStartTradeIndex`, discarding replay state.
2. The realized-P&L loop starts at `Math.Max(tradeCountAtDayStart, realtimeStartTradeIndex)`,
   covering both going live mid-session and crossing a session boundary while live.
3. Diagnostic now writes **`stochreclaim_diag_v2.csv`** (new name — the added columns
   would misalign the old 9-column header) with `rtTrades` and `rtPnl`, which count
   real-time trades only and are directly comparable to the broker statement.

Verified: braces/parens balanced, only 7 original lines replaced, no behavioural
parameter changed. **Not compiled** — NinjaTrader is the only place that can do that.

## To roll back to the pre-session version

1. Copy `StochReclaim30_2026-07-15_pre-session_v3-watchdog_2F4842DD.cs` over the live
   file (rename it to `StochReclaim30.cs`). The Strategies folder is protected by
   Windows Defender Controlled Folder Access, so copy it in **File Explorer**
   (which CFA trusts) or via the elevated AuditMode deploy script.
2. In NinjaTrader: NinjaScript Editor -> **F5** to recompile.
3. **Re-enable** the strategy on its chart (F5 disables running strategies).

Verify the copy landed by comparing SHA-256 to the hash in the table above.

| `EmaPullbackSim_2026-09-28.cs` | 2026-09-28 | - | EMA21 pullback + ADX>=25, 1.0/0.5 ATR, 3 MNQ. **Forward test on Sim only - refuses non-Sim/Playback accounts.** Not compiled yet. |

| `LucidPlan_2026-09-29.cs` | 2026-09-29 | - | 4 strategies in one file: PlanEmaPullback (RTH, 4->5 MNQ), PlanVwapReclaim (RTH, 2), PlanNewsFade (ETH, 3), PlanLondonBreakout (ETH, 2). **Sim/Playback only**, real-time only, ForexFactory news-flat rule (fails closed), account day stop -$1,100. Not compiled yet. |
