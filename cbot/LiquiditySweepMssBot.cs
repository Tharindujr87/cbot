#pragma warning disable CS0618
using System;
using System.IO;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum FvgEntryType
    {
        Midpoint_50,       // 50% equilibrium retracement of the FVG
        Proximal_Edge,     // Enters at the boundary edge of the FVG (Highest fill rate)
        Market_On_MSS      // Enters instantly on MSS candle close
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        // =========================================================================
        // INSTITUTIONAL SESSION CLOCKS (Filters out Asian Chop & False Breaks)
        // =========================================================================
        [Parameter("Use Session Filter", Group = "Session Clocks (UTC)", DefaultValue = true)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start UTC", Group = "Session Clocks (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int SessionStartUtc { get; set; }

        [Parameter("Session End UTC", Group = "Session Clocks (UTC)", DefaultValue = 17, MinValue = 0, MaxValue = 23)]
        public int SessionEndUtc { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Session Clocks (UTC)", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        // =========================================================================
        // SMC CORE STRATEGY (15M Liquidity Sweep + 5M MSS Displacement)
        // =========================================================================
        [Parameter("15M Sweep Lookback Bars", Group = "SMC Strategy", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int LookbackBars15M { get; set; }

        [Parameter("Displacement ATR Mult", Group = "SMC Strategy", DefaultValue = 0.6, MinValue = 0.3, MaxValue = 2.0)]
        public double DisplacementAtrMult { get; set; }

        [Parameter("Risk Reward Ratio", Group = "SMC Strategy", DefaultValue = 6.5, MinValue = 2.0, MaxValue = 15.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Invalidation SL Buffer (Pips)", Group = "SMC Strategy", DefaultValue = 4.5, MinValue = 0.5, MaxValue = 10.0)]
        public double InvalidationBufferPips { get; set; }

        [Parameter("Max Pending Bars", Group = "SMC Strategy", DefaultValue = 16, MinValue = 3, MaxValue = 40)]
        public int MaxPendingBars { get; set; }

        [Parameter("FVG Entry Mode", Group = "SMC Strategy", DefaultValue = FvgEntryType.Proximal_Edge)]
        public FvgEntryType EntryMode { get; set; }

        // =========================================================================
        // TRADE MANAGEMENT & BREAKEVEN PROTECTION
        // =========================================================================
        [Parameter("Enable Breakeven Lock", Group = "Trade Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Breakeven Trigger (+R)", Group = "Trade Management", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Breakeven Offset (Pips)", Group = "Trade Management", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 3.0)]
        public double BreakevenOffsetPips { get; set; }

        [Parameter("Enable Partial Take Profit", Group = "Trade Management", DefaultValue = false)]
        public bool EnablePartialTp { get; set; }

        [Parameter("Partial TP Trigger (+R)", Group = "Trade Management", DefaultValue = 3.0, MinValue = 1.5, MaxValue = 5.0)]
        public double PartialTpTriggerR { get; set; }

        [Parameter("Partial TP Volume %", Group = "Trade Management", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double PartialTpPercent { get; set; }

        // =========================================================================
        // RISK & PROTECTION
        // =========================================================================
        [Parameter("Risk Per Trade %", Group = "Risk Controls", DefaultValue = 2.5, MinValue = 0.1, MaxValue = 20.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", Group = "Risk Controls", DefaultValue = 15.0, MinValue = 5.0, MaxValue = 40.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Config File Path", Group = "Risk Controls", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // State Machine & Indicators
        private Bars _bars15M;
        private AverageTrueRange _atr5M;
        private double _dailyStartingBalance;
        private int _currentDay;
        private double _sweepLevel;
        private double _mssLevel;
        private double _fvgEntryLevel;
        private double _stopLossPrice;
        private double _takeProfitPrice;
        private int _fvgPendingBarCount;
        private int _sweepBarIndex;
        private bool _isSweepActive;
        private bool _isBullishSweep;
        private bool _isBreakevenSet;
        private bool _isPartialTpSet;
        private bool _isCircuitHalted;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;
            _isSweepActive = false;
            _isCircuitHalted = false;

            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _atr5M = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);

            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Winning SMC Sweep & MSS Bot Initialized] Symbol: {0} | Chart: {1}", BotSymbol, TimeFrame);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            CheckHotReloadConfig();

            DateTime now = Server.Time;

            // Daily Reset at Midnight (00:00 UTC)
            if (now.DayOfYear != _currentDay)
            {
                _currentDay = now.DayOfYear;
                _dailyStartingBalance = Account.Equity;
                if (_isCircuitHalted)
                {
                    _isCircuitHalted = false;
                    Print("[Daily Reset] New trading day started. Circuit breaker cleared.");
                }
            }

            if (_isCircuitHalted) return;

            // Daily Drawdown Circuit Breaker
            double dailyLoss = _dailyStartingBalance - Account.Equity;
            double drawdownPercent = (dailyLoss / _dailyStartingBalance) * 100.0;
            if (drawdownPercent >= CircuitBreakerDrawdownPercent)
            {
                _isCircuitHalted = true;
                Print("[CIRCUIT BREAKER] Daily Drawdown {0:F2}% reached limit. Halting robot for today.", drawdownPercent);
                CancelAllPendingLimitOrders();
                FlattenAllPositions();
                return;
            }

            // Active Position Management
            var activePosition = Positions.Find("SweepMssBot", SymbolName);
            if (activePosition != null)
            {
                ManageOpenPosition(activePosition);
            }
            else
            {
                _isBreakevenSet = false;
                _isPartialTpSet = false;
            }
        }

        protected override void OnBar()
        {
            if (_isCircuitHalted) return;

            DateTime now = Server.Time;

            // 1. Session Filter Check (London & New York 07:00 - 17:00 UTC)
            if (UseSessionFilter)
            {
                int currentHourUtc = now.Hour;
                if (currentHourUtc < SessionStartUtc || currentHourUtc >= SessionEndUtc)
                {
                    if (Positions.Count == 0 && PendingOrders.Count > 0)
                    {
                        CancelAllPendingLimitOrders();
                    }
                    _isSweepActive = false;
                    return;
                }
            }

            // 2. Manage Pending Order Expiration
            if (PendingOrders.Count > 0)
            {
                _fvgPendingBarCount++;
                if (_fvgPendingBarCount >= MaxPendingBars)
                {
                    CancelAllPendingLimitOrders();
                    Print("[Expiration] Pending FVG Limit Order expired after {0} bars.", MaxPendingBars);
                }
                return;
            }

            // 3. Timeout sweep if no MSS displacement occurs within 20 bars
            if (_isSweepActive)
            {
                if (Bars.Count - _sweepBarIndex > 20)
                {
                    _isSweepActive = false;
                    Print("[Timeout] 15M Sweep expired without confirmed MSS displacement. Resetting.");
                }
            }

            if (_bars15M.Count < LookbackBars15M || Bars.Count < 30) return;

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            // Prevent new entry if a position is currently open
            if (Positions.Find("SweepMssBot", SymbolName) != null) return;

            // =========================================================================
            // STEP 1: CHECK 15M HIGH / LOW LIQUIDITY SWEEP
            // =========================================================================
            if (!_isSweepActive)
            {
                double recent15MHigh = double.MinValue;
                double recent15MLow = double.MaxValue;
                int lookback = Math.Min(LookbackBars15M, _bars15M.Count - 1);
                for (int i = 1; i <= lookback; i++)
                {
                    if (_bars15M.HighPrices.Last(i) > recent15MHigh) recent15MHigh = _bars15M.HighPrices.Last(i);
                    if (_bars15M.LowPrices.Last(i) < recent15MLow) recent15MLow = _bars15M.LowPrices.Last(i);
                }

                var lastBar = Bars.Last(1);

                // Bearish Sweep: Pierced 15M High and Closed Below
                if (lastBar.High > recent15MHigh && lastBar.Close < recent15MHigh)
                {
                    _isSweepActive = true;
                    _isBullishSweep = false;
                    _sweepLevel = recent15MHigh;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.LowPrices.Minimum(10);
                    Print("[15M Sweep] High ({0:F5}) Swept. Looking for Bearish MSS below {1:F5}", _sweepLevel, _mssLevel);
                }
                // Bullish Sweep: Pierced 15M Low and Closed Above
                else if (lastBar.Low < recent15MLow && lastBar.Close > recent15MLow)
                {
                    _isSweepActive = true;
                    _isBullishSweep = true;
                    _sweepLevel = recent15MLow;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.HighPrices.Maximum(10);
                    Print("[15M Sweep] Low ({0:F5}) Swept. Looking for Bullish MSS above {1:F5}", _sweepLevel, _mssLevel);
                }
            }
            // =========================================================================
            // STEP 2: CHECK 5M MARKET STRUCTURE SHIFT (MSS) WITH DISPLACEMENT
            // =========================================================================
            else
            {
                double currentAtr = _atr5M.Result.Last(1);
                var lastBar = Bars.Last(1);
                double bodySize = Math.Abs(lastBar.Close - lastBar.Open);
                bool isDisplacement = bodySize >= (DisplacementAtrMult * currentAtr);

                // --- Bearish MSS Confirmation ---
                if (!_isBullishSweep && lastBar.Close < _mssLevel && isDisplacement)
                {
                    double fvgUpper = Bars.Last(3).Low;
                    double fvgLower = Bars.Last(1).High;

                    if (EntryMode == FvgEntryType.Proximal_Edge && fvgUpper > fvgLower)
                        _fvgEntryLevel = fvgLower;
                    else if (EntryMode == FvgEntryType.Midpoint_50 && fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = lastBar.Close;

                    _stopLossPrice = _sweepLevel + (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_stopLossPrice - _fvgEntryLevel) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;

                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel - (rewardPips * Symbol.PipSize);

                    if (EntryMode == FvgEntryType.Market_On_MSS)
                        ExecuteMarketEntry(TradeType.Sell, lastBar.Close, _stopLossPrice, _takeProfitPrice, riskPips, rewardPips);
                    else
                        PlaceFvgLimitOrder(TradeType.Sell, _fvgEntryLevel, _stopLossPrice, _takeProfitPrice, riskPips, rewardPips);

                    _isSweepActive = false;
                }
                // --- Bullish MSS Confirmation ---
                else if (_isBullishSweep && lastBar.Close > _mssLevel && isDisplacement)
                {
                    double fvgLower = Bars.Last(3).High;
                    double fvgUpper = Bars.Last(1).Low;

                    if (EntryMode == FvgEntryType.Proximal_Edge && fvgUpper > fvgLower)
                        _fvgEntryLevel = fvgUpper;
                    else if (EntryMode == FvgEntryType.Midpoint_50 && fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = lastBar.Close;

                    _stopLossPrice = _sweepLevel - (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_fvgEntryLevel - _stopLossPrice) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;

                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel + (rewardPips * Symbol.PipSize);

                    if (EntryMode == FvgEntryType.Market_On_MSS)
                        ExecuteMarketEntry(TradeType.Buy, lastBar.Close, _stopLossPrice, _takeProfitPrice, riskPips, rewardPips);
                    else
                        PlaceFvgLimitOrder(TradeType.Buy, _fvgEntryLevel, _stopLossPrice, _takeProfitPrice, riskPips, rewardPips);

                    _isSweepActive = false;
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

            // 1. One-Time Breakeven Lock at +1.5R
            if (EnableBreakeven && !_isBreakevenSet && currentGainR >= BreakevenTriggerR)
            {
                double bePrice = position.TradeType == TradeType.Buy 
                    ? Math.Round(entryPrice + (BreakevenOffsetPips * Symbol.PipSize), Symbol.Digits) 
                    : Math.Round(entryPrice - (BreakevenOffsetPips * Symbol.PipSize), Symbol.Digits);

                bool isSafe = position.TradeType == TradeType.Buy 
                    ? (bePrice < Symbol.Bid - (2.0 * Symbol.PipSize)) 
                    : (bePrice > Symbol.Ask + (2.0 * Symbol.PipSize));

                if (isSafe)
                {
                    var modifyResult = ModifyPosition(position, bePrice, position.TakeProfit);
                    if (modifyResult.IsSuccessful)
                    {
                        _isBreakevenSet = true;
                        Print("[Breakeven Locked] Stop Loss moved to {0:F5} (+{1:F1}R reached)", bePrice, currentGainR);
                    }
                }
            }

            // 2. Optional Partial Take Profit at +3.0R
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

        private void PlaceFvgLimitOrder(TradeType tradeType, double targetPrice, double stopPrice, double tpPrice, double riskPips, double rewardPips)
        {
            CancelAllPendingLimitOrders();

            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, riskPips);

            double requiredMargin = volumeInUnits / 30.0;
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                volumeInUnits = (Account.FreeMargin * 0.85) * 30.0;
            }

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (volumeInUnits < Symbol.VolumeInUnitsMin) volumeInUnits = Symbol.VolumeInUnitsMin;

            targetPrice = Math.Round(targetPrice, Symbol.Digits);
            stopPrice = Math.Round(stopPrice, Symbol.Digits);
            tpPrice = Math.Round(tpPrice, Symbol.Digits);

            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, targetPrice, "SweepMssBot", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _fvgPendingBarCount = 0;
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                Print("[Limit Placed] {0} at {1:F5} | SL: {2:F5} ({3:F1}p) | TP: {4:F5} ({5:F1}p)", 
                    tradeType, targetPrice, stopPrice, riskPips, tpPrice, rewardPips);
            }
        }

        private void ExecuteMarketEntry(TradeType tradeType, double entryPrice, double stopPrice, double tpPrice, double riskPips, double rewardPips)
        {
            CancelAllPendingLimitOrders();

            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, riskPips);

            double requiredMargin = volumeInUnits / 30.0;
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                volumeInUnits = (Account.FreeMargin * 0.85) * 30.0;
            }

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (volumeInUnits < Symbol.VolumeInUnitsMin) volumeInUnits = Symbol.VolumeInUnitsMin;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "SweepMssBot", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                Print("[Market Entry] {0} at {1:F5} | SL: {2:F5} ({3:F1}p) | TP: {4:F5} ({5:F1}p)", 
                    tradeType, entryPrice, stopPrice, riskPips, tpPrice, rewardPips);
            }
        }

        private double CalculateVolumeUnits(double riskAmount, double riskPips)
        {
            if (riskPips <= 0) return Symbol.VolumeInUnitsMin;
            double pipValuePerUnit = Symbol.PipValue;
            double units = riskAmount / (riskPips * pipValuePerUnit);
            return units;
        }

        private void CancelAllPendingLimitOrders()
        {
            foreach (var order in PendingOrders)
            {
                if (order.Label == "SweepMssBot")
                    CancelPendingOrder(order);
            }
        }

        private void FlattenAllPositions()
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
                if (json.Contains("\"emergency_kill_active\": true") && !_isCircuitHalted)
                {
                    _isCircuitHalted = true;
                    Print("[EMERGENCY KILL] Immediate halt requested. Purging orders.");
                    CancelAllPendingLimitOrders();
                    FlattenAllPositions();
                }
                else if (json.Contains("\"emergency_kill_active\": false") && _isCircuitHalted)
                {
                    _isCircuitHalted = false;
                    Print("[cBot Resumed] Emergency lock cleared.");
                }
            }
            catch { }
        }
    }
}
