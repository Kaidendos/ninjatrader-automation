// LucidPlan_2026-09-29.cs - NinjaTrader 8, FOUR strategies in one file (each appears separately in the Strategies list):
//   PlanEmaPullback   RTH 5-min chart  EMA21 pullback + ADX>=25, stop 1.0 ATR / target 0.5 ATR, 4 MNQ -> 5 at +$1,500
//   PlanVwapReclaim   RTH 5-min chart  LONG when low pierces VWAP-3sigma and closes back above; stop below bar low
//                                       (max 2 ATR, min 4 pts), target 2R, 2 MNQ
//   PlanNewsFade      ETH 5-min chart  5 min after each high-impact USD release, FADE the release bar; stop = 1x release-bar
//                                       range, target = 50% retrace of it, max 60 min, 3 MNQ
//   PlanLondonBreakout ETH 5-min chart close outside the 19:00-02:00 ET range between 02:00-05:00 ET -> follow it,
//                                       stop 2 ATR / target 2 ATR, one trade per night, flat by 08:25 ET, 2 MNQ
// Backtest basis: CANDIDATES_2026.md (2026, news-legal). Sizes from size_boot.py "MID" row.
//
// SAFETY (all four):
//   * NO orders unless the account name starts with "Sim" or "Playback" (project rule: never automate a funded/real account).
//   * Real-time only: nothing is traded on historical bars, so enabling never books phantom P&L.
//   * LucidDaily news rule: no new entries from 15 min before to 2 min after a high-impact USD event; any open position is
//     closed when an event is <= 7 min away (so flat >= 2 min before it). Calendar: ForexFactory weekly JSON, refreshed hourly.
//     If the calendar can't be loaded, the strategies DO NOT TRADE (fail closed).
//   * Daily guard: no new entries once the account's realized P&L today <= -DayStopUsd (default -$1,100, under the $1,200 DLL).
// Chart setup: PlanEmaPullback + PlanVwapReclaim need trading hours "US Equities RTH" (they refuse otherwise - the EMA edge
// only exists with RTH-only indicators). PlanNewsFade + PlanLondonBreakout need an ETH/24h template ("CME US Index Futures ETH").
// NOT COMPILED HERE - compile with F5 in the NinjaScript Editor and report any errors.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // ---------------------------------------------------------------- shared helpers
    public static class LucidPlanShared
    {
        private static readonly object Gate = new object();
        private static List<DateTime> events = new List<DateTime>();     // event times in US Eastern
        private static DateTime lastLoad = DateTime.MinValue;
        private static bool loadedOk = false;
        private static bool loading = false;
        public static readonly TimeZoneInfo Et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        public static DateTime ToEt(DateTime chartTime)
        {
            return TimeZoneInfo.ConvertTime(chartTime, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, Et);
        }

        public static bool IsSimAccount(Account a)
        {
            string n = a != null ? a.Name : "";
            return n.StartsWith("Sim", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
        }

        // refresh the calendar in the background at most once an hour
        public static void EnsureCalendar()
        {
            lock (Gate)
            {
                if (loading || (DateTime.UtcNow - lastLoad).TotalMinutes < 60) return;
                loading = true;
            }
            Task.Run(() =>
            {
                var list = new List<DateTime>(); bool ok = false;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    string json;
                    using (var wc = new WebClient()) json = wc.DownloadString("https://nfs.faireconomy.media/ff_calendar_thisweek.json");
                    foreach (Match m in Regex.Matches(json, "\\{[^{}]*\\}"))
                    {
                        string o = m.Value;
                        if (!Regex.IsMatch(o, "\"country\"\\s*:\\s*\"USD\"")) continue;
                        if (!Regex.IsMatch(o, "\"impact\"\\s*:\\s*\"High\"")) continue;
                        Match d = Regex.Match(o, "\"date\"\\s*:\\s*\"([^\"]+)\"");
                        DateTimeOffset dto;
                        if (d.Success && DateTimeOffset.TryParse(d.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
                            list.Add(TimeZoneInfo.ConvertTime(dto.UtcDateTime, TimeZoneInfo.Utc, Et));
                    }
                    ok = true;
                }
                catch (Exception) { ok = false; }
                lock (Gate)
                {
                    if (ok) { events = list; loadedOk = true; }
                    lastLoad = DateTime.UtcNow; loading = false;
                }
            });
        }

        public static bool CalendarOk { get { lock (Gate) return loadedOk; } }

        public static List<DateTime> Events { get { lock (Gate) return new List<DateTime>(events); } }

        // true if entries are blocked at time nowEt (T-15 .. T+2)
        public static bool EntryBlocked(DateTime nowEt)
        {
            foreach (var t in Events)
                if (nowEt >= t.AddMinutes(-15) && nowEt < t.AddMinutes(2)) return true;
            return false;
        }

        // true if an open position must be closed now (an event is 0..7 min away)
        public static bool MustBeFlat(DateTime nowEt)
        {
            foreach (var t in Events)
            {
                double m = (t - nowEt).TotalMinutes;
                if (m >= -1.0 && m <= 7.0) return true;
            }
            return false;
        }

        public static DateTime? NextEventAfter(DateTime nowEt)
        {
            DateTime? best = null;
            foreach (var t in Events)
                if (t > nowEt && t.Date == nowEt.Date && (best == null || t < best.Value)) best = t;
            return best;
        }

        public static double RealizedToday(Account a)
        {
            try { return a.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar); } catch { return 0; }
        }
    }

    // ---------------------------------------------------------------- 1) EMA pullback (RTH)
    public class PlanEmaPullback : Strategy
    {
        private EMA ema21, ema50; private ADX adx; private ATR atr; private bool allowed;

        [NinjaScriptProperty, Range(1, 20), Display(Name = "Contracts", Order = 1, GroupName = "Size")] public int Contracts { get; set; }
        [NinjaScriptProperty, Range(1, 20), Display(Name = "Contracts after step-up", Order = 2, GroupName = "Size")] public int StepUpContracts { get; set; }
        [NinjaScriptProperty, Display(Name = "Step up when eval profit >= $", Order = 3, GroupName = "Size")] public double StepUpAtProfit { get; set; }
        [NinjaScriptProperty, Display(Name = "Eval start balance (0 = no step-up)", Order = 4, GroupName = "Size")] public double EvalStartBalance { get; set; }
        [NinjaScriptProperty, Display(Name = "Day stop $ (account realized)", Order = 5, GroupName = "Risk")] public double DayStopUsd { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PlanEmaPullback"; Description = "EMA21 pullback + ADX>=25, 1.0/0.5 ATR. RTH chart. SIM ONLY.";
                Calculate = Calculate.OnBarClose; EntriesPerDirection = 1; EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true; ExitOnSessionCloseSeconds = 30; BarsRequiredToTrade = 60;
                Contracts = 4; StepUpContracts = 5; StepUpAtProfit = 1500; EvalStartBalance = 0; DayStopUsd = 1100;
            }
            else if (State == State.DataLoaded)
            {
                ema21 = EMA(Close, 21); ema50 = EMA(Close, 50); adx = ADX(14); atr = ATR(14);
                allowed = LucidPlanShared.IsSimAccount(Account);
                string th = Bars.TradingHours != null ? Bars.TradingHours.Name : "";
                if (th.IndexOf("RTH", StringComparison.OrdinalIgnoreCase) < 0) { allowed = false; Print(Name + ": needs an RTH trading-hours template (got '" + th + "'). NOT trading."); }
                if (!LucidPlanShared.IsSimAccount(Account)) Print(Name + ": account is not Sim/Playback. NOT trading.");
                LucidPlanShared.EnsureCalendar();
            }
        }

        private int Size()
        {
            if (EvalStartBalance <= 0) return Contracts;
            double profit = Account.Get(AccountItem.CashValue, Currency.UsDollar) - EvalStartBalance;
            return profit >= StepUpAtProfit ? StepUpContracts : Contracts;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade || !allowed || State != State.Realtime) return;
            LucidPlanShared.EnsureCalendar();
            DateTime closeEt = LucidPlanShared.ToEt(Time[0]); DateTime startEt = closeEt.AddMinutes(-BarsPeriod.Value);
            double h = startEt.Hour + startEt.Minute / 60.0;
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (h >= 15 + 55 / 60.0 - 1e-9 || LucidPlanShared.MustBeFlat(closeEt))
                { if (Position.MarketPosition == MarketPosition.Long) ExitLong(); else ExitShort(); }
                return;
            }
            if (!LucidPlanShared.CalendarOk || LucidPlanShared.EntryBlocked(closeEt) || LucidPlanShared.MustBeFlat(closeEt)) return;
            if (h < 9 + 40 / 60.0 - 1e-9 || h > 15.5 + 1e-9) return;
            if (LucidPlanShared.RealizedToday(Account) <= -DayStopUsd) return;
            if (adx[0] < 25 || atr[0] <= 0) return;
            int stopT = Math.Max(1, (int)Math.Round(1.0 * atr[0] / TickSize)), tgtT = Math.Max(1, (int)Math.Round(0.5 * atr[0] / TickSize));
            bool up = ema21[0] > ema50[0] && ema21[0] >= ema21[1], dn = ema21[0] < ema50[0] && ema21[0] <= ema21[1];
            if (up && Low[0] <= ema21[0] && Close[0] > ema21[0])
            { SetStopLoss("EPL", CalculationMode.Ticks, stopT, false); SetProfitTarget("EPL", CalculationMode.Ticks, tgtT); EnterLong(Size(), "EPL"); Print(string.Format("{0:HH:mm} ET EMA LONG {1}", startEt, Size())); }
            else if (dn && High[0] >= ema21[0] && Close[0] < ema21[0])
            { SetStopLoss("EPS", CalculationMode.Ticks, stopT, false); SetProfitTarget("EPS", CalculationMode.Ticks, tgtT); EnterShort(Size(), "EPS"); Print(string.Format("{0:HH:mm} ET EMA SHORT {1}", startEt, Size())); }
        }
    }

    // ---------------------------------------------------------------- 2) VWAP 3-sigma reclaim, long only (RTH)
    public class PlanVwapReclaim : Strategy
    {
        private ATR atr; private bool allowed; private double cv, ctpv, ctp2v;

        [NinjaScriptProperty, Range(1, 20), Display(Name = "Contracts", Order = 1, GroupName = "Size")] public int Contracts { get; set; }
        [NinjaScriptProperty, Display(Name = "Sigma", Order = 2, GroupName = "Parameters")] public double Sigma { get; set; }
        [NinjaScriptProperty, Display(Name = "Day stop $ (account realized)", Order = 3, GroupName = "Risk")] public double DayStopUsd { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PlanVwapReclaim"; Description = "Long VWAP-3sigma reclaim, stop below bar low (max 2 ATR), 2R. RTH chart. SIM ONLY.";
                Calculate = Calculate.OnBarClose; EntriesPerDirection = 1; EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true; ExitOnSessionCloseSeconds = 30; BarsRequiredToTrade = 30;
                Contracts = 2; Sigma = 3.0; DayStopUsd = 1100;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(14); allowed = LucidPlanShared.IsSimAccount(Account);
                string th = Bars.TradingHours != null ? Bars.TradingHours.Name : "";
                if (th.IndexOf("RTH", StringComparison.OrdinalIgnoreCase) < 0) { allowed = false; Print(Name + ": needs an RTH trading-hours template (got '" + th + "'). NOT trading."); }
                if (!LucidPlanShared.IsSimAccount(Account)) Print(Name + ": account is not Sim/Playback. NOT trading.");
                LucidPlanShared.EnsureCalendar();
            }
        }

        protected override void OnBarUpdate()
        {
            // session VWAP and volume-weighted sigma (computed on every bar, historical too, so it is warm in real time)
            if (Bars.IsFirstBarOfSession) { cv = 0; ctpv = 0; ctp2v = 0; }
            double tp = (High[0] + Low[0] + Close[0]) / 3.0, v = Math.Max(1.0, Volume[0]);
            cv += v; ctpv += tp * v; ctp2v += tp * tp * v;
            double vw = ctpv / cv, sd = Math.Sqrt(Math.Max(0.0, ctp2v / cv - vw * vw));
            if (CurrentBar < BarsRequiredToTrade || !allowed || State != State.Realtime) return;
            LucidPlanShared.EnsureCalendar();
            DateTime closeEt = LucidPlanShared.ToEt(Time[0]); DateTime startEt = closeEt.AddMinutes(-BarsPeriod.Value);
            double h = startEt.Hour + startEt.Minute / 60.0;
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (h >= 15 + 55 / 60.0 - 1e-9 || LucidPlanShared.MustBeFlat(closeEt)) ExitLong();
                return;
            }
            if (!LucidPlanShared.CalendarOk || LucidPlanShared.EntryBlocked(closeEt) || LucidPlanShared.MustBeFlat(closeEt)) return;
            if (h < 9 + 40 / 60.0 - 1e-9 || h > 15.5 + 1e-9) return;
            if (LucidPlanShared.RealizedToday(Account) <= -DayStopUsd) return;
            double band = vw - Sigma * sd;
            if (sd > 0 && atr[0] > 0 && Low[0] < band && Close[0] > band)
            {
                double stopPts = Math.Min(2.0 * atr[0], Math.Max(4.0, Close[0] - Low[0] + TickSize));
                int stopT = Math.Max(1, (int)Math.Round(stopPts / TickSize));
                SetStopLoss("VWL", CalculationMode.Ticks, stopT, false); SetProfitTarget("VWL", CalculationMode.Ticks, 2 * stopT);
                EnterLong(Contracts, "VWL"); Print(string.Format("{0:HH:mm} ET VWAP3 LONG {1} stop {2} pts", startEt, Contracts, stopPts));
            }
        }
    }

    // ---------------------------------------------------------------- 3) post-news fade (ETH chart, needs 08:30 bars)
    public class PlanNewsFade : Strategy
    {
        private bool allowed; private DateTime entryEt = DateTime.MinValue;

        [NinjaScriptProperty, Range(1, 20), Display(Name = "Contracts", Order = 1, GroupName = "Size")] public int Contracts { get; set; }
        [NinjaScriptProperty, Display(Name = "Max hold minutes", Order = 2, GroupName = "Parameters")] public int MaxHoldMin { get; set; }
        [NinjaScriptProperty, Display(Name = "Day stop $ (account realized)", Order = 3, GroupName = "Risk")] public double DayStopUsd { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PlanNewsFade"; Description = "Fade the 5-min release bar of high-impact USD news at T+5; 1x range stop, 50% retrace target. ETH chart. SIM ONLY.";
                Calculate = Calculate.OnBarClose; EntriesPerDirection = 1; EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true; ExitOnSessionCloseSeconds = 30; BarsRequiredToTrade = 5;
                Contracts = 3; MaxHoldMin = 60; DayStopUsd = 1100;
            }
            else if (State == State.DataLoaded)
            {
                allowed = LucidPlanShared.IsSimAccount(Account);
                string th = Bars.TradingHours != null ? Bars.TradingHours.Name : "";
                if (th.IndexOf("RTH", StringComparison.OrdinalIgnoreCase) >= 0) { allowed = false; Print(Name + ": needs a 24h/ETH template so 08:30 bars exist (got '" + th + "'). NOT trading."); }
                if (!LucidPlanShared.IsSimAccount(Account)) Print(Name + ": account is not Sim/Playback. NOT trading.");
                LucidPlanShared.EnsureCalendar();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade || !allowed || State != State.Realtime) return;
            LucidPlanShared.EnsureCalendar();
            DateTime closeEt = LucidPlanShared.ToEt(Time[0]); DateTime startEt = closeEt.AddMinutes(-BarsPeriod.Value);
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if ((closeEt - entryEt).TotalMinutes >= MaxHoldMin - 0.01 || LucidPlanShared.MustBeFlat(closeEt))
                { if (Position.MarketPosition == MarketPosition.Long) ExitLong(); else ExitShort(); }
                return;
            }
            if (!LucidPlanShared.CalendarOk || LucidPlanShared.RealizedToday(Account) <= -DayStopUsd) return;
            bool releaseBar = false;
            foreach (var t in LucidPlanShared.Events)
            {
                DateTime b = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute - t.Minute % 5, 0);
                if (b == startEt) { releaseBar = true; break; }
            }
            if (!releaseBar) return;
            DateTime? nxt = LucidPlanShared.NextEventAfter(startEt.AddMinutes(1));
            if (nxt.HasValue && closeEt >= nxt.Value.AddMinutes(-8)) return;               // not enough room before the next release
            if (LucidPlanShared.EntryBlocked(closeEt)) return;
            double move = Close[0] - Open[0], rng = High[0] - Low[0];
            if (move == 0) return;
            int side = move > 0 ? -1 : 1;
            double stopPts = Math.Max(4.0, rng), tgt = Open[0] + 0.5 * move;
            int stopT = Math.Max(1, (int)Math.Round(stopPts / TickSize));
            string sig = side > 0 ? "NFL" : "NFS";
            SetStopLoss(sig, CalculationMode.Ticks, stopT, false);
            if ((tgt - Close[0]) * side > TickSize) SetProfitTarget(sig, CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(tgt));
            else SetProfitTarget(sig, CalculationMode.Ticks, 100000);                          // no target: time exit only
            if (side > 0) EnterLong(Contracts, sig); else EnterShort(Contracts, sig);
            entryEt = closeEt;
            Print(string.Format("{0:HH:mm} ET NEWS FADE {1} {2}, release bar move {3:0.00}, stop {4:0.00} pts", closeEt, side > 0 ? "LONG" : "SHORT", Contracts, move, stopPts));
        }
    }

    // ---------------------------------------------------------------- 4) London breakout of the Asia range (ETH chart)
    public class PlanLondonBreakout : Strategy
    {
        private ATR atr; private bool allowed, tradedTonight; private double asiaHi = double.NaN, asiaLo = double.NaN;

        [NinjaScriptProperty, Range(1, 20), Display(Name = "Contracts", Order = 1, GroupName = "Size")] public int Contracts { get; set; }
        [NinjaScriptProperty, Display(Name = "Day stop $ (account realized)", Order = 2, GroupName = "Risk")] public double DayStopUsd { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PlanLondonBreakout"; Description = "Break of the 19:00-02:00 ET range between 02:00-05:00 ET, 2/2 ATR, flat 08:25 ET. ETH chart. SIM ONLY.";
                Calculate = Calculate.OnBarClose; EntriesPerDirection = 1; EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true; ExitOnSessionCloseSeconds = 30; BarsRequiredToTrade = 30;
                Contracts = 2; DayStopUsd = 1100;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(14); allowed = LucidPlanShared.IsSimAccount(Account);
                string th = Bars.TradingHours != null ? Bars.TradingHours.Name : "";
                if (th.IndexOf("RTH", StringComparison.OrdinalIgnoreCase) >= 0) { allowed = false; Print(Name + ": needs a 24h/ETH template (got '" + th + "'). NOT trading."); }
                if (!LucidPlanShared.IsSimAccount(Account)) Print(Name + ": account is not Sim/Playback. NOT trading.");
                LucidPlanShared.EnsureCalendar();
            }
        }

        protected override void OnBarUpdate()
        {
            DateTime closeEt = LucidPlanShared.ToEt(Time[0]); DateTime startEt = closeEt.AddMinutes(-BarsPeriod.Value);
            int m = startEt.Hour * 60 + startEt.Minute;
            if (Bars.IsFirstBarOfSession) { asiaHi = double.NaN; asiaLo = double.NaN; tradedTonight = false; }
            if (m >= 19 * 60 || m < 2 * 60)                                                     // Asia range 19:00-02:00 ET
            {
                asiaHi = double.IsNaN(asiaHi) ? High[0] : Math.Max(asiaHi, High[0]);
                asiaLo = double.IsNaN(asiaLo) ? Low[0] : Math.Min(asiaLo, Low[0]);
            }
            if (CurrentBar < BarsRequiredToTrade || !allowed || State != State.Realtime) return;
            LucidPlanShared.EnsureCalendar();
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if ((m >= 8 * 60 + 20 && m < 18 * 60) || LucidPlanShared.MustBeFlat(closeEt))
                { if (Position.MarketPosition == MarketPosition.Long) ExitLong(); else ExitShort(); }
                return;
            }
            if (tradedTonight || double.IsNaN(asiaHi) || !LucidPlanShared.CalendarOk) return;
            if (m < 2 * 60 || m > 5 * 60) return;                                               // entries 02:00-05:00 ET
            if (LucidPlanShared.EntryBlocked(closeEt) || LucidPlanShared.MustBeFlat(closeEt)) return;
            if (LucidPlanShared.RealizedToday(Account) <= -DayStopUsd || atr[0] <= 0) return;
            int t2 = Math.Max(1, (int)Math.Round(2.0 * atr[0] / TickSize));
            if (Close[0] > asiaHi && Close[1] <= asiaHi)
            { SetStopLoss("LBL", CalculationMode.Ticks, t2, false); SetProfitTarget("LBL", CalculationMode.Ticks, t2); EnterLong(Contracts, "LBL"); tradedTonight = true; Print(string.Format("{0:HH:mm} ET LONDON LONG {1}", closeEt, Contracts)); }
            else if (Close[0] < asiaLo && Close[1] >= asiaLo)
            { SetStopLoss("LBS", CalculationMode.Ticks, t2, false); SetProfitTarget("LBS", CalculationMode.Ticks, t2); EnterShort(Contracts, "LBS"); tradedTonight = true; Print(string.Format("{0:HH:mm} ET LONDON SHORT {1}", closeEt, Contracts)); }
        }
    }
}
