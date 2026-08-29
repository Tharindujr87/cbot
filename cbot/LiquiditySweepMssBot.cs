#pragma warning disable CS0618
using System;
using System.IO;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        [Parameter("SuperTrend ATR Period", DefaultValue = 10, MinValue = 5, MaxValue = 50)]
        public int AtrPeriod { get; set; }

        [Parameter("SuperTrend Multiplier", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 6.0)]
        public double SuperTrendMultiplier { get; set; }

        [Parameter("Enable EMA Trend Filter", DefaultValue = true)]
        public bool EnableEmaFilter { get; set; }

        [Parameter("EMA Trend Period", DefaultValue = 200, MinValue = 20, MaxValue = 500)]
        public int EmaPeriod { get; set; }

        [Parameter("Enable RSI Momentum Filter", DefaultValue = true)]
        public bool EnableRsiFilter { get; set; }

        [Parameter("RSI Period", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Long Threshold", DefaultValue = 50.0, MinValue = 40.0, MaxValue = 65.0)]
        public double RsiLongThreshold { get; set; }

        [Parameter("RSI Short Threshold", DefaultValue = 50.0, MinValue = 35.0, MaxValue = 60.0)]
        public double RsiShortThreshold { get; set; }

        [Parameter("Enable Fixed Take Profit", DefaultValue = false)]
        public bool EnableFixedTp { get; set; }

        [Parameter("Risk Reward Ratio (TP)", DefaultValue = 3.0, MinValue = 1.5, MaxValue = 6.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Risk Per Trade %", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", DefaultValue = 30.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Config File Path", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // Indicators & DataSeries
        private AverageTrueRange _atr;
        private ExponentialMovingAverage _ema;
        private RelativeStrengthIndex _rsi;
        private IndicatorDataSeries _superTrendUp;
        private IndicatorDataSeries _superTrendDown;
        private IndicatorDataSeries _superTrendDirection; // 1 = Bullish (Green), -1 = Bearish (Red)
        private double _dailyStartingBalance;
        private int _currentDay;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;

            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);

            _superTrendUp = CreateDataSeries();
            _superTrendDown = CreateDataSeries();
            _superTrendDirection = CreateDataSeries();

            // Pre-calculate SuperTrend series over historical bars
            for (int i = 0; i < Bars.Count; i++)
            {
                CalculateSuperTrend(i);
            }

            Print("[LuxAlgo SuperTrend Bot Started] Initialized on {0} ({1}) | ATR: {2}, Mult: {3}", 
                SymbolName, TimeFrame, AtrPeriod, SuperTrendMultiplier);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            CheckHotReloadConfig();

            // 1. Safety: Daily Drawdown Circuit Breaker Guard
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double drawdownPercent = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (drawdownPercent >= CircuitBreakerDrawdownPercent)
            {
                TriggerCircuitBreaker(drawdownPercent);
                return;
            }

            // 2. Continuous Dynamic SuperTrend Trailing Stop Management
            var position = Positions.Find("LuxSuperTrend", SymbolName);
            if (position != null)
            {
                ManageSuperTrendTrailingStop(position);
            }
        }

        protected override void OnBar()
        {
            int lastCompletedIndex = Bars.Count - 2;
            int currentIndex = Bars.Count - 1;

            if (lastCompletedIndex < Math.Max(AtrPeriod, Math.Max(EmaPeriod, RsiPeriod)) + 2)
                return;

            // Update SuperTrend for the newly completed bar
            CalculateSuperTrend(lastCompletedIndex);

            DateTime now = Server.Time;
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _dailyStartingBalance = Account.Balance;
            }

            double prevDirection = _superTrendDirection[lastCompletedIndex - 1];
            double currentDirection = _superTrendDirection[lastCompletedIndex];

            double lastClose = Bars.ClosePrices[lastCompletedIndex];
            double emaValue = _ema.Result[lastCompletedIndex];
            double rsiValue = _rsi.Result[lastCompletedIndex];

            var activePosition = Positions.Find("LuxSuperTrend", SymbolName);

            // --- BULLISH SIGNAL: SuperTrend flipped from Red (-1) to Green (+1) ---
            if (prevDirection == -1 && currentDirection == 1)
            {
                // Close any existing Sell position immediately
                if (activePosition != null && activePosition.TradeType == TradeType.Sell)
                {
                    ClosePosition(activePosition);
                }

                // Check Filters
                bool emaPass = !EnableEmaFilter || (lastClose > emaValue);
                bool rsiPass = !EnableRsiFilter || (rsiValue >= RsiLongThreshold);

                if (emaPass && rsiPass && Positions.Count == 0)
                {
                    double stStopPrice = _superTrendDown[lastCompletedIndex];
                    double riskPips = (lastClose - stStopPrice) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = (_atr.Result[lastCompletedIndex] * SuperTrendMultiplier) / Symbol.PipSize;

                    double? takeProfit = EnableFixedTp ? (double?)(lastClose + (riskPips * RiskRewardRatio * Symbol.PipSize)) : null;

                    ExecuteTrade(TradeType.Buy, lastClose, stStopPrice, riskPips, takeProfit);
                }
            }
            // --- BEARISH SIGNAL: SuperTrend flipped from Green (+1) to Red (-1) ---
            else if (prevDirection == 1 && currentDirection == -1)
            {
                // Close any existing Buy position immediately
                if (activePosition != null && activePosition.TradeType == TradeType.Buy)
                {
                    ClosePosition(activePosition);
                }

                // Check Filters
                bool emaPass = !EnableEmaFilter || (lastClose < emaValue);
                bool rsiPass = !EnableRsiFilter || (rsiValue <= RsiShortThreshold);

                if (emaPass && rsiPass && Positions.Count == 0)
                {
                    double stStopPrice = _superTrendUp[lastCompletedIndex];
                    double riskPips = (stStopPrice - lastClose) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = (_atr.Result[lastCompletedIndex] * SuperTrendMultiplier) / Symbol.PipSize;

                    double? takeProfit = EnableFixedTp ? (double?)(lastClose - (riskPips * RiskRewardRatio * Symbol.PipSize)) : null;

                    ExecuteTrade(TradeType.Sell, lastClose, stStopPrice, riskPips, takeProfit);
                }
            }
        }

        private void ExecuteTrade(TradeType tradeType, double entryPrice, double stopPrice, double riskPips, double? takeProfitPrice)
        {
            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, riskPips);

            // Free margin buffer protection (85%)
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

            double? tpPips = takeProfitPrice.HasValue ? (double?)(Math.Abs(takeProfitPrice.Value - entryPrice) / Symbol.PipSize) : null;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "LuxSuperTrend", riskPips, tpPips);
            if (result.IsSuccessful)
            {
                Print("[LuxAlgo SuperTrend Entry] {0} {1} units @ {2:F5} | Initial Stop: {3:F5} ({4:F1} pips)", 
                    tradeType, volumeInUnits, entryPrice, stopPrice, riskPips);
            }
            else
            {
                Print("[Order Error] Failed: {0}", result.Error);
            }
        }

        private void ManageSuperTrendTrailingStop(Position position)
        {
            int lastCompletedIndex = Bars.Count - 2;
            if (lastCompletedIndex < 1) return;

            if (position.TradeType == TradeType.Buy)
            {
                double currentSuperTrend = _superTrendDown[lastCompletedIndex];
                if (currentSuperTrend > 0)
                {
                    // Move stop loss higher along the SuperTrend green line
                    if (!position.StopLoss.HasValue || currentSuperTrend > position.StopLoss.Value)
                    {
                        ModifyPosition(position, currentSuperTrend, position.TakeProfit);
                    }
                }
            }
            else if (position.TradeType == TradeType.Sell)
            {
                double currentSuperTrend = _superTrendUp[lastCompletedIndex];
                if (currentSuperTrend > 0)
                {
                    // Move stop loss lower along the SuperTrend red line
                    if (!position.StopLoss.HasValue || currentSuperTrend < position.StopLoss.Value)
                    {
                        ModifyPosition(position, currentSuperTrend, position.TakeProfit);
                    }
                }
            }
        }

        private void CalculateSuperTrend(int index)
        {
            if (index < AtrPeriod)
            {
                _superTrendUp[index] = 0;
                _superTrendDown[index] = 0;
                _superTrendDirection[index] = 1;
                return;
            }

            double high = Bars.HighPrices[index];
            double low = Bars.LowPrices[index];
            double close = Bars.ClosePrices[index];
            double prevClose = Bars.ClosePrices[index - 1];
            double atr = _atr.Result[index];

            double basicUpper = ((high + low) / 2.0) + (SuperTrendMultiplier * atr);
            double basicLower = ((high + low) / 2.0) - (SuperTrendMultiplier * atr);

            double prevFinalUpper = index > 0 ? _superTrendUp[index - 1] : basicUpper;
            double prevFinalLower = index > 0 ? _superTrendDown[index - 1] : basicLower;
            double prevDirection = index > 0 && _superTrendDirection[index - 1] != 0 ? _superTrendDirection[index - 1] : 1;

            double finalUpper = (basicUpper < prevFinalUpper || prevClose > prevFinalUpper) ? basicUpper : prevFinalUpper;
            double finalLower = (basicLower > prevFinalLower || prevClose < prevFinalLower) ? basicLower : prevFinalLower;

            double direction = prevDirection;
            if (prevDirection == 1 && close < finalLower)
                direction = -1;
            else if (prevDirection == -1 && close > finalUpper)
                direction = 1;

            _superTrendUp[index] = finalUpper;
            _superTrendDown[index] = finalLower;
            _superTrendDirection[index] = direction;
        }

        private double CalculateVolumeUnits(double riskAmount, double riskPips)
        {
            if (riskPips <= 0) return Symbol.VolumeInUnitsMin;
            double pipValuePerUnit = Symbol.PipValue;
            return riskAmount / (riskPips * pipValuePerUnit);
        }

        private void TriggerCircuitBreaker(double currentDrawdown)
        {
            Print("[CIRCUIT BREAKER ACTIVATED] Daily Drawdown reached {0:F2}%. Flattening and halting.", currentDrawdown);
            CloseAllBotPositions();
        }

        private void CloseAllBotPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == "LuxSuperTrend")
                    ClosePosition(position);
            }
        }

        private void CheckHotReloadConfig()
        {
            try
            {
                if (!System.IO.File.Exists(ConfigFilePath)) return;
                string json = System.IO.File.ReadAllText(ConfigFilePath);
                if (json.Contains("\"emergency_kill_active\": true"))
                {
                    CloseAllBotPositions();
                }
            }
            catch { }
        }
    }
}
