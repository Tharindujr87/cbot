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
        WAITING_FOR_MSS,
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

        [Parameter("Session Start UTC", DefaultValue = 12)]
        public int SessionStartUtc { get; set; }

        [Parameter("Session End UTC", DefaultValue = 17)]
        public int SessionEndUtc { get; set; }

        [Parameter("Displacement ATR Mult", DefaultValue = 1.8)]
        public double DisplacementAtrMult { get; set; }

        [Parameter("Risk Reward Ratio", DefaultValue = 3.5)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Invalidation Buffer (Pips)", DefaultValue = 1.5)]
        public double InvalidationBufferPips { get; set; }

        [Parameter("Max Pending Bars", DefaultValue = 8)]
        public int MaxPendingBars { get; set; }

        [Parameter("Risk Per Trade %", DefaultValue = 15.0)]
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

        protected override void OnStart()
        {
            _currentState = BotState.WAITING_FOR_SWEEP;
            _dailyStartingBalance = Account.Balance;
            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _atr1M = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);

            Print("[cBot Started] Initialized Liquidity Sweep & MSS Execution Robot for {0}", BotSymbol);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            // 1. Check Hot Reload Strategy Config & Emergency Kill
            CheckHotReloadConfig();

            if (_currentState == BotState.EMERGENCY_KILL || _currentState == BotState.HALTED_CIRCUIT_BREAKER)
                return;

            // 2. Safety: 30% Drawdown Circuit Breaker Guard
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double drawdownPercent = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (drawdownPercent >= CircuitBreakerDrawdownPercent)
            {
                TriggerCircuitBreaker(drawdownPercent);
                return;
            }

            // 3. Time-of-Day Filter (London/NY overlap)
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

            // 4. Update In-Trade State
            var activePosition = Positions.Find("SweepMssBot", SymbolName);
            if (activePosition != null)
            {
                _currentState = BotState.IN_TRADE;
                return;
            }
            else if (_currentState == BotState.IN_TRADE)
            {
                _currentState = BotState.WAITING_FOR_SWEEP;
            }

            // 5. Manage Pending FVG Limit Order Expiration
            if (_currentState == BotState.FVG_ORDER_PENDING)
            {
                _fvgPendingBarCount++;
                if (_fvgPendingBarCount > MaxPendingBars * 60) // Ticks/bars threshold
                {
                    CancelAllPendingLimitOrders();
                    _currentState = BotState.WAITING_FOR_SWEEP;
                    Print("[cBot Expiration] Pending FVG Limit Order expired after {0} bars.", MaxPendingBars);
                }
            }
        }

        protected override void OnBar()
        {
            if (_currentState == BotState.EMERGENCY_KILL || _currentState == BotState.HALTED_CIRCUIT_BREAKER)
                return;

            int currentHourUtc = Server.Time.Hour;
            if (currentHourUtc < SessionStartUtc || currentHourUtc >= SessionEndUtc)
                return;

            // Step A: Check 15M High/Low Liquidity Sweep
            if (_currentState == BotState.WAITING_FOR_SWEEP)
            {
                if (_bars15M.Count < 20) return;
                
                double recent15MHigh = _bars15M.HighPrices.Maximum(15);
                double recent15MLow = _bars15M.LowPrices.Minimum(15);

                // Bearish Sweep: 15M High pierced then rejected
                if (Bars.Last(1).High > recent15MHigh && Bars.Last(1).Close < recent15MHigh)
                {
                    _currentState = BotState.SWEEP_DETECTED_BEARISH;
                    _sweepLevel = recent15MHigh;
                    _mssLevel = Bars.LowPrices.Minimum(10);
                    Print("[cBot Sweep] 15M High Liquidity Swept at {0}. Looking for Bearish MSS below {1}", _sweepLevel, _mssLevel);
                }
                // Bullish Sweep: 15M Low pierced then rejected
                else if (Bars.Last(1).Low < recent15MLow && Bars.Last(1).Close > recent15MLow)
                {
                    _currentState = BotState.SWEEP_DETECTED_BULLISH;
                    _sweepLevel = recent15MLow;
                    _mssLevel = Bars.HighPrices.Maximum(10);
                    Print("[cBot Sweep] 15M Low Liquidity Swept at {0}. Looking for Bullish MSS above {1}", _sweepLevel, _mssLevel);
                }
            }
            // Step B: Check 1M Market Structure Shift (MSS) with Displacement
            else if (_currentState == BotState.SWEEP_DETECTED_BEARISH || _currentState == BotState.SWEEP_DETECTED_BULLISH)
            {
                double currentAtr = _atr1M.Result.Last(1);
                var lastBar = Bars.Last(1);
                double bodySize = Math.Abs(lastBar.Close - lastBar.Open);

                bool isDisplacement = bodySize >= (DisplacementAtrMult * currentAtr);

                if (_currentState == BotState.SWEEP_DETECTED_BEARISH && lastBar.Close < _mssLevel && isDisplacement)
                {
                    // Bearish MSS confirmed -> Identify Fair Value Gap (FVG)
                    // FVG between High of Bar 3 and Low of Bar 1
                    double fvgUpper = Bars.Last(3).Low;
                    double fvgLower = Bars.Last(1).High;
                    if (fvgUpper > fvgLower)
                    {
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0; // Midpoint FVG
                        _stopLossPrice = _sweepLevel + (InvalidationBufferPips * Symbol.PipSize);
                        double riskPips = (_stopLossPrice - _fvgEntryLevel) / Symbol.PipSize;
                        double rewardPips = riskPips * RiskRewardRatio;
                        _takeProfitPrice = _fvgEntryLevel - (rewardPips * Symbol.PipSize);

                        PlaceFvgLimitOrder(TradeType.Sell, _fvgEntryLevel, riskPips, rewardPips);
                    }
                }
                else if (_currentState == BotState.SWEEP_DETECTED_BULLISH && lastBar.Close > _mssLevel && isDisplacement)
                {
                    // Bullish MSS confirmed -> Identify FVG
                    double fvgLower = Bars.Last(3).High;
                    double fvgUpper = Bars.Last(1).Low;
                    if (fvgUpper > fvgLower)
                    {
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                        _stopLossPrice = _sweepLevel - (InvalidationBufferPips * Symbol.PipSize);
                        double riskPips = (_fvgEntryLevel - _stopLossPrice) / Symbol.PipSize;
                        double rewardPips = riskPips * RiskRewardRatio;
                        _takeProfitPrice = _fvgEntryLevel + (rewardPips * Symbol.PipSize);

                        PlaceFvgLimitOrder(TradeType.Buy, _fvgEntryLevel, riskPips, rewardPips);
                    }
                }
            }
        }

        private void PlaceFvgLimitOrder(TradeType tradeType, double targetPrice, double riskPips, double rewardPips)
        {
            // Safety 1: Spread Guard (Spread <= 1.2 pips)
            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > 1.2)
            {
                Print("[cBot Abort] Spread too wide ({0:F1} pips > 1.2 limit). Aborting order.", spreadPips);
                _currentState = BotState.WAITING_FOR_SWEEP;
                return;
            }

            // Safety 2: Free Margin Buffer (< 85% total account allocation)
            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, riskPips);

            double requiredMargin = volumeInUnits / 30.0; // 1:30 leverage
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                Print("[cBot Margin Guard] Margin requirement {0} exceeds 85% free margin buffer. Scaling lot size down.", requiredMargin);
                volumeInUnits = (Account.FreeMargin * 0.85) * 30.0;
            }

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[cBot Margin Guard] Volume {0} below broker minimum.", volumeInUnits);
                return;
            }

            // Place Limit Order using exact price levels
            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, targetPrice, "SweepMssBot", _stopLossPrice, _takeProfitPrice, null, null, true, true);
            if (result.IsSuccessful)
            {
                _currentState = BotState.FVG_ORDER_PENDING;
                _fvgPendingBarCount = 0;
                Print("[cBot Order Placed] {0} Limit at {1}, SL: {2}, TP: {3}, Vol: {4}", tradeType, targetPrice, _stopLossPrice, _takeProfitPrice, volumeInUnits);
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
            Print("[CIRCUIT BREAKER ACTIVATED] Daily Drawdown reached {0:F2}% (Limit: {1}%). Halting robot.", currentDrawdown, CircuitBreakerDrawdownPercent);
            CancelAllPendingLimitOrders();
            FlattenAllOpenPositions();
        }

        private void TriggerEmergencyKill()
        {
            _currentState = BotState.EMERGENCY_KILL;
            Print("[EMERGENCY KILL SWITCH] Immediate halt requested. Purging orders and closing positions.");
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
                // Parsing for emergency kill status
                if (json.Contains("\"emergency_kill_active\": true") && _currentState != BotState.EMERGENCY_KILL)
                {
                    TriggerEmergencyKill();
                }
                else if (json.Contains("\"emergency_kill_active\": false") && _currentState == BotState.EMERGENCY_KILL)
                {
                    _currentState = BotState.WAITING_FOR_SWEEP;
                    Print("[cBot Resumed] Emergency lock cleared. Back to active standby.");
                }
            }
            catch { }
        }
    }
}
