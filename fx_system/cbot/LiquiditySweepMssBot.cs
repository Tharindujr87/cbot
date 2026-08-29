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

        [Parameter("SuperTrend ATR Period", DefaultValue = 10, MinValue = 3, MaxValue = 50)]
        public int AtrPeriod { get; set; }

        [Parameter("SuperTrend Multiplier", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 6.0)]
        public double SuperTrendMultiplier { get; set; }

        [Parameter("Enable EMA 200 Trend Filter", DefaultValue = false)]
        public bool EnableEmaFilter { get; set; }

        [Parameter("EMA Period", DefaultValue = 200, MinValue = 20, MaxValue = 500)]
        public int EmaPeriod { get; set; }

        [Parameter("Enable Fixed Take Profit", DefaultValue = false)]
        public bool EnableFixedTp { get; set; }

        [Parameter("Take Profit (R:R)", DefaultValue = 3.0, MinValue = 1.5, MaxValue = 6.0)]
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
        private double _dailyStartingBalance;
        private int _currentDay;

        // SuperTrend State Variables
        private double _prevUpper;
        private double _prevLower;
        private int _prevDirection; // 1 = Bullish (Green), -1 = Bearish (Red)
        private bool _isInitialized;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;

            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);

            _prevUpper = 0;
            _prevLower = 0;
            _prevDirection = 1;
            _isInitialized = false;

            Print("[LuxAlgo SuperTrend Bot] Initialized on {0} ({1})", SymbolName, TimeFrame);
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
            if (Bars.Count < Math.Max(AtrPeriod, 15) + 5)
                return;

            DateTime now = Server.Time;
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _dailyStartingBalance = Account.Balance;
            }

            double high = Bars.Last(1).High;
            double low = Bars.Last(1).Low;
            double close = Bars.Last(1).Close;
            double prevClose = Bars.Last(2).Close;
            double atr = _atr.Result.Last(1);

            double hl2 = (high + low) / 2.0;
            double basicUpper = hl2 + (SuperTrendMultiplier * atr);
            double basicLower = hl2 - (SuperTrendMultiplier * atr);

            if (!_isInitialized)
            {
                _prevUpper = basicUpper;
                _prevLower = basicLower;
                _prevDirection = close > basicUpper ? 1 : -1;
                _isInitialized = true;
                return;
            }

            // Standard SuperTrend Band Ratcheting
            double finalUpper = (basicUpper < _prevUpper || prevClose > _prevUpper) ? basicUpper : _prevUpper;
            double finalLower = (basicLower > _prevLower || prevClose < _prevLower) ? basicLower : _prevLower;

            int oldDirection = _prevDirection;
            int newDirection = oldDirection;

            // Trend Reversal Logic with Band Reset
            if (oldDirection == -1 && close > _prevUpper)
            {
                newDirection = 1;
                finalLower = basicLower;
            }
            else if (oldDirection == 1 && close < _prevLower)
            {
                newDirection = -1;
                finalUpper = basicUpper;
            }

            _prevUpper = finalUpper;
            _prevLower = finalLower;
            _prevDirection = newDirection;

            var activePosition = Positions.Find("LuxSuperTrend", SymbolName);

            // --- BUY SIGNAL: SuperTrend flipped from Bearish (-1) to Bullish (+1) ---
            if (oldDirection == -1 && newDirection == 1)
            {
                if (activePosition != null && activePosition.TradeType == TradeType.Sell)
                {
                    ClosePosition(activePosition);
                }

                bool emaPass = !EnableEmaFilter || (close > _ema.Result.Last(1));

                if (emaPass && Positions.Count == 0)
                {
                    double riskPips = (close - finalLower) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = (atr * SuperTrendMultiplier) / Symbol.PipSize;
                    if (riskPips < 3.0) riskPips = 3.0;

                    double? takeProfitPips = EnableFixedTp ? (double?)(riskPips * RiskRewardRatio) : null;
                    ExecuteTrade(TradeType.Buy, close, finalLower, riskPips, takeProfitPips);
                }
            }
            // --- SELL SIGNAL: SuperTrend flipped from Bullish (+1) to Bearish (-1) ---
            else if (oldDirection == 1 && newDirection == -1)
            {
                if (activePosition != null && activePosition.TradeType == TradeType.Buy)
                {
                    ClosePosition(activePosition);
                }

                bool emaPass = !EnableEmaFilter || (close < _ema.Result.Last(1));

                if (emaPass && Positions.Count == 0)
                {
                    double riskPips = (finalUpper - close) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = (atr * SuperTrendMultiplier) / Symbol.PipSize;
                    if (riskPips < 3.0) riskPips = 3.0;

                    double? takeProfitPips = EnableFixedTp ? (double?)(riskPips * RiskRewardRatio) : null;
                    ExecuteTrade(TradeType.Sell, close, finalUpper, riskPips, takeProfitPips);
                }
            }
        }

        private void ExecuteTrade(TradeType tradeType, double entryPrice, double stopPrice, double riskPips, double? takeProfitPips)
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

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "LuxSuperTrend", riskPips, takeProfitPips);
            if (result.IsSuccessful)
            {
                Print("[SuperTrend ENTRY] {0} {1} units @ {2:F5} | SL: {3:F5} ({4:F1} pips)", 
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
            Print("[CIRCUIT BREAKER] Daily Drawdown reached {0:F2}%. Halting.", currentDrawdown);
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
