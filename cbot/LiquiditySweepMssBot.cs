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

    public enum FvgEntryType
    {
        Midpoint_50,
        Proximal_Edge,
        Market_On_MSS
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        // --- Session Filter ---
        [Parameter("Use Session Filter", Group = "Session Clocks", DefaultValue = false)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start UTC", Group = "Session Clocks", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int SessionStartUtc { get; set; }

        [Parameter("Session End UTC", Group = "Session Clocks", DefaultValue = 18, MinValue = 0, MaxValue = 23)]
        public int SessionEndUtc { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Session Clocks", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        // --- Strategy Parameters ---
        [Parameter("Displacement ATR Mult", Group = "SMC Strategy", DefaultValue = 0.6, MinValue = 0.3, MaxValue = 3.0)]
        public double DisplacementAtrMult { get; set; }

        [Parameter("Risk Reward Ratio", Group = "SMC Strategy", DefaultValue = 6.5, MinValue = 1.5, MaxValue = 15.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Invalidation Buffer (Pips)", Group = "SMC Strategy", DefaultValue = 4.5, MinValue = 0.5, MaxValue = 10.0)]
        public double InvalidationBufferPips { get; set; }

        [Parameter("Max Pending Bars", Group = "SMC Strategy", DefaultValue = 16, MinValue = 3, MaxValue = 50)]
        public int MaxPendingBars { get; set; }

        [Parameter("FVG Entry Mode", Group = "SMC Strategy", DefaultValue = FvgEntryType.Midpoint_50)]
        public FvgEntryType EntryMode { get; set; }

        [Parameter("15M Lookback Bars", Group = "SMC Strategy", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int LookbackBars15M { get; set; }

        // --- Trade Management & Protection ---
        [Parameter("Enable Breakeven Lock", Group = "Trade Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Breakeven Trigger (+R)", Group = "Trade Management", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Breakeven Lock Offset (Pips)", Group = "Trade Management", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 3.0)]
        public double BreakevenOffsetPips { get; set; }

        [Parameter("Enable Partial Take Profit", Group = "Trade Management", DefaultValue = false)]
        public bool EnablePartialTp { get; set; }

        [Parameter("Partial TP Trigger (+R)", Group = "Trade Management", DefaultValue = 3.0, MinValue = 1.5, MaxValue = 5.0)]
        public double PartialTpTriggerR { get; set; }

        [Parameter("Partial TP Volume %", Group = "Trade Management", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double PartialTpPercent { get; set; }

        // --- Risk & Circuit Breaker ---
        [Parameter("Risk Per Trade %", Group = "Risk Controls", DefaultValue = 2.5, MinValue = 0.1, MaxValue = 30.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", Group = "Risk Controls", DefaultValue = 20.0, MinValue = 5.0, MaxValue = 50.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Config File Path", Group = "Risk Controls", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // State Machine & Indicators
        private BotState _currentState;
        private Bars _bars15M;
        private AverageTrueRange _atr;
        private double _dailyStartingBalance;
        private double _sweepLevel;
        private double _mssLevel;
        private double _fvgEntryLevel;
        private double _stopLossPrice;
        private double _takeProfitPrice;
        private int _fvgPendingBarCount;
        private int _sweepBarIndex;
        private bool _partialTpTaken;

        protected override void OnStart()
        {
            _currentState = BotState.WAITING_FOR_SWEEP;
            _dailyStartingBalance = Account.Balance;
            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _atr = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);
            _partialTpTaken = false;

            Print("[cBot Started] Upgraded Liquidity Sweep & MSS Robot initialized for {0} on {1}", BotSymbol, TimeFrame);
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

            // 2. Active Trade Management: Breakeven & Partial TP
            var activePosition = Positions.Find("SweepMssBot", SymbolName);
            if (activePosition != null)
            {
                _currentState = BotState.IN_TRADE;
                ManageOpenPosition(activePosition);
            }
            else if (_currentState == BotState.IN_TRADE)
            {
                _currentState = BotState.WAITING_FOR_SWEEP;
                _partialTpTaken = false;
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

            // 1. Manage Pending Order Expiration per completed bar
            if (_currentState == BotState.FVG_ORDER_PENDING)
            {
                _fvgPendingBarCount++;
                if (_fvgPendingBarCount >= MaxPendingBars)
                {
                    CancelAllPendingLimitOrders();
                    _currentState = BotState.WAITING_FOR_SWEEP;
                    Print("[cBot Expiration] FVG Limit Order expired after {0} bars.", MaxPendingBars);
                }
                return;
            }

            // 2. Timeout sweep if no MSS occurs within 20 bars
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
                if (_bars15M.Count < LookbackBars15M || Bars.Count < LookbackBars15M) return;

                // Spread Guard
                double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
                if (spreadPips > MaxSpreadPips) return;

                // Calculate Swing High/Low over PRIOR completed 15M bars
                double recent15MHigh = double.MinValue;
                double recent15MLow = double.MaxValue;
                int lookback = Math.Min(LookbackBars15M, _bars15M.Count - 1);
                for (int i = 1; i <= lookback; i++)
                {
                    if (_bars15M.HighPrices.Last(i) > recent15MHigh) recent15MHigh = _bars15M.HighPrices.Last(i);
                    if (_bars15M.LowPrices.Last(i) < recent15MLow) recent15MLow = _bars15M.LowPrices.Last(i);
                }

                // Bearish Sweep: Bar pierced prior 15M High and rejected back below
                if (Bars.Last(1).High > recent15MHigh && Bars.Last(1).Close < recent15MHigh)
                {
                    _currentState = BotState.SWEEP_DETECTED_BEARISH;
                    _sweepLevel = recent15MHigh;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.LowPrices.Minimum(10);
                    Print("[cBot Sweep] 15M High ({0:F5}) Swept. Looking for Bearish MSS below {1:F5}", _sweepLevel, _mssLevel);
                }
                // Bullish Sweep: Bar pierced prior 15M Low and rejected back above
                else if (Bars.Last(1).Low < recent15MLow && Bars.Last(1).Close > recent15MLow)
                {
                    _currentState = BotState.SWEEP_DETECTED_BULLISH;
                    _sweepLevel = recent15MLow;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.HighPrices.Maximum(10);
                    Print("[cBot Sweep] 15M Low ({0:F5}) Swept. Looking for Bullish MSS above {1:F5}", _sweepLevel, _mssLevel);
                }
            }
            // Step B: Check Market Structure Shift (MSS) with Displacement
            else if (_currentState == BotState.SWEEP_DETECTED_BEARISH || _currentState == BotState.SWEEP_DETECTED_BULLISH)
            {
                double currentAtr = _atr.Result.Last(1);
                var lastBar = Bars.Last(1);
                double bodySize = Math.Abs(lastBar.Close - lastBar.Open);
                bool isDisplacement = bodySize >= (DisplacementAtrMult * currentAtr);

                // --- BEARISH MSS ---
                if (_currentState == BotState.SWEEP_DETECTED_BEARISH && lastBar.Close < _mssLevel && isDisplacement)
                {
                    double fvgUpper = Bars.Last(3).Low;
                    double fvgLower = Bars.Last(1).High;

                    if (EntryMode == FvgEntryType.Proximal_Edge && fvgUpper > fvgLower)
                        _fvgEntryLevel = fvgLower; // Proximal boundary touch
                    else if (EntryMode == FvgEntryType.Midpoint_50 && fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0; // 50% FVG Midpoint
                    else
                        _fvgEntryLevel = lastBar.Close; // Market close

                    _stopLossPrice = _sweepLevel + (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_stopLossPrice - _fvgEntryLevel) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;
                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel - (rewardPips * Symbol.PipSize);

                    if (EntryMode == FvgEntryType.Market_On_MSS)
                        ExecuteMarketEntry(TradeType.Sell, lastBar.Close, _stopLossPrice, riskPips, rewardPips);
                    else
                        PlaceFvgLimitOrder(TradeType.Sell, _fvgEntryLevel, riskPips, rewardPips);
                }
                // --- BULLISH MSS ---
                else if (_currentState == BotState.SWEEP_DETECTED_BULLISH && lastBar.Close > _mssLevel && isDisplacement)
                {
                    double fvgLower = Bars.Last(3).High;
                    double fvgUpper = Bars.Last(1).Low;

                    if (EntryMode == FvgEntryType.Proximal_Edge && fvgUpper > fvgLower)
                        _fvgEntryLevel = fvgUpper; // Proximal boundary touch
                    else if (EntryMode == FvgEntryType.Midpoint_50 && fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0; // 50% FVG Midpoint
                    else
                        _fvgEntryLevel = lastBar.Close;

                    _stopLossPrice = _sweepLevel - (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_fvgEntryLevel - _stopLossPrice) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;
                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel + (rewardPips * Symbol.PipSize);

                    if (EntryMode == FvgEntryType.Market_On_MSS)
                        ExecuteMarketEntry(TradeType.Buy, lastBar.Close, _stopLossPrice, riskPips, rewardPips);
                    else
                        PlaceFvgLimitOrder(TradeType.Buy, _fvgEntryLevel, riskPips, rewardPips);
                }
            }
        }

        private void ManageOpenPosition(Position position)
        {
            if (!position.StopLoss.HasValue) return;

            double entryPrice = position.EntryPrice;
            double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            double initialRiskPips = Math.Abs(entryPrice - position.StopLoss.Value) / Symbol.PipSize;

            if (initialRiskPips <= 0) return;

            double currentGainPips = position.TradeType == TradeType.Buy 
                ? (currentPrice - entryPrice) / Symbol.PipSize 
                : (entryPrice - currentPrice) / Symbol.PipSize;

            double currentGainR = currentGainPips / initialRiskPips;

            // 1. Breakeven Lock at +1.5R (or configured R)
            if (EnableBreakeven && currentGainR >= BreakevenTriggerR)
            {
                double bePrice = position.TradeType == TradeType.Buy 
                    ? entryPrice + (BreakevenOffsetPips * Symbol.PipSize) 
                    : entryPrice - (BreakevenOffsetPips * Symbol.PipSize);

                bool needsBe = position.TradeType == TradeType.Buy 
                    ? position.StopLoss.Value < bePrice 
                    : position.StopLoss.Value > bePrice;

                if (needsBe)
                {
                    ModifyPosition(position, bePrice, position.TakeProfit);
                    Print("[Breakeven Locked] Stop Loss moved to {0:F5} (+{1:F1}R reached)", bePrice, currentGainR);
                }
            }

            // 2. Partial Take Profit at +3.0R (optional)
            if (EnablePartialTp && !_partialTpTaken && currentGainR >= PartialTpTriggerR)
            {
                double volumeToClose = position.VolumeInUnits * (PartialTpPercent / 100.0);
                volumeToClose = Symbol.NormalizeVolumeInUnits(volumeToClose, RoundingMode.Down);
                if (volumeToClose >= Symbol.VolumeInUnitsMin && volumeToClose < position.VolumeInUnits)
                {
                    ClosePosition(position, volumeToClose);
                    _partialTpTaken = true;
                    Print("[Partial TP Banked] Closed {0} units ({1}%) at +{2:F1}R profit.", volumeToClose, PartialTpPercent, currentGainR);
                }
            }
        }

        private void PlaceFvgLimitOrder(TradeType tradeType, double targetPrice, double riskPips, double rewardPips)
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

            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, targetPrice, "SweepMssBot", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _currentState = BotState.FVG_ORDER_PENDING;
                _fvgPendingBarCount = 0;
                _partialTpTaken = false;
                Print("[cBot Limit Placed] {0} at {1:F5} | SL: {2:F5} ({3:F1} pips) | TP: {4:F5} ({5:F1} pips)", 
                    tradeType, targetPrice, _stopLossPrice, riskPips, _takeProfitPrice, rewardPips);
            }
            else
            {
                Print("[cBot Order Error] Failed to place limit order: {0}", result.Error);
                _currentState = BotState.WAITING_FOR_SWEEP;
            }
        }

        private void ExecuteMarketEntry(TradeType tradeType, double entryPrice, double stopPrice, double riskPips, double rewardPips)
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

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "SweepMssBot", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _currentState = BotState.IN_TRADE;
                _partialTpTaken = false;
                Print("[cBot Market Entry] {0} at {1:F5} | SL: {2:F5} | TP: {3:F5}", tradeType, entryPrice, stopPrice, _takeProfitPrice);
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
