// ═══════════════════════════════════════════════════════════════════════════
// StochReclaim30 — NinjaTrader 8 port of the Claude 2 live MNQ strategy
// (C:\trading_system\MNQ-Signal-System, stoch_rev-th30 long, vol-sized).
//
// Rule-for-rule mapping from the Python engine (claude2_app.py):
//   * BUY when raw Stochastic %K(14) crosses UP through 30
//       - raw %K computed manually: 100*(Close-LL14)/(HH14-LL14).
//         (NT's built-in Stochastics indicator is smoothed — do NOT use it.)
//   * Long only · stop 0.75*ATR(14) · target 0.5*ATR(14) (Wilder ATR = NT ATR)
//   * Entries on flow-positive bars only: bar closes in its UPPER half
//   * Morning only: bot gates on bar-OPEN time 09:40-11:30 ET = bar-CLOSE
//     09:45-11:35 ET (same bars; NT stamps bars with close time)
//   * Size fitted per trade: a full stop must fit the remaining -$500 day
//     budget and a full target may not bust the +$1,500 day cap; max 10 MNQ
//   * Day locks after 6 completed trades (sizing already locks at the caps)
//   * Flat at 16:00 ET (session close exit + explicit guard)
//   * NEWS GUARD (v2): same ForexFactory feed as the bot. High-impact USD
//     events -> no entries 30 min before to 10 min after; if the event hit
//     PRE-OPEN (07:00-09:30 ET, e.g. CPI/PPI/NFP 08:30), no entries before
//     10:30 ET while the open digests the burst. Real-time only — historical
//     backtests have no calendar (matches the Python replay). Fetch failures
//     fail OPEN (guard idle), same as the bot.
//
// v3 (2026-07-15): FEED WATCHDOG. The Tradovate/Lucid data subscription died
// silently 11:00-11:24 ET while the connection heartbeat stayed alive — no
// disconnect was logged and the day's only signal was missed. v3 watches the
// tick stream during RTH: >90s of silence -> NT Alert + Discord ping (webhook
// read from the bot's .env); late/backfilled bars -> alert; every real-time
// RTH bar appended to Documents\NinjaTrader 8\stochreclaim_diag.csv.
//
// v3.3 (2026-07-20): ACCOUNT PROFIT TARGET. Stops trading for good once total
// account profit reaches AccountTargetUsd (Lucid 25K eval goal = $1,250). Total
// profit = AccountBankedUsd (profit already in the account before this run) +
// realized P&L of every trade since the strategy was enabled. On reaching the
// goal it latches OFF (no new entries this run) and pings Discord. Set the two
// params on the strategy: "Account profit target ($)" and "Profit already
// banked at start ($)". 0 target = feature off. NOTE: F5-recompile + re-enable
// resets the trade counter, so "banked at start" must equal the account's
// current profit at the moment you enable it.
//
// v3.2 (2026-07-20): TRADE PINGS. Every real-time entry and exit posts to the
// same Discord webhook (bot .env). This replaces the Topstep/ProjectX trade
// pings that went dark when that API subscription lapsed — NT trades through
// its own broker connection, so its webhook is unaffected. Entry ping: size,
// fill price, stop/target. Exit ping: fill, per-trade P&L, running day P&L and
// trade count. The Discord POST is now UTF-8 so the status emoji render.
//
// TIME MODEL (v2): all session times are counted in BARS SINCE THE 09:30 ET
// SESSION OPEN, so every time parameter below is EASTERN TIME regardless of
// the PC / NinjaTrader timezone setting. Requires the chart setup below.
//
// SETUP (important — signals depend on it):
//   1. Chart: MNQ (current contract), 5-minute bars.
//   2. Chart Trading hours template: "US Equities RTH" (09:30-16:00 ET).
//      The Python engine computes ATR/%K on RTH bars only — an ETH template
//      feeds overnight bars into the indicators and changes the signals.
//   3. Calculate must stay "On bar close".
//
// Fill-model note: the Python replay enters AT the signal bar's close; a NT
// backtest fills the market order at the NEXT bar's open (~1 tick of drift).
// Live/real-time behavior is identical to the bot (market order the moment
// the bar closes).
// ═══════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class StochReclaim30 : Strategy
	{
		private const string NEWS_URL = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
		private const int SESSION_OPEN_MIN = 9 * 60 + 30;   // 09:30 ET, start of RTH

		private ATR atr;
		private MIN lowMin;
		private MAX highMax;
		private Series<double> kSeries;

		private double dayPnl;              // realized P&L today (model, $)
		private int dayTrades;              // completed trades today
		private int tradeCountAtDayStart;   // SystemPerformance index at session open
		private bool dayWon;                // only used when StopAfterWin = true

		// news guard state (ET wall times of high-impact USD events this week)
		private readonly object newsLock = new object();
		private List<DateTime> newsEventsEt;
		private DateTime newsFetchedUtc = DateTime.MinValue;
		private bool newsFetchBusy;
		private TimeZoneInfo easternTz;

		// feed watchdog (v3): a Tradovate data subscription can die silently while
		// the connection heartbeat stays alive (2026-07-15: no MNQ ticks 11:00-11:24
		// ET, no disconnect logged, missed the day's only signal). Detect tick
		// silence during RTH and scream: NT Alert + Discord + diag CSV.
		private const string ENV_PATH = @"C:\trading_system\MNQ-Signal-System\.env";
		private const int STALL_SECS = 90;
		private DateTime lastTickUtc = DateTime.MinValue;
		private DateTime lastLateAlertUtc = DateTime.MinValue;
		private System.Threading.Timer feedTimer;
		private volatile bool stallAlerted;
		private string discordUrl;
		private bool testFired;             // TEST fire latch — one shot per enable

		// trade pings (v3.2)
		private int lastSlTicks;            // stop/target of the working entry, for the ping
		private int lastTpTicks;
		private bool inPosition;            // true between entry fill and going flat

		// account profit target (v3.3)
		private bool targetReached;          // latched once the eval goal is banked
		private int realtimeStartTradeIndex; // AllTrades count when live began (skip historical)

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Stoch %K(14) reclaim of 30, long only, vol-sized, FF news guard — port of the Claude 2 live MNQ bot.";
				Name = "StochReclaim30";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				BarsRequiredToTrade = 20;
				IsInstantiatedOnEachOptimizationIteration = true;

				// ── strategy parameters (defaults = live config.py) ──
				StochPeriod = 14;
				StochThreshold = 30;
				AtrPeriod = 14;
				SlAtr = 0.75;
				TpAtr = 0.5;
				MaxContracts = 10;
				DayMaxLossUsd = 500;
				DayMaxProfitUsd = 1500;   // 0 = cap off (config: None after funding)
				DayMaxTrades = 6;
				EntryStartEt = 94500;     // ET bar-CLOSE times, HHmmss (see header)
				EntryEndEt = 113500;
				FlatTimeEt = 160000;
				StopAfterWin = false;     // config.STOP_AFTER_WIN (True after funding)
				TestFireOnce = false;     // TEST: one 1-lot bracket entry, Sim accounts only
				UseNewsGuard = true;      // ForexFactory high-impact USD guard
				NewsPreMin = 30;          // config.NEWS_WINDOW
				NewsPostMin = 10;         // config.NEWS_WINDOW_POST
				PreOpenSettleEt = 103000; // config.NEWS_PREOPEN_SETTLE_HR (10:30 ET)

				// account profit target (Lucid 25K eval): stop at +$1,250 total.
				// Banked = profit already in the account at enable time (user: $989
				// on 2026-07-20). 0 target disables the feature.
				AccountTargetUsd = 1250;
				AccountBankedUsd = 989;
			}
			else if (State == State.DataLoaded)
			{
				atr = ATR(AtrPeriod);
				lowMin = MIN(Low, StochPeriod);
				highMax = MAX(High, StochPeriod);
				kSeries = new Series<double>(this);
				try
				{
					easternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				}
				catch (Exception)
				{
					easternTz = null;   // news guard will stay idle
				}
				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute)
					Print(Name + ": WARNING — designed for 5-minute bars; session-time math assumes minute bars.");
			}
			else if (State == State.Realtime)
			{
				RefreshNewsAsync();     // load this week's calendar once live
				// account-target counts only live trades — snapshot the historical count
				realtimeStartTradeIndex = SystemPerformance.AllTrades.Count;
				try
				{
					if (System.IO.File.Exists(ENV_PATH))
						foreach (string line in System.IO.File.ReadAllLines(ENV_PATH))
							if (line.StartsWith("DISCORD_WEBHOOK="))
								discordUrl = line.Substring("DISCORD_WEBHOOK=".Length).Trim().Trim('"');
				}
				catch (Exception) { discordUrl = null; }
				lastTickUtc = DateTime.UtcNow;
				if (feedTimer == null)
					feedTimer = new System.Threading.Timer(CheckFeed, null, 30000, 30000);
			}
			else if (State == State.Terminated)
			{
				if (feedTimer != null)
				{
					feedTimer.Dispose();
					feedTimer = null;
				}
			}
		}

		// ── feed watchdog ──

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last)
				return;
			lastTickUtc = DateTime.UtcNow;
			if (stallAlerted)
			{
				stallAlerted = false;
				Notify("MNQ feed RECOVERED — ticks flowing again. Gap bars backfill as "
					   + "historical and can NOT fire live orders; next live bar trades normally.");
			}
		}

		private void CheckFeed(object unused)
		{
			try
			{
				if (State != State.Realtime || easternTz == null || lastTickUtc == DateTime.MinValue)
					return;
				DateTime et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternTz);
				if (et.DayOfWeek == DayOfWeek.Saturday || et.DayOfWeek == DayOfWeek.Sunday)
					return;
				double hr = et.Hour + et.Minute / 60.0;
				if (hr < 9.5 || hr >= 16.0)
					return;                     // watch RTH only — that's when we trade
				double silent = (DateTime.UtcNow - lastTickUtc).TotalSeconds;
				if (silent > STALL_SECS && !stallAlerted)
				{
					stallAlerted = true;
					Notify(string.Format("MNQ DATA FEED STALLED — no ticks for {0:F0}s during RTH. "
										 + "Strategy is BLIND. Fix now: Control Center > Connections > "
										 + "disconnect + reconnect the Lucid/Simulation connection.", silent));
				}
			}
			catch (Exception) { }
		}

		private void WatchBarLag()
		{
			double lag = (DateTime.UtcNow - Time[0].ToUniversalTime()).TotalSeconds;
			if (lag > 120 && lag < 86400
					&& (DateTime.UtcNow - lastLateAlertUtc).TotalSeconds > 180)
			{
				lastLateAlertUtc = DateTime.UtcNow;
				Notify(string.Format("bar {0:HH:mm} arrived {1:F0}s late — stall + backfill; "
									 + "live signals inside the gap were missed.", Time[0], lag));
			}
		}

		private void Notify(string msg)
		{
			Print(Name + ": " + msg);
			try
			{
				Alert("feedWatch" + DateTime.UtcNow.Ticks, Priority.High, msg,
					  NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert1.wav", 10,
					  System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.Yellow);
			}
			catch (Exception) { }
			PostDiscord("**NT StochReclaim30**: " + msg);
		}

		// Trade fill ping — Discord + Output only. Unlike the feed watchdog these
		// are routine, so they skip the loud NT Alert popup/sound.
		private void PostTrade(string msg)
		{
			Print(Name + ": " + msg);
			PostDiscord("**NT StochReclaim30** · " + msg);
		}

		// Fire-and-forget POST of a Discord message. Encoding is forced to UTF-8
		// because WebClient defaults to system-ANSI, which mangles the status
		// emoji/symbols in the trade pings (and could break the JSON body).
		private void PostDiscord(string content)
		{
			if (string.IsNullOrEmpty(discordUrl))
				return;
			string url = discordUrl;
			string safe = content.Replace("\\", "\\\\").Replace("\"", "\\\"")
								  .Replace("\r", " ").Replace("\n", " ");
			Task.Run(new Action(delegate
			{
				try
				{
					System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
					using (var wc = new System.Net.WebClient())
					{
						wc.Encoding = System.Text.Encoding.UTF8;
						wc.Headers.Add("Content-Type", "application/json; charset=utf-8");
						wc.UploadString(url, "{\"content\": \"" + safe + "\"}");
					}
				}
				catch (Exception) { }
			}));
		}

		private void Diag(int sessMin)
		{
			try
			{
				string path = NinjaTrader.Core.Globals.UserDataDir + "stochreclaim_diag.csv";
				if (!System.IO.File.Exists(path))
					System.IO.File.AppendAllText(path,
						"proc_utc,bar_et,close,k,atr,sessMin,pos,dayPnl,dayTrades\r\n");
				System.IO.File.AppendAllText(path, string.Format(
					"{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm},{2:F2},{3:F1},{4:F2},{5},{6},{7:F0},{8}\r\n",
					DateTime.UtcNow, Time[0], Close[0], kSeries[0], atr[0], sessMin,
					Position.MarketPosition, dayPnl, dayTrades));
			}
			catch (Exception) { }
		}

		// ── ET session-time helpers (timezone-proof: bar count, not wall clock) ──

		private static int EtToSessionMin(int hhmmss)
		{
			// HHmmss ET -> minutes after the 09:30 ET open
			return (hhmmss / 10000) * 60 + (hhmmss / 100) % 100 - SESSION_OPEN_MIN;
		}

		private int BarCloseSessionMin()
		{
			// bar N of the session closes (N+1)*period minutes after 09:30 ET
			return (Bars.BarsSinceNewTradingDay + 1) * BarsPeriod.Value;
		}

		// ── news guard ──

		private void RefreshNewsAsync()
		{
			lock (newsLock)
			{
				if (newsFetchBusy)
					return;
				newsFetchBusy = true;
			}
			Task.Run(new Action(FetchNews));
		}

		private void FetchNews()
		{
			try
			{
				if (easternTz == null)
					return;
				System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
				using (var wc = new System.Net.WebClient())
				{
					wc.Headers.Add("User-Agent", "StochReclaim30-NT8/1.0");
					string json = wc.DownloadString(NEWS_URL);
					// System.Web.Extensions ships with NT8 — no external JSON dependency
					var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
					ser.MaxJsonLength = 16 * 1024 * 1024;
					var arr = ser.Deserialize<List<Dictionary<string, object>>>(json);
					var evts = new List<DateTime>();
					foreach (var it in arr)
					{
						string impact = FieldOrNull(it, "impact");
						string country = FieldOrNull(it, "country");
						if (country == null)
							country = FieldOrNull(it, "currency");
						if (impact == null || !impact.Equals("High", StringComparison.OrdinalIgnoreCase))
							continue;
						if (country == null || !(country.Equals("USD", StringComparison.OrdinalIgnoreCase)
												 || country.Equals("US", StringComparison.OrdinalIgnoreCase)))
							continue;
						DateTimeOffset dto;
						if (!DateTimeOffset.TryParse(FieldOrNull(it, "date"), out dto))
							continue;
						evts.Add(TimeZoneInfo.ConvertTime(dto, easternTz).DateTime);   // ET wall time
					}
					lock (newsLock)
					{
						newsEventsEt = evts;
						newsFetchedUtc = DateTime.UtcNow;
					}
					Print(Name + ": news calendar loaded — " + evts.Count + " high-impact USD events this week.");
				}
			}
			catch (Exception ex)
			{
				Print(Name + ": news fetch failed (guard idle this cycle): " + ex.Message);
			}
			finally
			{
				lock (newsLock) { newsFetchBusy = false; }
			}
		}

		private static string FieldOrNull(Dictionary<string, object> obj, string key)
		{
			object v;
			return obj.TryGetValue(key, out v) && v != null ? v.ToString() : null;
		}

		private bool NewsBlocked(DateTime tradingDate, int sessMin, out string reason)
		{
			// tradingDate: calendar date of the session (RTH keeps one date in any
			// Americas/Europe display timezone). sessMin: bar close, min after 09:30 ET.
			reason = null;
			if (!UseNewsGuard || State != State.Realtime)
				return false;                       // historical replay has no calendar
			List<DateTime> evts;
			lock (newsLock) { evts = newsEventsEt; }
			if (evts == null)
				return false;                       // fetch failed/pending -> fail open
			int settleMin = EtToSessionMin(PreOpenSettleEt);
			foreach (DateTime ev in evts)
			{
				if (ev.Date != tradingDate)
					continue;
				int evMin = ev.Hour * 60 + ev.Minute - SESSION_OPEN_MIN;   // rel. 09:30 ET
				if (evMin >= -150 && evMin <= 0 && sessMin < settleMin)
				{
					reason = ev.ToString("HH:mm") + " ET pre-open event — settling until " +
							 (PreOpenSettleEt / 10000).ToString("00") + ":" + ((PreOpenSettleEt / 100) % 100).ToString("00") + " ET";
					return true;
				}
				if (sessMin >= evMin - NewsPreMin && sessMin <= evMin + NewsPostMin)
				{
					reason = "news blackout around " + ev.ToString("HH:mm") + " ET event";
					return true;
				}
			}
			return false;
		}

		// ── main loop ──

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			// ── raw %K(14); NaN when the 14-bar range is flat (mirrors Python) ──
			double hh = highMax[0], ll = lowMin[0];
			kSeries[0] = hh - ll > 0 ? 100.0 * (Close[0] - ll) / (hh - ll) : double.NaN;

			if (CurrentBar < BarsRequiredToTrade)
				return;

			// ── new session: reset the day state ──
			if (Bars.IsFirstBarOfSession)
			{
				dayPnl = 0;
				dayTrades = 0;
				dayWon = false;
				tradeCountAtDayStart = SystemPerformance.AllTrades.Count;
				// re-fetch the calendar if older than 12h (new week / long sessions)
				if (State == State.Realtime && (DateTime.UtcNow - newsFetchedUtc).TotalHours > 12)
					RefreshNewsAsync();
			}

			int sessMin = BarCloseSessionMin();

			if (State == State.Realtime)
			{
				WatchBarLag();
				Diag(sessMin);
			}

			// ── EOD flat guard (session-close exit is the primary mechanism) ──
			if (Position.MarketPosition == MarketPosition.Long && !TestFireOnce
					&& sessMin >= EtToSessionMin(FlatTimeEt))
			{
				ExitLong("EOD", "");
				return;
			}

			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			// ── TEST fire: one deliberate 1-lot bracket entry, then idle. Set the
			// TEST parameter on a THROWAWAY instance only — it bypasses every gate
			// (window/news/sizing) and never trades the real signal logic. ──
			if (TestFireOnce)
			{
				if (!testFired && State == State.Realtime)
				{
					double tAtr = atr[0];
					if (double.IsNaN(tAtr) || tAtr <= 0)
					{
						Print(Name + ": TEST fire waiting — ATR not ready.");
						return;
					}
					testFired = true;
					int tSl = Math.Max(1, (int)Math.Round(tAtr * SlAtr / TickSize));
					int tTp = Math.Max(1, (int)Math.Round(tAtr * TpAtr / TickSize));
					SetStopLoss(CalculationMode.Ticks, tSl);
					SetProfitTarget(CalculationMode.Ticks, tTp);
					lastSlTicks = tSl;
					lastTpTicks = tTp;
					EnterLong(1, "StochReclaim");
					Print(string.Format("{0}: TEST FIRE on account '{1}' — 1 lot, stop {2} ticks / target {3} ticks OCO bracket.",
										Name, Account.Name, tSl, tTp));
				}
				return;
			}
			if (sessMin < EtToSessionMin(EntryStartEt) || sessMin > EtToSessionMin(EntryEndEt))
				return;
			if (dayTrades >= DayMaxTrades || dayWon)
				return;

			// ── account profit target: stop for good once the eval goal is banked.
			// REAL-TIME ONLY — during historical warm-up the guard is skipped so the
			// chart still shows the strategy's backtest markers. ──
			if (State == State.Realtime && AccountTargetUsd > 0)
			{
				if (targetReached)
					return;
				if (CurrentAccountProfit() >= AccountTargetUsd)
				{
					targetReached = true;
					PostTrade(string.Format(
						"🎯 GOAL HIT — account +${0:F0} (target ${1}). Trading stopped; no new entries.",
						CurrentAccountProfit(), AccountTargetUsd));
					return;
				}
			}

			// ── signal: %K crosses up through the threshold ──
			double kPrev = kSeries[1], k = kSeries[0];
			double atrVal = atr[0];
			if (double.IsNaN(k) || double.IsNaN(kPrev) || atrVal <= 0)
				return;
			if (!(kPrev <= StochThreshold && k > StochThreshold))
				return;

			// flow-positive bar: closes in its upper half (svol > 0 in the bot)
			if (Close[0] <= (High[0] + Low[0]) / 2.0)
				return;

			// news guard (real-time only)
			string newsReason;
			if (NewsBlocked(Time[0].Date, sessMin, out newsReason))
			{
				Print(Name + ": signal skipped — " + newsReason);
				return;
			}

			// ── hard-cap sizing (claude2_app._size_for) ──
			double pointValue = Instrument.MasterInstrument.PointValue;   // $2 MNQ
			double stopUsd = atrVal * SlAtr * pointValue;                 // per contract
			double tpUsd = atrVal * TpAtr * pointValue;
			double budget = DayMaxLossUsd + dayPnl;                       // may still lose this
			int n = Math.Min(MaxContracts, (int)Math.Floor(budget / stopUsd));
			if (DayMaxProfitUsd > 0)
			{
				double room = DayMaxProfitUsd - dayPnl;                   // may still make this
				n = Math.Min(n, (int)Math.Floor(room / tpUsd));
			}
			if (n < 1)
				return;                                                   // day is effectively done

			// ── bracket + entry (offsets anchor to the actual fill, like live OCO) ──
			int slTicks = Math.Max(1, (int)Math.Round(atrVal * SlAtr / TickSize));
			int tpTicks = Math.Max(1, (int)Math.Round(atrVal * TpAtr / TickSize));
			SetStopLoss(CalculationMode.Ticks, slTicks);
			SetProfitTarget(CalculationMode.Ticks, tpTicks);
			lastSlTicks = slTicks;
			lastTpTicks = tpTicks;
			EnterLong(n, "StochReclaim");
		}

		// total account profit = profit already banked before this run + realized
		// P&L of REAL-TIME trades only. Historical backtest trades (index below
		// realtimeStartTradeIndex) are excluded — counting them would false-trip
		// the target during the chart's warm-up and hide all subsequent trades.
		private double CurrentAccountProfit()
		{
			double cum = 0;
			var trades = SystemPerformance.AllTrades;
			for (int i = realtimeStartTradeIndex; i < trades.Count; i++)
				cum += trades[i].ProfitCurrency;
			return AccountBankedUsd + cum;
		}

		protected override void OnPositionUpdate(Position position, double averagePrice,
			int quantity, MarketPosition marketPosition)
		{
			// ── entered a position: ping the fill once (real-time only) ──
			if (marketPosition == MarketPosition.Long)
			{
				if (!inPosition)
				{
					inPosition = true;
					if (State == State.Realtime)
						PostTrade(string.Format(
							"\U0001F7E2 ENTRY  {0} MNQ @ {1:F2}  |  stop {2:F2} / target {3:F2}  |  {4}",
							quantity, averagePrice,
							averagePrice - lastSlTicks * TickSize,
							averagePrice + lastTpTicks * TickSize,
							Account.Name));
				}
				return;                     // partial reductions stay Long — no re-ping
			}

			if (marketPosition != MarketPosition.Flat)
				return;                     // long-only strategy; ignore any short state

			// ── went flat: refresh today's realized P&L + completed-trade count ──
			double pnl = 0;
			for (int i = tradeCountAtDayStart; i < SystemPerformance.AllTrades.Count; i++)
				pnl += SystemPerformance.AllTrades[i].ProfitCurrency;
			dayPnl = pnl;
			dayTrades++;

			// ── ping the closed trade with its result ──
			bool won = false;
			if (SystemPerformance.AllTrades.Count > tradeCountAtDayStart)
			{
				Trade last = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
				won = last.ProfitCurrency > 0;
				if (inPosition && State == State.Realtime)
					PostTrade(string.Format(
						"{0} EXIT  {1} MNQ  {2:F2} -> {3:F2}  |  {4}${5:F0}  |  day {6}${7:F0} on {8} trade{9}",
						won ? "✅" : "\U0001F534",
						last.Quantity, last.Entry.Price, last.Exit.Price,
						last.ProfitCurrency >= 0 ? "+" : "-", Math.Abs(last.ProfitCurrency),
						dayPnl >= 0 ? "+" : "-", Math.Abs(dayPnl),
						dayTrades, dayTrades == 1 ? "" : "s"));
			}
			inPosition = false;

			if (StopAfterWin && won)
				dayWon = true;
		}

		#region Properties
		[NinjaScriptProperty, Range(2, int.MaxValue)]
		[Display(Name = "Stoch %K period", GroupName = "Signal", Order = 0)]
		public int StochPeriod { get; set; }

		[NinjaScriptProperty, Range(1, 99)]
		[Display(Name = "Stoch threshold (cross up)", GroupName = "Signal", Order = 1)]
		public int StochThreshold { get; set; }

		[NinjaScriptProperty, Range(2, int.MaxValue)]
		[Display(Name = "ATR period", GroupName = "Signal", Order = 2)]
		public int AtrPeriod { get; set; }

		[NinjaScriptProperty, Range(0.05, 10)]
		[Display(Name = "Stop (x ATR)", GroupName = "Risk", Order = 0)]
		public double SlAtr { get; set; }

		[NinjaScriptProperty, Range(0.05, 10)]
		[Display(Name = "Target (x ATR)", GroupName = "Risk", Order = 1)]
		public double TpAtr { get; set; }

		[NinjaScriptProperty, Range(1, 100)]
		[Display(Name = "Max contracts", GroupName = "Risk", Order = 2)]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty, Range(1, 100000)]
		[Display(Name = "Day loss budget ($)", GroupName = "Risk", Order = 3)]
		public int DayMaxLossUsd { get; set; }

		[NinjaScriptProperty, Range(0, 100000)]
		[Display(Name = "Day profit cap ($, 0 = off)", GroupName = "Risk", Order = 4)]
		public int DayMaxProfitUsd { get; set; }

		[NinjaScriptProperty, Range(1, 50)]
		[Display(Name = "Max trades / day", GroupName = "Risk", Order = 5)]
		public int DayMaxTrades { get; set; }

		[NinjaScriptProperty, Range(0, 235959)]
		[Display(Name = "Entry window start (ET bar close, HHmmss)", GroupName = "Session", Order = 0)]
		public int EntryStartEt { get; set; }

		[NinjaScriptProperty, Range(0, 235959)]
		[Display(Name = "Entry window end (ET bar close, HHmmss)", GroupName = "Session", Order = 1)]
		public int EntryEndEt { get; set; }

		[NinjaScriptProperty, Range(0, 235959)]
		[Display(Name = "Flat time (ET, HHmmss)", GroupName = "Session", Order = 2)]
		public int FlatTimeEt { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stop after first win", GroupName = "Session", Order = 3)]
		public bool StopAfterWin { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TEST: fire one 1-lot bracket entry on next bar", GroupName = "Test", Order = 0)]
		public bool TestFireOnce { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News guard (ForexFactory high-impact USD)", GroupName = "News", Order = 0)]
		public bool UseNewsGuard { get; set; }

		[NinjaScriptProperty, Range(0, 240)]
		[Display(Name = "Blackout minutes before event", GroupName = "News", Order = 1)]
		public int NewsPreMin { get; set; }

		[NinjaScriptProperty, Range(0, 240)]
		[Display(Name = "Blackout minutes after event", GroupName = "News", Order = 2)]
		public int NewsPostMin { get; set; }

		[NinjaScriptProperty, Range(0, 235959)]
		[Display(Name = "Pre-open event: entries resume (ET, HHmmss)", GroupName = "News", Order = 3)]
		public int PreOpenSettleEt { get; set; }

		[NinjaScriptProperty, Range(0, 1000000)]
		[Display(Name = "Account profit target ($, 0 = off)", GroupName = "Account goal", Order = 0)]
		public double AccountTargetUsd { get; set; }

		[NinjaScriptProperty, Range(0, 1000000)]
		[Display(Name = "Profit already banked at start ($)", GroupName = "Account goal", Order = 1)]
		public double AccountBankedUsd { get; set; }
		#endregion
	}
}
