#pragma warning disable CS0618
using System;
using System.IO;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum BotState
    {
        WAITING_FOR_ASIAN_SESSION,
        BUILDING_ASIAN_RANGE,
        SCANNING_LONDON_SWEEP,
        IN_TRADE,
        HALTED_CIRCUIT_BREAKER,
        EMERGENCY_KILL
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        [Parameter("Asian Start Hour (UTC)", DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int AsianStartHour { get; set; }

        [Parameter("Asian End Hour (UTC)", DefaultValue = 6, MinValue = 0, MaxValue = 23)]
        public int AsianEndHour { get; set; }

        [Parameter("London Scan End Hour (UTC)", DefaultValue = 13, MinValue = 7, MaxValue = 23)]
        public int LondonEndHour { get; set; }

        [Parameter("Min Asian Range (Pips)", DefaultValue = 8.0, MinValue = 2.0, MaxValue = 50.0)]
        public double MinAsianRangePips { get; set; }

        [Parameter("Max Asian Range (Pips)", DefaultValue = 60.0, MinValue = 20.0, MaxValue = 150.0)]
        public double MaxAsianRangePips { get; set; }

        [Parameter("SuperTrend ATR Period", DefaultValue = 10, MinValue = 5, MaxValue = 30)]
        public int SuperTrendPeriod { get; set; }

        [Parameter("SuperTrend Multiplier", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0)]
        public double SuperTrendMultiplier { get; set; }

        [Parameter("Enable ATR Trailing Stop", DefaultValue = true)]
        public bool EnableAtrTrailing { get; set; }

        [Parameter("Enable Breakeven at 1R", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Risk Reward Ratio (Target)", DefaultValue = 3.0, MinValue = 1.5, MaxValue = 6.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Invalidation Buffer (Pips)", DefaultValue = 2.0)]
        public double InvalidationBufferPips { get; set; }

        [Parameter("Risk Per Trade %", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 15.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", DefaultValue = 30.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Config File Path", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // State Machine & Indicators
        private BotState _currentState;
        private AverageTrueRange _atr;
        private double _dailyStartingBalance;
        private double _asianHigh;
        private double _asianLow;
        private bool _asianRangeReady;
        private bool _highSwept;
        private bool _lowSwept;
        private double _sweepExtreme;
        private int _currentDay;

        // SuperTrend Internal Series
        private IndicatorDataSeries _superTrendUp;
        private IndicatorDataSeries _superTrendDown;
        private IndicatorDataSeries _superTrendDirection; // 1 = Bullish, -1 = Bearish

        protected override void OnStart()
        {
            _currentState = BotState.WAITING_FOR_ASIAN_SESSION;
            _dailyStartingBalance = Account.Balance;
            _atr = Indicators.AverageTrueRange(Bars, SuperTrendPeriod, MovingAverageType.Simple);

            _superTrendUp = CreateDataSeries();
            _superTrendDown = CreateDataSeries();
            _superTrendDirection = CreateDataSeries();

            _currentDay = Server.Time.DayOfYear;
            _asianHigh = double.MinValue;
            _asianLow = double.MaxValue;

            Print("[cBot Started] London Judas Sweep + SuperTrend ATR initialized on {0} ({1})", BotSymbol, TimeFrame);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            CheckHotReloadConfig();

            if (_currentState == BotState.EMERGENCY_KILL || _currentState == BotState.HALTED_CIRCUIT_BREAKER)
                return;

            // 1. Safety: Daily Drawdown Circuit Breaker Guard
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double drawdownPercent = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (drawdownPercent >= CircuitBreakerDrawdownPercent)
            {
                TriggerCircuitBreaker(drawdownPercent);
                return;
            }

            // 2. Active Trade Management: Breakeven & Dynamic ATR Trailing Stop
            var activePosition = Positions.Find("JudasSuperTrend", SymbolName);
            if (activePosition != null)
            {
                _currentState = BotState.IN_TRADE;
                ManageTrailingStop(activePosition);
            }
            else if (_currentState == BotState.IN_TRADE)
            {
                _currentState = BotState.SCANNING_LONDON_SWEEP;
            }
        }

        protected override void OnBar()
        {
            if (_currentState == BotState.EMERGENCY_KILL || _currentState == BotState.HALTED_CIRCUIT_BREAKER)
                return;

            // Calculate SuperTrend Value on this Bar
            UpdateSuperTrend(Bars.Count - 1);

            DateTime now = Server.Time;

            // Reset Range at New Trading Day
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _asianHigh = double.MinValue;
                _asianLow = double.MaxValue;
                _asianRangeReady = false;
                _highSwept = false;
                _lowSwept = false;
                _dailyStartingBalance = Account.Balance;
                _currentState = BotState.WAITING_FOR_ASIAN_SESSION;
            }

            // Phase 1: Build Asian Session Range (00:00 - 06:00 UTC)
            if (now.Hour >= AsianStartHour && now.Hour < AsianEndHour)
            {
                _currentState = BotState.BUILDING_ASIAN_RANGE;
                var lastBar = Bars.Last(1);
                if (lastBar.High > _asianHigh) _asianHigh = lastBar.High;
                if (lastBar.Low < _asianLow) _asianLow = lastBar.Low;
                return;
            }

            // Phase 2: Finalize Asian Range at 06:00 UTC
            if (now.Hour >= AsianEndHour && !_asianRangeReady)
            {
                if (_asianHigh > double.MinValue && _asianLow < double.MaxValue)
                {
                    double rangePips = (_asianHigh - _asianLow) / Symbol.PipSize;
                    if (rangePips >= MinAsianRangePips && rangePips <= MaxAsianRangePips)
                    {
                        _asianRangeReady = true;
                        _currentState = BotState.SCANNING_LONDON_SWEEP;
                        Print("[Asian Range Set] High: {0:F5} | Low: {1:F5} | Range: {2:F1} pips", _asianHigh, _asianLow, rangePips);
                    }
                    else
                    {
                        Print("[Asian Range Discarded] Range {0:F1} pips outside allowed bounds ({1}-{2} pips)", rangePips, MinAsianRangePips, MaxAsianRangePips);
                        return;
                    }
                }
            }

            // Phase 3: Scan London Judas Sweeps & SuperTrend Trigger (07:00 - 13:00 UTC)
            if (_asianRangeReady && now.Hour >= AsianEndHour && now.Hour < LondonEndHour && Positions.Count == 0)
            {
                var bar = Bars.Last(1);
                double superTrendDir = _superTrendDirection.Last(1);

                // --- BEARISH SCENARIO: Asian High Swept & SuperTrend confirms Bearish ---
                if (!_highSwept && bar.High > _asianHigh)
                {
                    _highSwept = true;
                    _sweepExtreme = bar.High;
                    Print("[Judas Sweep] Asian High pierced at {0:F5}. Watching for rejection...", _sweepExtreme);
                }

                if (_highSwept && bar.Close < _asianHigh && superTrendDir == -1)
                {
                    double stopLoss = Math.Max(_sweepExtreme, bar.High) + (InvalidationBufferPips * Symbol.PipSize);
                    double entryPrice = bar.Close;
                    double riskPips = Math.Abs(stopLoss - entryPrice) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 3.0;

                    double rewardPips = Math.Max(riskPips * RiskRewardRatio, (_asianHigh - _asianLow) / Symbol.PipSize);
                    ExecuteOrder(TradeType.Sell, entryPrice, stopLoss, riskPips, rewardPips);
                    _highSwept = false;
                    return;
                }

                // --- BULLISH SCENARIO: Asian Low Swept & SuperTrend confirms Bullish ---
                if (!_lowSwept && bar.Low < _asianLow)
                {
                    _lowSwept = true;
                    _sweepExtreme = bar.Low;
                    Print("[Judas Sweep] Asian Low pierced at {0:F5}. Watching for rejection...", _sweepExtreme);
                }

                if (_lowSwept && bar.Close > _asianLow && superTrendDir == 1)
                {
                    double stopLoss = Math.Min(_sweepExtreme, bar.Low) - (InvalidationBufferPips * Symbol.PipSize);
                    double entryPrice = bar.Close;
                    double riskPips = Math.Abs(entryPrice - stopLoss) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 3.0;

                    double rewardPips = Math.Max(riskPips * RiskRewardRatio, (_asianHigh - _asianLow) / Symbol.PipSize);
                    ExecuteOrder(TradeType.Buy, entryPrice, stopLoss, riskPips, rewardPips);
                    _lowSwept = false;
                    return;
                }
            }
        }

        private void ExecuteOrder(TradeType tradeType, double entryPrice, double stopLossPrice, double riskPips, double rewardPips)
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

            double takeProfitPrice = tradeType == TradeType.Buy 
                ? entryPrice + (rewardPips * Symbol.PipSize) 
                : entryPrice - (rewardPips * Symbol.PipSize);

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "JudasSuperTrend", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _currentState = BotState.IN_TRADE;
                Print("[cBot ENTRY] {0} {1} units at {2:F5} | SL: {3:F5} ({4:F1} pips) | Target TP: {5:F5}", 
                    tradeType, volumeInUnits, entryPrice, stopLossPrice, riskPips, takeProfitPrice);
            }
        }

        private void ManageTrailingStop(Position position)
        {
            if (!position.StopLoss.HasValue) return;

            double entryPrice = position.EntryPrice;
            double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            double initialRiskPips = Math.Abs(entryPrice - position.StopLoss.Value) / Symbol.PipSize;

            if (initialRiskPips <= 0) return;

            double currentGainPips = position.TradeType == TradeType.Buy 
                ? (currentPrice - entryPrice) / Symbol.PipSize 
                : (entryPrice - currentPrice) / Symbol.PipSize;

            // 1. Move to Breakeven at +1.0R
            if (EnableBreakeven && currentGainPips >= initialRiskPips)
            {
                double bePrice = position.TradeType == TradeType.Buy 
                    ? entryPrice + (0.5 * Symbol.PipSize) 
                    : entryPrice - (0.5 * Symbol.PipSize);

                bool shouldSetBe = position.TradeType == TradeType.Buy 
                    ? position.StopLoss.Value < bePrice 
                    : position.StopLoss.Value > bePrice;

                if (shouldSetBe)
                {
                    ModifyPosition(position, bePrice, position.TakeProfit);
                    Print("[Breakeven Locked] Stop Loss moved to {0:F5}", bePrice);
                }
            }

            // 2. Dynamic ATR / SuperTrend Trailing Stop
            if (EnableAtrTrailing && currentGainPips >= (1.5 * initialRiskPips))
            {
                double atrValue = _atr.Result.Last(1);
                double trailDistance = SuperTrendMultiplier * atrValue;

                double newStopLoss = position.TradeType == TradeType.Buy 
                    ? currentPrice - trailDistance 
                    : currentPrice + trailDistance;

                bool isTighter = position.TradeType == TradeType.Buy 
                    ? newStopLoss > position.StopLoss.Value 
                    : newStopLoss < position.StopLoss.Value;

                if (isTighter)
                {
                    ModifyPosition(position, newStopLoss, position.TakeProfit);
                }
            }
        }

        private void UpdateSuperTrend(int index)
        {
            if (index < SuperTrendPeriod) return;

            double high = Bars.HighPrices[index];
            double low = Bars.LowPrices[index];
            double close = Bars.ClosePrices[index];
            double prevClose = Bars.ClosePrices[index - 1];
            double atr = _atr.Result[index];

            double basicUpper = ((high + low) / 2.0) + (SuperTrendMultiplier * atr);
            double basicLower = ((high + low) / 2.0) - (SuperTrendMultiplier * atr);

            double prevFinalUpper = _superTrendUp[index - 1];
            double prevFinalLower = _superTrendDown[index - 1];
            double prevDirection = _superTrendDirection[index - 1] != 0 ? _superTrendDirection[index - 1] : 1;

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
            _currentState = BotState.HALTED_CIRCUIT_BREAKER;
            Print("[CIRCUIT BREAKER] Daily Drawdown {0:F2}% reached limit. Halting robot.", currentDrawdown);
            CancelAllOrdersAndPositions();
        }

        private void TriggerEmergencyKill()
        {
            _currentState = BotState.EMERGENCY_KILL;
            Print("[EMERGENCY KILL] Immediate halt requested. Purging all orders.");
            CancelAllOrdersAndPositions();
        }

        private void CancelAllOrdersAndPositions()
        {
            foreach (var order in PendingOrders)
            {
                if (order.Label == "JudasSuperTrend")
                    CancelPendingOrder(order);
            }
            foreach (var position in Positions)
            {
                if (position.Label == "JudasSuperTrend")
                    ClosePosition(position);
            }
        }

        private void CheckHotReloadConfig()
        {
            try
            {
                if (!System.IO.File.Exists(ConfigFilePath)) return;
                string json = System.IO.File.ReadAllText(ConfigFilePath);
                if (json.Contains("\"emergency_kill_active\": true") && _currentState != BotState.EMERGENCY_KILL)
                {
                    TriggerEmergencyKill();
                }
                else if (json.Contains("\"emergency_kill_active\": false") && _currentState == BotState.EMERGENCY_KILL)
                {
                    _currentState = BotState.SCANNING_LONDON_SWEEP;
                    Print("[cBot Resumed] Emergency lock cleared.");
                }
            }
            catch { }
        }
    }
}
