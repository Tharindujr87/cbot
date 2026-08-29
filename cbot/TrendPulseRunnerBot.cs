#pragma warning disable CS0618
using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class TrendPulseRunnerBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        // --- Trend Regime Filters ---
        [Parameter("Fast EMA (Pullback Zone)", Group = "Trend Engine", DefaultValue = 20, MinValue = 10, MaxValue = 50)]
        public int FastEmaPeriod { get; set; }

        [Parameter("Medium EMA (Baseline)", Group = "Trend Engine", DefaultValue = 50, MinValue = 30, MaxValue = 100)]
        public int MedEmaPeriod { get; set; }

        [Parameter("Slow EMA (Macro Trend)", Group = "Trend Engine", DefaultValue = 200, MinValue = 100, MaxValue = 400)]
        public int SlowEmaPeriod { get; set; }

        [Parameter("ADX Trend Period", Group = "Trend Engine", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int AdxPeriod { get; set; }

        [Parameter("Min ADX Strength", Group = "Trend Engine", DefaultValue = 20.0, MinValue = 10.0, MaxValue = 40.0)]
        public double MinAdxStrength { get; set; }

        // --- Pullback & Momentum Trigger ---
        [Parameter("Stochastic %K Period", Group = "Momentum Trigger", DefaultValue = 9, MinValue = 3, MaxValue = 21)]
        public int StochK { get; set; }

        [Parameter("Stochastic %D Period", Group = "Momentum Trigger", DefaultValue = 3, MinValue = 2, MaxValue = 10)]
        public int StochD { get; set; }

        [Parameter("Stochastic Slowing", Group = "Momentum Trigger", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int StochSlowing { get; set; }

        [Parameter("Stoch Long Oversold (<)", Group = "Momentum Trigger", DefaultValue = 30.0, MinValue = 15.0, MaxValue = 45.0)]
        public double StochLongThreshold { get; set; }

        [Parameter("Stoch Short Overbought (>)", Group = "Momentum Trigger", DefaultValue = 70.0, MinValue = 55.0, MaxValue = 85.0)]
        public double StochShortThreshold { get; set; }

        // --- Session Timing Filter ---
        [Parameter("Session Start Hour (UTC)", Group = "Session Clocks", DefaultValue = 6, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (UTC)", Group = "Session Clocks", DefaultValue = 18, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Session Clocks", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        // --- Risk, Profit & Trailing Stop Management ---
        [Parameter("ATR Period", Group = "Risk Management", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int AtrPeriod { get; set; }

        [Parameter("Stop Loss (ATR Mult)", Group = "Risk Management", DefaultValue = 1.5, MinValue = 0.8, MaxValue = 4.0)]
        public double StopLossAtrMult { get; set; }

        [Parameter("Take Profit Target 1 (R:R)", Group = "Risk Management", DefaultValue = 1.8, MinValue = 1.0, MaxValue = 5.0)]
        public double TargetRiskReward { get; set; }

        [Parameter("Enable Breakeven at 1R", Group = "Risk Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Enable Dynamic Trailing Stop", Group = "Risk Management", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }

        [Parameter("Trailing Stop ATR Mult", Group = "Risk Management", DefaultValue = 1.2, MinValue = 0.5, MaxValue = 3.0)]
        public double TrailingAtrMult { get; set; }

        [Parameter("Risk Per Trade %", Group = "Risk Management", DefaultValue = 1.5, MinValue = 0.2, MaxValue = 10.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Daily Loss Limit %", Group = "Risk Management", DefaultValue = 4.0, MinValue = 1.0, MaxValue = 15.0)]
        public double DailyLossLimitPercent { get; set; }

        // Indicators
        private ExponentialMovingAverage _fastEma;
        private ExponentialMovingAverage _medEma;
        private ExponentialMovingAverage _slowEma;
        private DirectionalMovementSystem _dms;
        private StochasticOscillator _stoch;
        private AverageTrueRange _atr;

        // Daily State
        private double _dailyStartingBalance;
        private int _currentDay;
        private bool _dailyHalted;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;
            _dailyHalted = false;

            _fastEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastEmaPeriod);
            _medEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, MedEmaPeriod);
            _slowEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SlowEmaPeriod);
            _dms = Indicators.DirectionalMovementSystem(Bars, AdxPeriod);
            _stoch = Indicators.StochasticOscillator(Bars, StochK, StochD, StochSlowing, MovingAverageType.Simple);
            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);

            Print("[TrendPulse Bot Initialized] Symbol: {0} | TimeFrame: {1}", SymbolName, TimeFrame);
        }

        protected override void OnTick()
        {
            // 1. Daily Drawdown Guard
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double drawdownPct = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (drawdownPct >= DailyLossLimitPercent && !_dailyHalted)
            {
                _dailyHalted = true;
                Print("[SAFETY HALT] Daily loss limit {0:F2}% hit. No more entries today.", drawdownPct);
            }

            // 2. Active Trade Breakeven & Dynamic Trailing Stop
            var position = Positions.Find("TrendPulse", SymbolName);
            if (position != null && position.StopLoss.HasValue)
            {
                ManagePositionStops(position);
            }
        }

        protected override void OnBar()
        {
            int requiredBars = Math.Max(SlowEmaPeriod, Math.Max(AdxPeriod, 30)) + 5;
            if (Bars.Count < requiredBars)
                return;

            DateTime now = Server.Time;

            // Daily Reset at 00:00 UTC
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _dailyStartingBalance = Account.Balance;
                _dailyHalted = false;
            }

            if (_dailyHalted) return;

            // Session Time Window (06:00 - 18:00 UTC London / NY Overlap)
            if (now.Hour < SessionStartHour || now.Hour >= SessionEndHour)
                return;

            // Spread Guard
            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips)
                return;

            // Strict single position rule
            if (Positions.FindAll("TrendPulse", SymbolName).Length > 0)
                return;

            // Pullback & Trend Indicators on completed bar Last(1)
            double close1 = Bars.ClosePrices.Last(1);
            double high1 = Bars.HighPrices.Last(1);
            double low1 = Bars.LowPrices.Last(1);

            double fastEma1 = _fastEma.Result.Last(1);
            double medEma1 = _medEma.Result.Last(1);
            double slowEma1 = _slowEma.Result.Last(1);

            double adx = _dms.ADX.Last(1);
            double diPlus = _dms.DIPlus.Last(1);
            double diMinus = _dms.DIMinus.Last(1);

            double stochKPrev = _stoch.PercentK.Last(2);
            double stochKCurr = _stoch.PercentK.Last(1);
            double stochDCurr = _stoch.PercentD.Last(1);

            double atr = _atr.Result.Last(1);

            // ==========================================
            // 🟢 HIGH-ACCURACY BULLISH SETUP (BUY)
            // ==========================================
            // 1. Macro Trend: 50 EMA > 200 EMA & Price > 200 EMA
            bool bullMacroTrend = (medEma1 > slowEma1) && (close1 > slowEma1);

            // 2. ADX Trend Strength: ADX > Min Strength and DI+ > DI-
            bool bullTrendStrong = (adx >= MinAdxStrength) && (diPlus > diMinus);

            // 3. Value Zone Pullback: Low dipped into 20/50 EMA zone, Close rejected back up
            bool bullPullbackZone = (low1 <= fastEma1) && (close1 >= Math.Min(fastEma1, medEma1));

            // 4. Momentum Reversal: Stoch crossed up from oversold region
            bool bullStochTrigger = (stochKPrev <= StochLongThreshold || stochKCurr <= (StochLongThreshold + 10.0)) 
                                    && (stochKCurr > stochDCurr);

            if (bullMacroTrend && bullTrendStrong && bullPullbackZone && bullStochTrigger)
            {
                double slDistancePips = (atr * StopLossAtrMult) / Symbol.PipSize;
                double tpDistancePips = slDistancePips * TargetRiskReward;
                if (slDistancePips < 4.0) slDistancePips = 4.0;

                ExecuteOrder(TradeType.Buy, close1, slDistancePips, tpDistancePips);
                return;
            }

            // ==========================================
            // 🔴 HIGH-ACCURACY BEARISH SETUP (SELL)
            // ==========================================
            // 1. Macro Trend: 50 EMA < 200 EMA & Price < 200 EMA
            bool bearMacroTrend = (medEma1 < slowEma1) && (close1 < slowEma1);

            // 2. ADX Trend Strength: ADX > Min Strength and DI- > DI+
            bool bearTrendStrong = (adx >= MinAdxStrength) && (diMinus > diPlus);

            // 3. Value Zone Pullback: High rallied into 20/50 EMA zone, Close rejected back down
            bool bearPullbackZone = (high1 >= fastEma1) && (close1 <= Math.Max(fastEma1, medEma1));

            // 4. Momentum Reversal: Stoch crossed down from overbought region
            bool bearStochTrigger = (stochKPrev >= StochShortThreshold || stochKCurr >= (StochShortThreshold - 10.0)) 
                                    && (stochKCurr < stochDCurr);

            if (bearMacroTrend && bearTrendStrong && bearPullbackZone && bearStochTrigger)
            {
                double slDistancePips = (atr * StopLossAtrMult) / Symbol.PipSize;
                double tpDistancePips = slDistancePips * TargetRiskReward;
                if (slDistancePips < 4.0) slDistancePips = 4.0;

                ExecuteOrder(TradeType.Sell, close1, slDistancePips, tpDistancePips);
                return;
            }
        }

        private void ExecuteOrder(TradeType tradeType, double entryPrice, double slPips, double tpPips)
        {
            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, slPips);

            // 85% Free Margin Guard
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

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "TrendPulse", slPips, tpPips);
            if (result.IsSuccessful)
            {
                Print("[TREND PULSE ENTRY] {0} {1} units @ {2:F5} | SL: {3:F1} pips | TP: {4:F1} pips", 
                    tradeType, volumeInUnits, entryPrice, slPips, tpPips);
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

            double currentGain = position.TradeType == TradeType.Buy 
                ? (currentPrice - entryPrice) 
                : (entryPrice - currentPrice);

            double profitInR = currentGain / initialRisk;

            // 1. Move to Breakeven (+0.5 pip) at +1.0R
            if (EnableBreakeven && profitInR >= 1.0)
            {
                double bePrice = position.TradeType == TradeType.Buy 
                    ? entryPrice + (0.5 * Symbol.PipSize) 
                    : entryPrice - (0.5 * Symbol.PipSize);

                bool needsBe = position.TradeType == TradeType.Buy 
                    ? position.StopLoss.Value < bePrice 
                    : position.StopLoss.Value > bePrice;

                if (needsBe)
                {
                    ModifyPosition(position, bePrice, position.TakeProfit);
                    Print("[Breakeven Locked] Stop Loss moved to Entry + 0.5 pips ({0:F5})", bePrice);
                }
            }

            // 2. Dynamic ATR Trailing Stop after +1.2R
            if (EnableTrailingStop && profitInR >= 1.2)
            {
                double atrTrailDistance = _atr.Result.Last(1) * TrailingAtrMult;
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
