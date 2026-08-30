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
        Auto_Adaptive,
        Midpoint_50,
        Proximal_Edge,
        Market_On_MSS
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        // --- Auto-Adaptive Intelligence ---
        [Parameter("Enable Auto-Adaptive Engine", Group = "Adaptive Intelligence", DefaultValue = true)]
        public bool EnableAdaptiveEngine { get; set; }

        [Parameter("Enable Volume Footprint Filter", Group = "Adaptive Intelligence", DefaultValue = true)]
        public bool EnableVolumeFilter { get; set; }

        [Parameter("Min Relative Volume (RVol)", Group = "Adaptive Intelligence", DefaultValue = 1.25, MinValue = 1.0, MaxValue = 3.0)]
        public double MinRelativeVolume { get; set; }

        [Parameter("Enable 1H Macro Structure Filter", Group = "Adaptive Intelligence", DefaultValue = true)]
        public bool EnableHtfStructureFilter { get; set; }

        [Parameter("Min Sweep Wick Rejection %", Group = "Adaptive Intelligence", DefaultValue = 40.0, MinValue = 20.0, MaxValue = 80.0)]
        public double MinWickRejectionPercent { get; set; }

        // --- Core Strategy Parameters ---
        [Parameter("Displacement ATR Mult", Group = "SMC Strategy", DefaultValue = 0.6, MinValue = 0.3, MaxValue = 3.0)]
        public double DisplacementAtrMult { get; set; }

        [Parameter("Risk Reward Ratio", Group = "SMC Strategy", DefaultValue = 6.5, MinValue = 1.5, MaxValue = 15.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Invalidation Buffer (Pips)", Group = "SMC Strategy", DefaultValue = 4.5, MinValue = 0.5, MaxValue = 10.0)]
        public double InvalidationBufferPips { get; set; }

        [Parameter("Max Pending Bars", Group = "SMC Strategy", DefaultValue = 16, MinValue = 3, MaxValue = 50)]
        public int MaxPendingBars { get; set; }

        [Parameter("FVG Entry Mode", Group = "SMC Strategy", DefaultValue = FvgEntryType.Auto_Adaptive)]
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

        [Parameter("Max Spread (Pips)", Group = "Risk Controls", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Config File Path", Group = "Risk Controls", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // Multi-Timeframe Series & Indicators
        private BotState _currentState;
        private Bars _bars15M;
        private Bars _bars1H;
        private AverageTrueRange _atr5M;
        private AverageTrueRange _atr100M;
        private ExponentialMovingAverage _ema50_1H;

        // Daily State
        private double _dailyStartingBalance;
        private int _currentDay;
        private double _sweepLevel;
        private double _mssLevel;
        private double _fvgEntryLevel;
        private double _stopLossPrice;
        private double _takeProfitPrice;
        private int _fvgPendingBarCount;
        private int _sweepBarIndex;
        private bool _isBreakevenSet;
        private bool _isPartialTpSet;

        protected override void OnStart()
        {
            _currentState = BotState.WAITING_FOR_SWEEP;
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;

            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _bars1H = MarketData.GetBars(TimeFrame.Hour, BotSymbol);

            _atr5M = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);
            _atr100M = Indicators.AverageTrueRange(Bars, 100, MovingAverageType.Simple);
            _ema50_1H = Indicators.ExponentialMovingAverage(_bars1H.ClosePrices, 50);

            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Smart-Money Bot Started] Auto-Adaptive Liquidity & Structure Engine initialized on {0} ({1})", BotSymbol, TimeFrame);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            CheckHotReloadConfig();

            DateTime now = Server.Time;

            // Reset Daily Circuit Breaker at Midnight
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _dailyStartingBalance = Account.Equity;
                if (_currentState == BotState.HALTED_CIRCUIT_BREAKER)
                {
                    _currentState = BotState.WAITING_FOR_SWEEP;
                    Print("[Daily Reset] New trading day started. Circuit breaker cleared.");
                }
            }

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

            // Active Trade Management
            var activePosition = Positions.Find("SweepMssBot", SymbolName);
            if (activePosition != null)
            {
                _currentState = BotState.IN_TRADE;
                ManageOpenPosition(activePosition);
            }
            else if (_currentState == BotState.IN_TRADE)
            {
                _currentState = BotState.WAITING_FOR_SWEEP;
                _isBreakevenSet = false;
                _isPartialTpSet = false;
            }
        }

        protected override void OnBar()
        {
            if (_currentState == BotState.EMERGENCY_KILL || _currentState == BotState.HALTED_CIRCUIT_BREAKER)
                return;

            // Manage Pending Order Expiration
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

            // Timeout sweep if no MSS occurs within 20 bars
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
                if (_bars15M.Count < LookbackBars15M || Bars.Count < LookbackBars15M || _bars1H.Count < 50) return;

                double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
                if (spreadPips > MaxSpreadPips) return;

                // Calculate Prior 15M High/Low Swing Liquidity
                double recent15MHigh = double.MinValue;
                double recent15MLow = double.MaxValue;
                int lookback = Math.Min(LookbackBars15M, _bars15M.Count - 1);
                for (int i = 1; i <= lookback; i++)
                {
                    if (_bars15M.HighPrices.Last(i) > recent15MHigh) recent15MHigh = _bars15M.HighPrices.Last(i);
                    if (_bars15M.LowPrices.Last(i) < recent15MLow) recent15MLow = _bars15M.LowPrices.Last(i);
                }

                var lastBar = Bars.Last(1);
                double candleRange = lastBar.High - lastBar.Low;
                if (candleRange <= 0) candleRange = Symbol.PipSize;

                // 1H Macro Structure Check
                double htf1HClose = _bars1H.ClosePrices.Last(1);
                double htf1HEma = _ema50_1H.Result.Last(1);
                bool htfBullish = htf1HClose > htf1HEma;
                bool htfBearish = htf1HClose < htf1HEma;

                // Bearish Sweep: 15M High Swept with Rejection Wick
                if (lastBar.High > recent15MHigh && lastBar.Close < recent15MHigh)
                {
                    double upperWick = lastBar.High - Math.Max(lastBar.Open, lastBar.Close);
                    double wickRatioPercent = (upperWick / candleRange) * 100.0;

                    bool wickValid = !EnableAdaptiveEngine || (wickRatioPercent >= MinWickRejectionPercent);
                    bool htfValid = !EnableHtfStructureFilter || htfBearish;

                    if (wickValid && htfValid)
                    {
                        _currentState = BotState.SWEEP_DETECTED_BEARISH;
                        _sweepLevel = recent15MHigh;
                        _sweepBarIndex = Bars.Count - 1;
                        _mssLevel = Bars.LowPrices.Minimum(10);
                        Print("[Institutional Sweep] 15M High ({0:F5}) Swept | Wick: {1:F1}% | HTF Bearish: {2}", 
                            _sweepLevel, wickRatioPercent, htfBearish);
                    }
                }
                // Bullish Sweep: 15M Low Swept with Rejection Wick
                else if (lastBar.Low < recent15MLow && lastBar.Close > recent15MLow)
                {
                    double lowerWick = Math.Min(lastBar.Open, lastBar.Close) - lastBar.Low;
                    double wickRatioPercent = (lowerWick / candleRange) * 100.0;

                    bool wickValid = !EnableAdaptiveEngine || (wickRatioPercent >= MinWickRejectionPercent);
                    bool htfValid = !EnableHtfStructureFilter || htfBullish;

                    if (wickValid && htfValid)
                    {
                        _currentState = BotState.SWEEP_DETECTED_BULLISH;
                        _sweepLevel = recent15MLow;
                        _sweepBarIndex = Bars.Count - 1;
                        _mssLevel = Bars.HighPrices.Maximum(10);
                        Print("[Institutional Sweep] 15M Low ({0:F5}) Swept | Wick: {1:F1}% | HTF Bullish: {2}", 
                            _sweepLevel, wickRatioPercent, htfBullish);
                    }
                }
            }
            // Step B: Check Market Structure Shift (MSS) with Volume & Displacement
            else if (_currentState == BotState.SWEEP_DETECTED_BEARISH || _currentState == BotState.SWEEP_DETECTED_BULLISH)
            {
                double currentAtr = _atr5M.Result.Last(1);
                double baselineAtr = _atr100M.Result.Last(1);
                var lastBar = Bars.Last(1);
                double bodySize = Math.Abs(lastBar.Close - lastBar.Open);

                // 1. Adaptive Volatility Adjustment
                double volatilityRatio = baselineAtr > 0 ? (currentAtr / baselineAtr) : 1.0;
                double effectiveDisplacementMult = DisplacementAtrMult;
                if (EnableAdaptiveEngine)
                {
                    if (volatilityRatio < 0.85) effectiveDisplacementMult *= 0.85; // Lower hurdle in compressed market
                    else if (volatilityRatio > 1.25) effectiveDisplacementMult *= 1.15; // Higher hurdle in explosive market
                }

                bool isDisplacement = bodySize >= (effectiveDisplacementMult * currentAtr);

                // 2. Relative Volume (RVol) Check
                bool isVolumeConfirmed = true;
                if (EnableVolumeFilter)
                {
                    double sumVol = 0;
                    int vLookback = Math.Min(20, Bars.Count - 2);
                    for (int i = 2; i <= vLookback + 1; i++) sumVol += Bars.TickVolumes.Last(i);
                    double avgVol = sumVol / vLookback;
                    double rVol = avgVol > 0 ? (lastBar.TickVolume / avgVol) : 1.0;
                    isVolumeConfirmed = rVol >= MinRelativeVolume;
                }

                // --- BEARISH MSS DISPLACEMENT ---
                if (_currentState == BotState.SWEEP_DETECTED_BEARISH && lastBar.Close < _mssLevel && isDisplacement && isVolumeConfirmed)
                {
                    double fvgUpper = Bars.Last(3).Low;
                    double fvgLower = Bars.Last(1).High;

                    FvgEntryType effectiveEntry = EntryMode;
                    if (EntryMode == FvgEntryType.Auto_Adaptive)
                    {
                        effectiveEntry = volatilityRatio > 1.1 ? FvgEntryType.Midpoint_50 : FvgEntryType.Proximal_Edge;
                    }

                    if (effectiveEntry == FvgEntryType.Proximal_Edge && fvgUpper > fvgLower)
                        _fvgEntryLevel = fvgLower;
                    else if (effectiveEntry == FvgEntryType.Midpoint_50 && fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = lastBar.Close;

                    _stopLossPrice = _sweepLevel + (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_stopLossPrice - _fvgEntryLevel) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;

                    double effectiveRr = RiskRewardRatio;
                    if (EnableAdaptiveEngine && volatilityRatio > 1.2) effectiveRr = RiskRewardRatio * 1.15; // Expand RR in trend runs

                    double rewardPips = riskPips * effectiveRr;
                    _takeProfitPrice = _fvgEntryLevel - (rewardPips * Symbol.PipSize);

                    CancelAllPendingLimitOrders();

                    if (effectiveEntry == FvgEntryType.Market_On_MSS)
                        ExecuteMarketEntry(TradeType.Sell, lastBar.Close, _stopLossPrice, riskPips, rewardPips);
                    else
                        PlaceFvgLimitOrder(TradeType.Sell, _fvgEntryLevel, riskPips, rewardPips);
                }
                // --- BULLISH MSS DISPLACEMENT ---
                else if (_currentState == BotState.SWEEP_DETECTED_BULLISH && lastBar.Close > _mssLevel && isDisplacement && isVolumeConfirmed)
                {
                    double fvgLower = Bars.Last(3).High;
                    double fvgUpper = Bars.Last(1).Low;

                    FvgEntryType effectiveEntry = EntryMode;
                    if (EntryMode == FvgEntryType.Auto_Adaptive)
                    {
                        effectiveEntry = volatilityRatio > 1.1 ? FvgEntryType.Midpoint_50 : FvgEntryType.Proximal_Edge;
                    }

                    if (effectiveEntry == FvgEntryType.Proximal_Edge && fvgUpper > fvgLower)
                        _fvgEntryLevel = fvgUpper;
                    else if (effectiveEntry == FvgEntryType.Midpoint_50 && fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = lastBar.Close;

                    _stopLossPrice = _sweepLevel - (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_fvgEntryLevel - _stopLossPrice) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;

                    double effectiveRr = RiskRewardRatio;
                    if (EnableAdaptiveEngine && volatilityRatio > 1.2) effectiveRr = RiskRewardRatio * 1.15;

                    double rewardPips = riskPips * effectiveRr;
                    _takeProfitPrice = _fvgEntryLevel + (rewardPips * Symbol.PipSize);

                    CancelAllPendingLimitOrders();

                    if (effectiveEntry == FvgEntryType.Market_On_MSS)
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

            // 1. One-Time Breakeven Lock at +1.5R with 5-digit broker compliance
            if (EnableBreakeven && !_isBreakevenSet && currentGainR >= BreakevenTriggerR)
            {
                double bePrice = position.TradeType == TradeType.Buy 
                    ? Math.Round(entryPrice + (BreakevenOffsetPips * Symbol.PipSize), Symbol.Digits) 
                    : Math.Round(entryPrice - (BreakevenOffsetPips * Symbol.PipSize), Symbol.Digits);

                bool isSafeDistance = position.TradeType == TradeType.Buy 
                    ? (bePrice < Symbol.Bid - (2.0 * Symbol.PipSize)) 
                    : (bePrice > Symbol.Ask + (2.0 * Symbol.PipSize));

                if (isSafeDistance)
                {
                    var modifyResult = ModifyPosition(position, bePrice, position.TakeProfit);
                    if (modifyResult.IsSuccessful)
                    {
                        _isBreakevenSet = true;
                        Print("[Breakeven Locked] Stop Loss moved to {0:F5} (+{1:F1}R reached)", bePrice, currentGainR);
                    }
                }
            }

            // 2. One-Time Partial Take Profit at +3.0R
            if (EnablePartialTp && !_isPartialTpSet && currentGainR >= PartialTpTriggerR)
            {
                double volumeToClose = position.VolumeInUnits * (PartialTpPercent / 100.0);
                volumeToClose = Symbol.NormalizeVolumeInUnits(volumeToClose, RoundingMode.Down);
                if (volumeToClose >= Symbol.VolumeInUnitsMin && volumeToClose < position.VolumeInUnits)
                {
                    var closeResult = ClosePosition(position, volumeToClose);
                    if (closeResult.IsSuccessful)
                    {
                        _isPartialTpSet = true;
                        Print("[Partial TP Banked] Closed {0} units ({1}%) at +{2:F1}R profit.", volumeToClose, PartialTpPercent, currentGainR);
                    }
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

            targetPrice = Math.Round(targetPrice, Symbol.Digits);
            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, targetPrice, "SweepMssBot", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _currentState = BotState.FVG_ORDER_PENDING;
                _fvgPendingBarCount = 0;
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                Print("[Smart Order Placed] {0} Limit at {1:F5} | SL: {2:F5} ({3:F1} pips) | TP: {4:F5} ({5:F1} pips)", 
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
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                Print("[Smart Market Entry] {0} at {1:F5} | SL: {2:F5} | TP: {3:F5}", tradeType, entryPrice, stopPrice, _takeProfitPrice);
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
            Print("[CIRCUIT BREAKER] Daily Drawdown {0:F2}% reached limit. Halting robot for today.", currentDrawdown);
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
