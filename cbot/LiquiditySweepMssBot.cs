#pragma warning disable CS0618
using System;
using System.IO;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum SessionPreset
    {
        London_And_NewYork,     // 07:00 - 16:30 UTC (Peak Institutional Volume)
        London_Only,            // 07:00 - 11:30 UTC
        NewYork_Only,           // 12:30 - 16:30 UTC
        Custom_Hours,           // Use Start and End Hour parameters
        Disabled_24_Hours       // Trade all day
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        // --- Session Kill-Zone Clocks ---
        [Parameter("Session Filter Mode", Group = "Session Clocks (UTC)", DefaultValue = SessionPreset.London_And_NewYork)]
        public SessionPreset SessionMode { get; set; }

        [Parameter("Custom Start Hour (UTC)", Group = "Session Clocks (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int CustomStartHourUtc { get; set; }

        [Parameter("Custom End Hour (UTC)", Group = "Session Clocks (UTC)", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int CustomEndHourUtc { get; set; }

        [Parameter("Close Open Trades at End of Day", Group = "Session Clocks (UTC)", DefaultValue = false)]
        public bool CloseAtEndOfDay { get; set; }

        [Parameter("Max Allowed Spread (Pips)", Group = "Session Clocks (UTC)", DefaultValue = 1.2, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        // --- Core Strategy Parameters ---
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

        // --- Trade Protection & Management ---
        [Parameter("Enable Breakeven Lock", Group = "Trade Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Breakeven Trigger (+R)", Group = "Trade Management", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Breakeven Offset (Pips)", Group = "Trade Management", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 3.0)]
        public double BreakevenOffsetPips { get; set; }

        [Parameter("Enable Partial Take Profit", Group = "Trade Management", DefaultValue = true)]
        public bool EnablePartialTp { get; set; }

        [Parameter("Partial TP Trigger (+R)", Group = "Trade Management", DefaultValue = 3.0, MinValue = 1.5, MaxValue = 5.0)]
        public double PartialTpTriggerR { get; set; }

        [Parameter("Partial TP Volume %", Group = "Trade Management", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double PartialTpPercent { get; set; }

        // --- Risk & Circuit Breaker ---
        [Parameter("Risk Per Trade %", Group = "Risk Controls", DefaultValue = 2.5, MinValue = 0.1, MaxValue = 20.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", Group = "Risk Controls", DefaultValue = 15.0, MinValue = 5.0, MaxValue = 40.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Config File Path", Group = "Risk Controls", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // Indicators & State Machine
        private Bars _bars15M;
        private AverageTrueRange _atr;
        private double _dailyStartingBalance;
        private int _currentDay;
        private double _sweepLevel;
        private double _mssLevel;
        private double _fvgEntryLevel;
        private double _stopLossPrice;
        private double _takeProfitPrice;
        private int _fvgPendingBarCount;
        private int _sweepBarIndex;
        private bool _isSweepPendingMss;
        private bool _isBullishSweep;
        private bool _isPendingLimitActive;
        private bool _isBreakevenSet;
        private bool _isPartialTpSet;
        private bool _isCircuitHalted;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;
            _isSweepPendingMss = false;
            _isPendingLimitActive = false;
            _isCircuitHalted = false;

            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _atr = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);

            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Session Kill-Zone cBot Started] Mode: {0} on {1} ({2})", SessionMode, BotSymbol, TimeFrame);
            CheckHotReloadConfig();
        }

        protected override void OnTick()
        {
            CheckHotReloadConfig();

            DateTime now = Server.Time;

            // Reset Daily Circuit Breaker at Midnight (00:00 UTC)
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

            // Daily Drawdown Circuit Breaker Guard
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
            var activePosition = Positions.Find("KillZoneSMC", SymbolName);
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

            // Check Session Filter Window
            bool isInsideSession = IsWithinTradingSession(now);

            // Outside Allowed Session
            if (!isInsideSession)
            {
                if (_isPendingLimitActive)
                {
                    CancelAllPendingLimitOrders();
                    Print("[Session Close] Cancelled pending limit order outside active session.");
                }
                _isSweepPendingMss = false;

                if (CloseAtEndOfDay && now.Hour >= 21)
                {
                    FlattenAllPositions();
                }
                return;
            }

            // Manage Pending Order Expiration
            if (_isPendingLimitActive)
            {
                _fvgPendingBarCount++;
                if (_fvgPendingBarCount >= MaxPendingBars)
                {
                    CancelAllPendingLimitOrders();
                    Print("[cBot Expiration] FVG Limit Order expired after {0} bars.", MaxPendingBars);
                }
                return;
            }

            // Timeout sweep if no MSS displacement occurs within 16 bars
            if (_isSweepPendingMss)
            {
                if (Bars.Count - _sweepBarIndex > 16)
                {
                    _isSweepPendingMss = false;
                    Print("[cBot Timeout] Sweep expired without confirmed MSS displacement. Resetting.");
                }
            }

            if (_bars15M.Count < LookbackBars15M || Bars.Count < LookbackBars15M) return;

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            // Do not take new entries if a position is already open
            if (Positions.Find("KillZoneSMC", SymbolName) != null) return;

            // =========================================================================
            // STEP 1: CHECK 15M HIGH / LOW LIQUIDITY SWEEP
            // =========================================================================
            if (!_isSweepPendingMss)
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
                    _isSweepPendingMss = true;
                    _isBullishSweep = false;
                    _sweepLevel = recent15MHigh;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.LowPrices.Minimum(8);
                    Print("[Session Sweep] 15M High ({0:F5}) Swept. Looking for Bearish MSS below {1:F5}", _sweepLevel, _mssLevel);
                }
                // Bullish Sweep: Pierced 15M Low and Closed Above
                else if (lastBar.Low < recent15MLow && lastBar.Close > recent15MLow)
                {
                    _isSweepPendingMss = true;
                    _isBullishSweep = true;
                    _sweepLevel = recent15MLow;
                    _sweepBarIndex = Bars.Count - 1;
                    _mssLevel = Bars.HighPrices.Maximum(8);
                    Print("[Session Sweep] 15M Low ({0:F5}) Swept. Looking for Bullish MSS above {1:F5}", _sweepLevel, _mssLevel);
                }
            }
            // =========================================================================
            // STEP 2: CHECK M5 MARKET STRUCTURE SHIFT (MSS) WITH DISPLACEMENT
            // =========================================================================
            else
            {
                double currentAtr = _atr.Result.Last(1);
                var lastBar = Bars.Last(1);
                double bodySize = Math.Abs(lastBar.Close - lastBar.Open);
                bool isDisplacement = bodySize >= (DisplacementAtrMult * currentAtr);

                // --- Bearish MSS Confirmation ---
                if (!_isBullishSweep && lastBar.Close < _mssLevel && isDisplacement)
                {
                    double fvgUpper = Bars.Last(3).Low;
                    double fvgLower = Bars.Last(1).High;

                    // 50% FVG Retracement Level
                    if (fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = (lastBar.High + lastBar.Close) / 2.0;

                    _stopLossPrice = _sweepLevel + (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_stopLossPrice - _fvgEntryLevel) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;

                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel - (rewardPips * Symbol.PipSize);

                    PlaceFvgLimitOrder(TradeType.Sell, _fvgEntryLevel, _stopLossPrice, _takeProfitPrice, riskPips, rewardPips);
                    _isSweepPendingMss = false;
                }
                // --- Bullish MSS Confirmation ---
                else if (_isBullishSweep && lastBar.Close > _mssLevel && isDisplacement)
                {
                    double fvgLower = Bars.Last(3).High;
                    double fvgUpper = Bars.Last(1).Low;

                    // 50% FVG Retracement Level
                    if (fvgUpper > fvgLower)
                        _fvgEntryLevel = (fvgUpper + fvgLower) / 2.0;
                    else
                        _fvgEntryLevel = (lastBar.Low + lastBar.Close) / 2.0;

                    _stopLossPrice = _sweepLevel - (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(_fvgEntryLevel - _stopLossPrice) / Symbol.PipSize;
                    if (riskPips <= 0) riskPips = 2.0;

                    double rewardPips = riskPips * RiskRewardRatio;
                    _takeProfitPrice = _fvgEntryLevel + (rewardPips * Symbol.PipSize);

                    PlaceFvgLimitOrder(TradeType.Buy, _fvgEntryLevel, _stopLossPrice, _takeProfitPrice, riskPips, rewardPips);
                    _isSweepPendingMss = false;
                }
            }
        }

        private bool IsWithinTradingSession(DateTime time)
        {
            int hour = time.Hour;
            int minute = time.Minute;
            double timeDecimal = hour + (minute / 60.0);

            switch (SessionMode)
            {
                case SessionPreset.London_And_NewYork:
                    // London (07:00 - 11:30) OR New York (12:30 - 16:30)
                    bool isLondon = timeDecimal >= 7.0 && timeDecimal <= 11.5;
                    bool isNewYork = timeDecimal >= 12.5 && timeDecimal <= 16.5;
                    return isLondon || isNewYork;

                case SessionPreset.London_Only:
                    return timeDecimal >= 7.0 && timeDecimal <= 11.5;

                case SessionPreset.NewYork_Only:
                    return timeDecimal >= 12.5 && timeDecimal <= 16.5;

                case SessionPreset.Custom_Hours:
                    if (CustomStartHourUtc < CustomEndHourUtc)
                        return hour >= CustomStartHourUtc && hour < CustomEndHourUtc;
                    else
                        return hour >= CustomStartHourUtc || hour < CustomEndHourUtc;

                case SessionPreset.Disabled_24_Hours:
                default:
                    return true;
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
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                volumeInUnits = Symbol.VolumeInUnitsMin;
            }

            targetPrice = Math.Round(targetPrice, Symbol.Digits);
            stopPrice = Math.Round(stopPrice, Symbol.Digits);
            tpPrice = Math.Round(tpPrice, Symbol.Digits);

            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, targetPrice, "KillZoneSMC", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _isPendingLimitActive = true;
                _fvgPendingBarCount = 0;
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                Print("[KillZone Order Placed] {0} Limit at {1:F5} | SL: {2:F5} ({3:F1} pips) | TP: {4:F5} ({5:F1} pips)", 
                    tradeType, targetPrice, stopPrice, riskPips, tpPrice, rewardPips);
            }
            else
            {
                Print("[cBot Order Error] Failed to place limit order: {0}", result.Error);
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
                if (order.Label == "KillZoneSMC")
                    CancelPendingOrder(order);
            }
            _isPendingLimitActive = false;
        }

        private void FlattenAllPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == "KillZoneSMC")
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
