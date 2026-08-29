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

        [Parameter("Enable EMA Trend Filter", DefaultValue = false)]
        public bool EnableEmaFilter { get; set; }

        [Parameter("EMA Trend Period", DefaultValue = 200, MinValue = 20, MaxValue = 500)]
        public int EmaPeriod { get; set; }

        [Parameter("Enable RSI Filter", DefaultValue = false)]
        public bool EnableRsiFilter { get; set; }

        [Parameter("RSI Period", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Long Threshold", DefaultValue = 50.0)]
        public double RsiLongThreshold { get; set; }

        [Parameter("RSI Short Threshold", DefaultValue = 50.0)]
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

        // Indicators & State
        private AverageTrueRange _atr;
        private ExponentialMovingAverage _ema;
        private RelativeStrengthIndex _rsi;
        private double _dailyStartingBalance;
        private int _currentDay;

        // Clean SuperTrend State Tracker
        private double _prevUpper;
        private double _prevLower;
        private int _currentDirection; // 1 = Bullish, -1 = Bearish
        private double _currentStopLoss;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;

            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);

            _prevUpper = double.MaxValue;
            _prevLower = double.MinValue;
            _currentDirection = 1;

            Print("[LuxAlgo SuperTrend Bot Started] Initialized on {0} ({1}) | ATR: {2}, Mult: {3}", 
                SymbolName, TimeFrame, AtrPeriod, SuperTrendMultiplier);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            CheckHotReloadConfig();

            // Daily Drawdown Circuit Breaker Guard
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double drawdownPercent = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (drawdownPercent >= CircuitBreakerDrawdownPercent)
            {
                TriggerCircuitBreaker(drawdownPercent);
                return;
            }

            // Continuous Trailing Stop Update
            var position = Positions.Find("LuxSuperTrend", SymbolName);
            if (position != null)
            {
                ManageSuperTrendTrailingStop(position);
            }
        }

        protected override void OnBar()
        {
            if (Bars.Count < Math.Max(AtrPeriod, Math.Max(EmaPeriod, RsiPeriod)) + 5)
                return;

            DateTime now = Server.Time;
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _dailyStartingBalance = Account.Balance;
            }

            // Calculate SuperTrend dynamically on completed bar Last(1)
            double high = Bars.Last(1).High;
            double low = Bars.Last(1).Low;
            double close = Bars.Last(1).Close;
            double prevClose = Bars.Last(2).Close;
            double atr = _atr.Result.Last(1);

            double basicUpper = ((high + low) / 2.0) + (SuperTrendMultiplier * atr);
            double basicLower = ((high + low) / 2.0) - (SuperTrendMultiplier * atr);

            double finalUpper = (basicUpper < _prevUpper || prevClose > _prevUpper) ? basicUpper : _prevUpper;
            double finalLower = (basicLower > _prevLower || prevClose < _prevLower) ? basicLower : _prevLower;

            int prevDir = _currentDirection;
            int newDir = prevDir;

            if (prevDir == 1 && close < finalLower)
                newDir = -1;
            else if (prevDir == -1 && close > finalUpper)
                newDir = 1;

            _prevUpper = finalUpper;
            _prevLower = finalLower;
            _currentDirection = newDir;
            _currentStopLoss = newDir == 1 ? finalLower : finalUpper;

            var activePosition = Positions.Find("LuxSuperTrend", SymbolName);

            // --- BULLISH SIGNAL: Direction flipped from Bearish (-1) to Bullish (+1) ---
            if (prevDir == -1 && newDir == 1)
            {
                if (activePosition != null && activePosition.TradeType == TradeType.Sell)
                {
                    ClosePosition(activePosition);
                }

                bool emaPass = !EnableEmaFilter || (close > _ema.Result.Last(1));
                bool rsiPass = !EnableRsiFilter || (_rsi.Result.Last(1) >= RsiLongThreshold);

                if (emaPass && rsiPass && Positions.Count == 0)
                {
                    double riskPips = (close - finalLower) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = (atr * SuperTrendMultiplier) / Symbol.PipSize;

                    double? takeProfit = EnableFixedTp ? (double?)(close + (riskPips * RiskRewardRatio * Symbol.PipSize)) : null;
                    ExecuteTrade(TradeType.Buy, close, finalLower, riskPips, takeProfit);
                }
            }
            // --- BEARISH SIGNAL: Direction flipped from Bullish (+1) to Bearish (-1) ---
            else if (prevDir == 1 && newDir == -1)
            {
                if (activePosition != null && activePosition.TradeType == TradeType.Buy)
                {
                    ClosePosition(activePosition);
                }

                bool emaPass = !EnableEmaFilter || (close < _ema.Result.Last(1));
                bool rsiPass = !EnableRsiFilter || (_rsi.Result.Last(1) <= RsiShortThreshold);

                if (emaPass && rsiPass && Positions.Count == 0)
                {
                    double riskPips = (finalUpper - close) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = (atr * SuperTrendMultiplier) / Symbol.PipSize;

                    double? takeProfit = EnableFixedTp ? (double?)(close - (riskPips * RiskRewardRatio * Symbol.PipSize)) : null;
                    ExecuteTrade(TradeType.Sell, close, finalUpper, riskPips, takeProfit);
                }
            }
        }

        private void ExecuteTrade(TradeType tradeType, double entryPrice, double stopPrice, double riskPips, double? takeProfitPrice)
        {
            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, riskPips);

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
            if (position.TradeType == TradeType.Buy)
            {
                double trailingStopPrice = _prevLower;
                if (trailingStopPrice > 0)
                {
                    if (!position.StopLoss.HasValue || trailingStopPrice > position.StopLoss.Value)
                    {
                        ModifyPosition(position, trailingStopPrice, position.TakeProfit);
                    }
                }
            }
            else if (position.TradeType == TradeType.Sell)
            {
                double trailingStopPrice = _prevUpper;
                if (trailingStopPrice > 0)
                {
                    if (!position.StopLoss.HasValue || trailingStopPrice < position.StopLoss.Value)
                    {
                        ModifyPosition(position, trailingStopPrice, position.TakeProfit);
                    }
                }
            }
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
