// EmaPullbackSim.cs - NinjaTrader 8 strategy, FORWARD TEST ON SIM ONLY.
// Rules = good_strategies.csv "ema_pullback [1/0.5 adxgt25]" as backtested in ts_backtest.py (2026):
//   Long : EMA21 > EMA50, EMA21 rising (>= prior bar), bar Low <= EMA21, Close > EMA21, ADX(14) >= 25
//   Short: EMA21 < EMA50, EMA21 falling (<= prior bar), bar High >= EMA21, Close < EMA21, ADX(14) >= 25
//   Stop 1.0 x ATR(14), target 0.5 x ATR(14), set as ticks from the fill. One position at a time.
//   Entries only on bars STARTING 09:40-15:30 ET. Flat at 15:55 ET. 3 MNQ. 5-minute chart, RTH template REQUIRED ("US Equities RTH").
//   Day guard: no new entries after realized day P&L <= -DayMaxLossUsd (default $1,100, under the $1,200 DLL).
// SAFETY: refuses to place any order unless the account name starts with "Sim" or "Playback".
//         Day P&L counts REAL-TIME trades only (the 2026-08-08 phantom-P&L lesson from StochReclaim30).
// Not compiled here - compile in NinjaScript Editor (F5). Backtest: 2026 net +$6,624, eval 79% pass / 0% bust.
#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class EmaPullbackSim : Strategy
    {
        private EMA ema21, ema50;
        private ADX adx;
        private ATR atr;
        private TimeZoneInfo et;
        private bool allowed;
        private int rtStartTrades = -1, tradesAtDayStart;
        private DateTime curDay = DateTime.MinValue;
        private double dayPnl;

        [NinjaScriptProperty, Range(1, 10), Display(Name = "Contracts", Order = 1, GroupName = "Parameters")]
        public int Contracts { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name = "ADX minimum", Order = 2, GroupName = "Parameters")]
        public double AdxMin { get; set; }
        [NinjaScriptProperty, Display(Name = "Stop x ATR", Order = 3, GroupName = "Parameters")]
        public double StopAtr { get; set; }
        [NinjaScriptProperty, Display(Name = "Target x ATR", Order = 4, GroupName = "Parameters")]
        public double TargetAtr { get; set; }
        [NinjaScriptProperty, Display(Name = "Day max loss USD (stop new entries)", Order = 5, GroupName = "Parameters")]
        public double DayMaxLossUsd { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EmaPullbackSim";
                Description = "EMA21 pullback + ADX>=25, 1.0/0.5 ATR bracket. SIM/PLAYBACK ACCOUNTS ONLY.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 60;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                Contracts = 3; AdxMin = 25; StopAtr = 1.0; TargetAtr = 0.5; DayMaxLossUsd = 1100;
            }
            else if (State == State.DataLoaded)
            {
                ema21 = EMA(Close, 21); ema50 = EMA(Close, 50); adx = ADX(14); atr = ATR(14);
                et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                string acct = Account != null ? Account.Name : "";
                allowed = acct.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                       || acct.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
                if (!allowed)
                    Print("EmaPullbackSim: account '" + acct + "' is not Sim/Playback - strategy will NOT trade.");
                // 2026-09-29: the edge exists ONLY when EMA/ATR/ADX are computed from regular-session bars.
                // On a 24h (ETH) chart the same rules lost -1.6 pts/trade in the 2026 backtest. Require an RTH template.
                string th = (Bars != null && Bars.TradingHours != null) ? Bars.TradingHours.Name : "";
                if (th.IndexOf("RTH", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    allowed = false;
                    Print("EmaPullbackSim: trading hours template '" + th + "' is not RTH - set the chart to 'US Equities RTH' (09:30-16:00 ET). Strategy will NOT trade.");
                }
            }
            else if (State == State.Realtime)
            {
                rtStartTrades = SystemPerformance.AllTrades.Count;      // ignore historical replay trades
                tradesAtDayStart = rtStartTrades; dayPnl = 0;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade || !allowed) return;
            DateTime closeEt = TimeZoneInfo.ConvertTime(Time[0], Core.Globals.GeneralOptions.TimeZoneInfo, et);
            DateTime startEt = closeEt.AddMinutes(-BarsPeriod.Value);   // NT stamps bars at CLOSE
            double hStart = startEt.Hour + startEt.Minute / 60.0;

            if (startEt.Date != curDay)
            {
                curDay = startEt.Date; dayPnl = 0;
                tradesAtDayStart = Math.Max(SystemPerformance.AllTrades.Count, rtStartTrades);
            }
            if (State == State.Realtime && rtStartTrades >= 0)
            {
                dayPnl = 0;
                for (int i = Math.Max(tradesAtDayStart, rtStartTrades); i < SystemPerformance.AllTrades.Count; i++)
                    dayPnl += SystemPerformance.AllTrades[i].ProfitCurrency;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (hStart >= 15 + 55 / 60.0 - 1e-9)
                {
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong(); else ExitShort();
                }
                return;
            }
            if (State != State.Realtime) return;                       // forward test only: no historical fills
            if (hStart < 9 + 40 / 60.0 - 1e-9 || hStart > 15.5 + 1e-9) return;
            if (dayPnl <= -DayMaxLossUsd) return;
            if (adx[0] < AdxMin || atr[0] <= 0) return;

            int stopTicks = Math.Max(1, (int)Math.Round(StopAtr * atr[0] / TickSize));
            int tgtTicks = Math.Max(1, (int)Math.Round(TargetAtr * atr[0] / TickSize));
            bool up = ema21[0] > ema50[0] && ema21[0] >= ema21[1];
            bool dn = ema21[0] < ema50[0] && ema21[0] <= ema21[1];

            if (up && Low[0] <= ema21[0] && Close[0] > ema21[0])
            {
                SetStopLoss("EPL", CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget("EPL", CalculationMode.Ticks, tgtTicks);
                EnterLong(Contracts, "EPL");
                Print(string.Format("{0:HH:mm} ET LONG {1} @~{2} stop {3}t target {4}t dayPnl {5:0}", startEt, Contracts, Close[0], stopTicks, tgtTicks, dayPnl));
            }
            else if (dn && High[0] >= ema21[0] && Close[0] < ema21[0])
            {
                SetStopLoss("EPS", CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget("EPS", CalculationMode.Ticks, tgtTicks);
                EnterShort(Contracts, "EPS");
                Print(string.Format("{0:HH:mm} ET SHORT {1} @~{2} stop {3}t target {4}t dayPnl {5:0}", startEt, Contracts, Close[0], stopTicks, tgtTicks, dayPnl));
            }
        }
    }
}
