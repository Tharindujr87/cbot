#pragma warning disable CS0618
using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class SimpleEmaRsiBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        // --- Bollinger Bands ---
        [Parameter("BB Period", Group = "Bollinger Bands", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int BbPeriod { get; set; }

        [Parameter("BB Standard Deviations", Group = "Bollinger Bands", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 4.0)]
        public double BbStdDev { get; set; }

        // --- Triple RSI Parameters ---
        [Parameter("RSI 1 (Fast) Period", Group = "Triple RSI", DefaultValue = 7, MinValue = 2, MaxValue = 20)]
        public int Rsi1Period { get; set; }

        [Parameter("RSI 2 (Medium) Period", Group = "Triple RSI", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int Rsi2Period { get; set; }

        [Parameter("RSI 3 (Slow) Period", Group = "Triple RSI", DefaultValue = 28, MinValue = 10, MaxValue = 60)]
        public int Rsi3Period { get; set; }

        [Parameter("RSI Fast Long Threshold (<= x)", Group = "Triple RSI", DefaultValue = 35.0, MinValue = 10.0, MaxValue = 50.0)]
        public double RsiFastLongThreshold { get; set; }

        [Parameter("RSI Medium Long Threshold (<= y)", Group = "Triple RSI", DefaultValue = 45.0, MinValue = 20.0, MaxValue = 55.0)]
        public double RsiMedLongThreshold { get; set; }

        [Parameter("RSI Slow Long Threshold (<= z)", Group = "Triple RSI", DefaultValue = 50.0, MinValue = 30.0, MaxValue = 60.0)]
        public double RsiSlowLongThreshold { get; set; }

        [Parameter("RSI Fast Short Threshold (>= x)", Group = "Triple RSI", DefaultValue = 65.0, MinValue = 50.0, MaxValue = 90.0)]
        public double RsiFastShortThreshold { get; set; }

        [Parameter("RSI Medium Short Threshold (>= y)", Group = "Triple RSI", DefaultValue = 55.0, MinValue = 45.0, MaxValue = 80.0)]
        public double RsiMedShortThreshold { get; set; }

        [Parameter("RSI Slow Short Threshold (>= z)", Group = "Triple RSI", DefaultValue = 50.0, MinValue = 40.0, MaxValue = 70.0)]
        public double RsiSlowShortThreshold { get; set; }

        // --- Trend & Volatility Filters ---
        [Parameter("EMA Trend Period", Group = "Trend & Volatility", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int EmaPeriod { get; set; }

        [Parameter("Enable EMA 50 Filter", Group = "Trend & Volatility", DefaultValue = true)]
        public bool EnableEmaFilter { get; set; }

        [Parameter("ATR Period", Group = "Trend & Volatility", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int AtrPeriod { get; set; }

        [Parameter("Max Allowed Spread (Pips)", Group = "Trend & Volatility", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 5.0)]
        public double MaxAllowedSpreadPips { get; set; }

        // --- Session Filter & Time Clocks ---
        [Parameter("Session Start Hour (UTC)", Group = "Session Filter", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int SessionStartHourUtc { get; set; }

        [Parameter("Session End Hour (UTC)", Group = "Session Filter", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int SessionEndHourUtc { get; set; }

        [Parameter("Force Close Hour (UTC)", Group = "Session Filter", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int ForceCloseHourUtc { get; set; }

        [Parameter("Force Close Minute (UTC)", Group = "Session Filter", DefaultValue = 45, MinValue = 0, MaxValue = 59)]
        public int ForceCloseMinuteUtc { get; set; }

        // --- Risk & Position Management ---
        [Parameter("Risk Per Trade %", Group = "Risk Management", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 10.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("SL Multiplier (x ATR)", Group = "Risk Management", DefaultValue = 1.2, MinValue = 0.5, MaxValue = 5.0)]
        public double SlMultiplier { get; set; }

        [Parameter("TP Multiplier (x ATR)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 10.0)]
        public double TpMultiplier { get; set; }

        [Parameter("Enable Breakeven (+1R)", Group = "Risk Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Enable ATR Trailing Stop", Group = "Risk Management", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }

        [Parameter("Trailing Stop ATR Mult", Group = "Risk Management", DefaultValue = 0.8, MinValue = 0.2, MaxValue = 3.0)]
        public double TrailingAtrMultiplier { get; set; }

        [Parameter("Max Trades Per Day", Group = "Safety Guardrails", DefaultValue = 4, MinValue = 1, MaxValue = 20)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Daily Loss Limit %", Group = "Safety Guardrails", DefaultValue = 4.0, MinValue = 1.0, MaxValue = 20.0)]
        public double DailyLossLimitPercent { get; set; }

        // Indicator References
        private BollingerBands _bb;
        private RelativeStrengthIndex _rsi1;
        private RelativeStrengthIndex _rsi2;
        private RelativeStrengthIndex _rsi3;
        private ExponentialMovingAverage _ema;
        private AverageTrueRange _atr;

        // Daily State Variables
        private double _dailyStartingBalance;
        private int _currentDay;
        private int _tradesCountToday;
        private bool _dailyLossHalted;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;
            _tradesCountToday = 0;
            _dailyLossHalted = false;

            // Initialize Indicators on M15
            _bb = Indicators.BollingerBands(Bars.ClosePrices, BbPeriod, BbStdDev, MovingAverageType.Simple);
            _rsi1 = Indicators.RelativeStrengthIndex(Bars.ClosePrices, Rsi1Period);
            _rsi2 = Indicators.RelativeStrengthIndex(Bars.ClosePrices, Rsi2Period);
            _rsi3 = Indicators.RelativeStrengthIndex(Bars.ClosePrices, Rsi3Period);
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);

            Print("[Bollinger 3RSI Bot] Initialized on {0} ({1}) | Session: {2}:00 - {3}:00 UTC", 
                SymbolName, TimeFrame, SessionStartHourUtc, SessionEndHourUtc);
        }

        protected override void OnTick()
        {
            DateTime now = Server.Time;

            // Check Force Close at 16:45 UTC
            if ((now.Hour == ForceCloseHourUtc && now.Minute >= ForceCloseMinuteUtc) || now.Hour > ForceCloseHourUtc)
            {
                var pos = Positions.Find("BB3RSI", SymbolName);
                if (pos != null)
                {
                    ClosePosition(pos);
                    Print("[EOD FORCE CLOSE] Closed open position at {0}:{1:D2} UTC session end.", now.Hour, now.Minute);
                }
            }

            // Daily Drawdown Guard
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double dailyDrawdownPct = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (dailyDrawdownPct >= DailyLossLimitPercent && !_dailyLossHalted)
            {
                _dailyLossHalted = true;
                Print("[DAILY LOSS LIMIT HIT] Drawdown {0:F2}% >= {1}%. Disabling new entries for today.", 
                    dailyDrawdownPct, DailyLossLimitPercent);
            }

            // Manage Breakeven & Trailing Stops
            var activePosition = Positions.Find("BB3RSI", SymbolName);
            if (activePosition != null && activePosition.StopLoss.HasValue)
            {
                ManagePositionStops(activePosition);
            }
        }

        protected override void OnBar()
        {
            int requiredBars = Math.Max(EmaPeriod, Math.Max(BbPeriod, Rsi3Period)) + 5;
            if (Bars.Count < requiredBars)
                return;

            DateTime now = Server.Time;

            // Daily Reset at new day
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _dailyStartingBalance = Account.Balance;
                _tradesCountToday = 0;
                _dailyLossHalted = false;
            }

            // Safety Guards: Max trades or Daily Loss Limit
            if (_tradesCountToday >= MaxTradesPerDay || _dailyLossHalted)
                return;

            // Session Time Filter (07:00 - 16:00 UTC)
            if (now.Hour < SessionStartHourUtc || now.Hour >= SessionEndHourUtc)
                return;

            // Spread Filter
            double currentSpreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (currentSpreadPips > MaxAllowedSpreadPips)
                return;

            // Only 1 concurrent position allowed
            if (Positions.FindAll("BB3RSI", SymbolName).Length > 0)
                return;

            // Extract Indicator Values on the completed bar Last(1)
            double close1 = Bars.ClosePrices.Last(1);
            double lowerBb1 = _bb.Bottom.Last(1);
            double upperBb1 = _bb.Top.Last(1);

            double rsi1Val = _rsi1.Result.Last(1);
            double rsi2Val = _rsi2.Result.Last(1);
            double rsi3Val = _rsi3.Result.Last(1);

            double ema50Val = _ema.Result.Last(1);
            double atrVal = _atr.Result.Last(1);

            // --- LONG ENTRY CONDITIONS ---
            // 1. Close <= Lower BB
            // 2. 3x RSI Oversold conditions (RSI7 <= x, RSI14 <= y, RSI28 <= z)
            // 3. Close > 50 EMA (Buying dip in overall bullish structure)
            bool longBb = close1 <= lowerBb1;
            bool longRsi = (rsi1Val <= RsiFastLongThreshold) && (rsi2Val <= RsiMedLongThreshold) && (rsi3Val <= RsiSlowLongThreshold);
            bool longEma = !EnableEmaFilter || (close1 > ema50Val);

            if (longBb && longRsi && longEma)
            {
                double slPips = (atrVal * SlMultiplier) / Symbol.PipSize;
                double tpPips = (atrVal * TpMultiplier) / Symbol.PipSize;
                if (slPips < 3.0) slPips = 3.0;

                ExecuteEntry(TradeType.Buy, close1, slPips, tpPips);
                return;
            }

            // --- SHORT ENTRY CONDITIONS ---
            // 1. Close >= Upper BB
            // 2. 3x RSI Overbought conditions (RSI7 >= x, RSI14 >= y, RSI28 >= z)
            // 3. Close < 50 EMA (Selling rally in overall bearish structure)
            bool shortBb = close1 >= upperBb1;
            bool shortRsi = (rsi1Val >= RsiFastShortThreshold) && (rsi2Val >= RsiMedShortThreshold) && (rsi3Val >= RsiSlowShortThreshold);
            bool shortEma = !EnableEmaFilter || (close1 < ema50Val);

            if (shortBb && shortRsi && shortEma)
            {
                double slPips = (atrVal * SlMultiplier) / Symbol.PipSize;
                double tpPips = (atrVal * TpMultiplier) / Symbol.PipSize;
                if (slPips < 3.0) slPips = 3.0;

                ExecuteEntry(TradeType.Sell, close1, slPips, tpPips);
                return;
            }
        }

        private void ExecuteEntry(TradeType tradeType, double entryPrice, double slPips, double tpPips)
        {
            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, slPips);

            // Free Margin Buffer Protection (85%)
            double requiredMargin = volumeInUnits / 30.0;
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                volumeInUnits = (Account.FreeMargin * 0.85) * 30.0;
            }

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                volumeInUnits = Symbol.VolumeInUnitsMin;
            }

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "BB3RSI", slPips, tpPips);
            if (result.IsSuccessful)
            {
                _tradesCountToday++;
                Print("[BB 3RSI ENTRY] {0} {1} units @ {2:F5} | SL: {3:F1} pips | TP: {4:F1} pips (Trade #{5} today)", 
                    tradeType, volumeInUnits, entryPrice, slPips, tpPips, _tradesCountToday);
            }
            else
            {
                Print("[Order Error] Failed: {0}", result.Error);
            }
        }

        private void ManagePositionStops(Position position)
        {
            double entryPrice = position.EntryPrice;
            double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            double initialRisk = Math.Abs(entryPrice - position.StopLoss.Value);

            if (initialRisk <= 0) return;

            double currentProfit = position.TradeType == TradeType.Buy 
                ? (currentPrice - entryPrice) 
                : (entryPrice - currentPrice);

            double profitInR = currentProfit / initialRisk;

            // 1. Move to Breakeven (+1.0 pip) at +1.0R
            if (EnableBreakeven && profitInR >= 1.0)
            {
                double bePrice = position.TradeType == TradeType.Buy 
                    ? entryPrice + (1.0 * Symbol.PipSize) 
                    : entryPrice - (1.0 * Symbol.PipSize);

                bool needsBe = position.TradeType == TradeType.Buy 
                    ? position.StopLoss.Value < bePrice 
                    : position.StopLoss.Value > bePrice;

                if (needsBe)
                {
                    ModifyPosition(position, bePrice, position.TakeProfit);
                    Print("[Breakeven Locked] Stop Loss moved to Entry + 1 pip ({0:F5})", bePrice);
                }
            }

            // 2. ATR Trailing Stop (Trail by ATR * TrailingAtrMultiplier after +1.0R)
            if (EnableTrailingStop && profitInR >= 1.0)
            {
                double atrTrailDistance = _atr.Result.Last(1) * TrailingAtrMultiplier;
                double trailStopPrice = position.TradeType == TradeType.Buy 
                    ? currentPrice - atrTrailDistance 
                    : currentPrice + atrTrailDistance;

                bool isTighter = position.TradeType == TradeType.Buy 
                    ? trailStopPrice > position.StopLoss.Value 
                    : trailStopPrice < position.StopLoss.Value;

                if (isTighter)
                {
                    ModifyPosition(position, trailStopPrice, position.TakeProfit);
                }
            }
        }

        private double CalculateVolumeUnits(double riskAmount, double riskPips)
        {
            if (riskPips <= 0) return Symbol.VolumeInUnitsMin;
            double pipValuePerUnit = Symbol.PipValue;
            return riskAmount / (riskPips * pipValuePerUnit);
        }
    }
}
