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
        WAITING_FOR_SWEEP,
        SWEEP_DETECTED_BULLISH,
        SWEEP_DETECTED_BEARISH,
        FVG_ORDER_PENDING,
        IN_TRADE,
        HALTED_CIRCUIT_BREAKER,
        EMERGENCY_KILL
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        [Parameter("Use Session Filter", DefaultValue = false)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start UTC", DefaultValue = 7)]
        public int SessionStartUtc { get; set; }

        [Parameter("Session End UTC", DefaultValue = 18)]
        public int SessionEndUtc { get; set; }

        [Parameter("Displacement ATR Mult", DefaultValue = 1.2)]
        public double DisplacementAtrMult { get; set; }

        [Parameter("Risk Reward Ratio", DefaultValue = 3.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Invalidation Buffer (Pips)", DefaultValue = 1.5)]
        public double InvalidationBufferPips { get; set; }

        [Parameter("Max Pending Bars (1M)", DefaultValue = 15)]
        public int MaxPendingBars { get; set; }

        [Parameter("Risk Per Trade %", DefaultValue = 2.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", DefaultValue = 30.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Config File Path", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // State Machine & Indicators
        private BotState _currentState;
        private Bars _bars15M;
        private AverageTrueRange _atr1M;
        private double _dailyStartingBalance;
        private double _sweepLevel;
        private double _mssLevel;
        private double _fvgEntryLevel;
        private double _stopLossPrice;
        private double _takeProfitPrice;
        private int _fvgPendingBarCount;
        private int _sweepBarIndex;

        protected override void OnStart()
        {
            _currentState = BotState.WAITING_FOR_SWEEP;
            _dailyStartingBalance = Account.Balance;
            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _atr1M = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);

            Print("[cBot Started] Liquidity Sweep & MSS Robot initialized for {0}", BotSymbol);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            CheckHotReloadConfig();

            if (_currentState == BotState.EMERGENCY_KILL || _currentState == BotState.HALTED_CIRCUIT_BREAKER)
                return;

            // Daily Drawdown Circuit Breaker Guard
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double drawdownPercent = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (drawdownPercent >= CircuitBreakerDrawdownPercent)
            {
                TriggerCircuitBreaker(drawdownPercent);
                return;
            }

            // Update In-Trade State
            var activePosition = Positions.Find("SweepMssBot", SymbolName);
            if (activePosition != null)
            {
                _currentState = BotState.IN_TRADE;
            }
            else if (_currentState == BotState.IN_TRADE)
            {
                _currentState = BotState.WAITING_FOR_SWEEP;
            }
        }

        protected override void OnBar()
        {
            if (_currentState == BotState.EMERGENCY_KILL || _currentState == BotState.HALTED_CIRCUIT_BREAKER)
                return;

            // Session Filter (if enabled)
            if (UseSessionFilter)
            {
                int currentHourUtc = Server.Time.Hour;
                if (currentHourUtc < SessionStartUtc || currentHourUtc >= SessionEndUtc)
                {
                    if (Positions.Count == 0 && _currentState != BotState.WAITING_FOR_SWEEP)
                    {
                        CancelAllPendingLimitOrders();
                        _currentState = BotState.WAITING_FOR_SWEEP;
                    }
                    return;
                }
            }

            // 1. Manage Pending Order Expiration per completed 1M bar
            if (_currentState == BotState.FVG_ORDER_PENDING)
            {
                _fvgPendingBarCount++;
                if (_fvgPendingBarCount >= MaxPendingBars)
                {
                    CancelAllPendingLimitOrders();
                    _currentState = BotState.WAITING_FOR_SWEEP;
                    Print("[cBot Expiration] FVG Limit Order expired after {0} 1M bars.", MaxPendingBars);
                }
                return;
            }

            // 2. Timeout sweep if no MSS occurs within 20 bars (20 minutes)
            if (_currentState == BotState.SWEEP_DETECTED_BEARISH || _currentState == BotState.SWEEP_DETECTED_BULLISH)
            {
                if (Bars.Count - _sweepBarIndex > 20)
                {
                    _currentState = BotState.WAITING_FOR_SWEEP;
                    Print("[cBot Timeout] Sweep expired without confirmed MSS displacement. Resetting.");
                }
            }

            // Step A: Check 15M High/Low Liquidity Sweep
            if (_currentState == BotState.WAITING_FOR_SWEEP)
            {
                if (_bars15M.Count < 20 || Bars.Count < 20) return;

                // Calculate Swing High/Low over PRIOR completed 15M bars (excluding current bar)
                double recent15MHigh = double.MinValue;
                double recent15MLow = double.MaxValue;
                int lookback = Math.Min(20, _bars15M.Count - 1);
                for (int i = 1; i <= lookback; i++)
                {
                    if (_bars15M.HighPrices.Last(i) > recent15MHigh) recent15MHigh = _bars15M.HighPrices.Last(i);
                    if (_bars15M.LowPrices.Last(i) < recent15MLow) recent15MLow = _bars15M.LowPrices.Last(i);
                }

                // Bearish Sweep: 1M bar pierced prior 15M High and rejected back below
                if (Bars.Last(1).High > recent15MHigh && Bars.Last(1).Close < recent15MHigh)
                {
                    _currentState = BotState.SWEEP_DETECTED_BEARISH;
                    _sweepLevel = recent15MHigh;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.LowPrices.Minimum(10);
                    Print("[cBot Sweep] 15M High ({0}) Swept. Looking for Bearish MSS below {1}", _sweepLevel, _mssLevel);
                }
                // Bullish Sweep: 1M bar pierced prior 15M Low and rejected back above
                else if (Bars.Last(1).Low < recent15MLow && Bars.Last(1).Close > recent15MLow)
                {
                    _currentState = BotState.SWEEP_DETECTED_BULLISH;
                    _sweepLevel = recent15MLow;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.HighPrices.Maximum(10);
                    Print("[cBot Sweep] 15M Low ({0}) Swept. Looking for Bullish MSS above {1}", _sweepLevel, _mssLevel);
                }
            }
            // Step B: Check 1M Market Structure Shift (MSS) with Displacement
            else if (_currentState == BotState.SWEEP_DETECTED_BEARISH || _currentState == BotState.SWEEP_DETECTED_BULLISH)
            {
                double currentAtr = _atr1M.Result.Last(1);
                var lastBar = Bars.Last(1);
                double bodySize = Math.Abs(lastBar.Close - lastBar.Open);
                bool isDisplacement = bodySize >= (DisplacementAtrMult * currentAtr);

                // Bearish MSS
                if (_currentState == BotState.SWEEP_DETECTED_BEARISH && lastBar.Close < _mssLevel && isDisplacement)
                {
                    // Look for Fair Value Gap in last 3 bars
                    double fvgUpper = Bars.Last(3).Low;
                    double fvgLower = Bars.Last(1).High;
                    if (fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = (lastBar.Open + lastBar.Close) / 2.0; // 50% displacement retracement

                    _stopLossPrice = _sweepLevel + (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_stopLossPrice - _fvgEntryLevel) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;
                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel - (rewardPips * Symbol.PipSize);

                    PlaceFvgLimitOrder(TradeType.Sell, _fvgEntryLevel, riskPips, rewardPips);
                }
                // Bullish MSS
                else if (_currentState == BotState.SWEEP_DETECTED_BULLISH && lastBar.Close > _mssLevel && isDisplacement)
                {
                    double fvgLower = Bars.Last(3).High;
                    double fvgUpper = Bars.Last(1).Low;
                    if (fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = (lastBar.Open + lastBar.Close) / 2.0;

                    _stopLossPrice = _sweepLevel - (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_fvgEntryLevel - _stopLossPrice) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;
                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel + (rewardPips * Symbol.PipSize);

                    PlaceFvgLimitOrder(TradeType.Buy, _fvgEntryLevel, riskPips, rewardPips);
                }
            }
        }

        private void PlaceFvgLimitOrder(TradeType tradeType, double targetPrice, double riskPips, double rewardPips)
        {
            // Safety 1: Free Margin Buffer
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

            // Place Limit Order
            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, targetPrice, "SweepMssBot", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _currentState = BotState.FVG_ORDER_PENDING;
                _fvgPendingBarCount = 0;
                Print("[cBot Order Placed] {0} Limit at {1}, SL: {2:F5}, TP: {3:F5}, Vol: {4}", tradeType, targetPrice, _stopLossPrice, _takeProfitPrice, volumeInUnits);
            }
            else
            {
                Print("[cBot Order Error] Failed to place limit order: {0}", result.Error);
                _currentState = BotState.WAITING_FOR_SWEEP;
            }
        }

        private double CalculateVolumeUnits(double riskAmount, double riskPips)
        {
            if (riskPips <= 0) return Symbol.VolumeInUnitsMin;
            double pipValuePerUnit = Symbol.PipValue;
            double units = riskAmount / (riskPips * pipValuePerUnit);
            return units;
        }

        private void TriggerCircuitBreaker(double currentDrawdown)
        {
            _currentState = BotState.HALTED_CIRCUIT_BREAKER;
            Print("[CIRCUIT BREAKER] Daily Drawdown {0:F2}% reached limit. Halting robot.", currentDrawdown);
            CancelAllPendingLimitOrders();
            FlattenAllOpenPositions();
        }

        private void TriggerEmergencyKill()
        {
            _currentState = BotState.EMERGENCY_KILL;
            Print("[EMERGENCY KILL] Immediate halt requested. Purging orders.");
            CancelAllPendingLimitOrders();
            FlattenAllOpenPositions();
        }

        private void CancelAllPendingLimitOrders()
        {
            foreach (var order in PendingOrders)
            {
                if (order.Label == "SweepMssBot")
                    CancelPendingOrder(order);
            }
        }

        private void FlattenAllOpenPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == "SweepMssBot")
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
                    _currentState = BotState.WAITING_FOR_SWEEP;
                    Print("[cBot Resumed] Emergency lock cleared.");
                }
            }
            catch { }
        }
    }
}
