#pragma warning disable CS0618
using System;
using System.IO;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum EngineMode
    {
        Hybrid_Dual_Engine,
        Trend_Continuation_Only,
        Liquidity_Sweep_Only
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        [Parameter("Engine Mode", Group = "System Mode", DefaultValue = EngineMode.Hybrid_Dual_Engine)]
        public EngineMode OperatingMode { get; set; }

        // --- Engine 1: Trend Continuation Settings ---
        [Parameter("Enable Trend Engine", Group = "Engine 1: Trend Following", DefaultValue = true)]
        public bool EnableTrendEngine { get; set; }

        [Parameter("Trend EMA Period (M15)", Group = "Engine 1: Trend Following", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Pullback Fast EMA (M5)", Group = "Engine 1: Trend Following", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int FastEmaPeriod { get; set; }

        [Parameter("Displacement ATR Mult", Group = "Engine 1: Trend Following", DefaultValue = 0.5, MinValue = 0.2, MaxValue = 2.0)]
        public double DisplacementAtrMult { get; set; }

        // --- Engine 2: Liquidity Sweep Settings ---
        [Parameter("Enable Sweep Engine", Group = "Engine 2: Liquidity Sweep", DefaultValue = true)]
        public bool EnableSweepEngine { get; set; }

        [Parameter("15M Sweep Lookback Bars", Group = "Engine 2: Liquidity Sweep", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int SweepLookbackBars { get; set; }

        [Parameter("Min Sweep Invalidation Buffer (Pips)", Group = "Engine 2: Liquidity Sweep", DefaultValue = 3.5, MinValue = 0.5, MaxValue = 10.0)]
        public double InvalidationBufferPips { get; set; }

        // --- Filters ---
        [Parameter("Enable Volume Filter", Group = "Filters", DefaultValue = false)]
        public bool EnableVolumeFilter { get; set; }

        [Parameter("Min Relative Volume (RVol)", Group = "Filters", DefaultValue = 1.0, MinValue = 0.8, MaxValue = 2.0)]
        public double MinRelativeVolume { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Filters", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        // --- Trade Management & Risk ---
        [Parameter("Risk Reward Ratio", Group = "Trade Management", DefaultValue = 4.5, MinValue = 2.0, MaxValue = 10.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Max Pending Bars", Group = "Trade Management", DefaultValue = 16, MinValue = 3, MaxValue = 40)]
        public int MaxPendingBars { get; set; }

        [Parameter("Enable Breakeven Lock", Group = "Trade Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Breakeven Trigger (+R)", Group = "Trade Management", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 3.0)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Breakeven Offset (Pips)", Group = "Trade Management", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 3.0)]
        public double BreakevenOffsetPips { get; set; }

        [Parameter("Enable Partial TP", Group = "Trade Management", DefaultValue = true)]
        public bool EnablePartialTp { get; set; }

        [Parameter("Partial TP Trigger (+R)", Group = "Trade Management", DefaultValue = 2.5, MinValue = 1.5, MaxValue = 4.0)]
        public double PartialTpTriggerR { get; set; }

        [Parameter("Partial TP Volume %", Group = "Trade Management", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double PartialTpPercent { get; set; }

        [Parameter("Enable Dynamic ATR Trailing", Group = "Trade Management", DefaultValue = true)]
        public bool EnableAtrTrailingStop { get; set; }

        [Parameter("Trailing ATR Multiplier", Group = "Trade Management", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 4.0)]
        public double TrailingAtrMultiplier { get; set; }

        // --- Risk Protection ---
        [Parameter("Risk Per Trade %", Group = "Risk Protection", DefaultValue = 2.5, MinValue = 0.1, MaxValue = 20.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", Group = "Risk Protection", DefaultValue = 15.0, MinValue = 5.0, MaxValue = 40.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Config File Path", Group = "Risk Protection", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // Indicators & Bars
        private Bars _bars15M;
        private AverageTrueRange _atr5M;
        private ExponentialMovingAverage _ema50_15M;
        private ExponentialMovingAverage _ema20_5M;

        // State Machine
        private bool _isPendingOrderActive;
        private int _pendingBarCounter;
        private double _dailyStartingBalance;
        private int _currentDay;
        private bool _isBreakevenSet;
        private bool _isPartialTpSet;
        private double _highestPriceSinceEntry;
        private double _lowestPriceSinceEntry;
        private bool _isCircuitHalted;

        protected override void OnStart()
        {
            _dailyStartingBalance = Account.Balance;
            _currentDay = Server.Time.DayOfYear;
            _isPendingOrderActive = false;
            _isCircuitHalted = false;

            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _atr5M = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);
            _ema50_15M = Indicators.ExponentialMovingAverage(_bars15M.ClosePrices, TrendEmaPeriod);
            _ema20_5M = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastEmaPeriod);

            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Fluid Hybrid Bot Started] Mode: {0} on {1} ({2})", OperatingMode, BotSymbol, TimeFrame);
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
                CancelAllPendingOrders();
                CloseAllPositions();
                return;
            }

            // Active Position Management
            var activePosition = Positions.Find("HybridSweepMss", SymbolName);
            if (activePosition != null)
            {
                ManageOpenPosition(activePosition);
            }
            else
            {
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                _highestPriceSinceEntry = double.MinValue;
                _lowestPriceSinceEntry = double.MaxValue;
            }
        }

        protected override void OnBar()
        {
            if (_isCircuitHalted) return;

            // Manage Pending Order Expiration
            if (_isPendingOrderActive)
            {
                _pendingBarCounter++;
                if (_pendingBarCounter >= MaxPendingBars)
                {
                    CancelAllPendingOrders();
                    _isPendingOrderActive = false;
                    Print("[Expiration] Pending order expired after {0} bars.", MaxPendingBars);
                }
                return;
            }

            if (_bars15M.Count < 30 || Bars.Count < 30) return;

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            if (Positions.Find("HybridSweepMss", SymbolName) != null) return;

            // Volume Check (Optional)
            bool volumeConfirmed = true;
            if (EnableVolumeFilter)
            {
                double sumVol = 0;
                int vLookback = Math.Min(20, Bars.Count - 2);
                for (int i = 2; i <= vLookback + 1; i++) sumVol += Bars.TickVolumes.Last(i);
                double avgVol = sumVol / vLookback;
                double rVol = avgVol > 0 ? (Bars.Last(1).TickVolume / avgVol) : 1.0;
                volumeConfirmed = rVol >= MinRelativeVolume;
            }
            if (!volumeConfirmed) return;

            var lastBar = Bars.Last(1);
            double currentAtr = _atr5M.Result.Last(1);
            double bodySize = Math.Abs(lastBar.Close - lastBar.Open);

            // =========================================================================
            // ENGINE 1: TREND FOLLOWING (PULLBACK / BREAKOUT RETEST)
            // =========================================================================
            if ((OperatingMode == EngineMode.Hybrid_Dual_Engine || OperatingMode == EngineMode.Trend_Continuation_Only) && EnableTrendEngine)
            {
                double m15Close = _bars15M.ClosePrices.Last(1);
                double m15Ema50 = _ema50_15M.Result.Last(1);
                double m5Ema20 = _ema20_5M.Result.Last(1);

                bool isUptrend = m15Close > m15Ema50;
                bool isDowntrend = m15Close < m15Ema50;
                bool isDisplacement = bodySize >= (DisplacementAtrMult * currentAtr);

                // --- Bullish Trend Setup ---
                if (isUptrend && isDisplacement && lastBar.Close > lastBar.Open)
                {
                    double recentM5High = Bars.HighPrices.Maximum(6);
                    if (lastBar.Close >= recentM5High) // Bullish impulse break
                    {
                        double entryPrice = (lastBar.Close + m5Ema20) / 2.0; // 50% discount equilibrium entry
                        double swingLow = Bars.LowPrices.Minimum(6);
                        double stopLossPrice = swingLow - (InvalidationBufferPips * Symbol.PipSize);
                        double riskPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;

                        if (riskPips >= 2.0 && riskPips <= 25.0)
                        {
                            double rewardPips = riskPips * RiskRewardRatio;
                            double takeProfitPrice = entryPrice + (rewardPips * Symbol.PipSize);

                            PlaceHybridLimitOrder(TradeType.Buy, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Trend-Buy");
                            return;
                        }
                    }
                }
                // --- Bearish Trend Setup ---
                else if (isDowntrend && isDisplacement && lastBar.Close < lastBar.Open)
                {
                    double recentM5Low = Bars.LowPrices.Minimum(6);
                    if (lastBar.Close <= recentM5Low) // Bearish impulse break
                    {
                        double entryPrice = (lastBar.Close + m5Ema20) / 2.0; // 50% premium equilibrium entry
                        double swingHigh = Bars.HighPrices.Maximum(6);
                        double stopLossPrice = swingHigh + (InvalidationBufferPips * Symbol.PipSize);
                        double riskPips = Math.Abs(stopLossPrice - entryPrice) / Symbol.PipSize;

                        if (riskPips >= 2.0 && riskPips <= 25.0)
                        {
                            double rewardPips = riskPips * RiskRewardRatio;
                            double takeProfitPrice = entryPrice - (rewardPips * Symbol.PipSize);

                            PlaceHybridLimitOrder(TradeType.Sell, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Trend-Sell");
                            return;
                        }
                    }
                }
            }

            // =========================================================================
            // ENGINE 2: 15M LIQUIDITY SWEEP (SWING EXHAUSTIONS)
            // =========================================================================
            if ((OperatingMode == EngineMode.Hybrid_Dual_Engine || OperatingMode == EngineMode.Liquidity_Sweep_Only) && EnableSweepEngine)
            {
                double recent15MHigh = double.MinValue;
                double recent15MLow = double.MaxValue;
                int lookback = Math.Min(SweepLookbackBars, _bars15M.Count - 1);
                for (int i = 1; i <= lookback; i++)
                {
                    if (_bars15M.HighPrices.Last(i) > recent15MHigh) recent15MHigh = _bars15M.HighPrices.Last(i);
                    if (_bars15M.LowPrices.Last(i) < recent15MLow) recent15MLow = _bars15M.LowPrices.Last(i);
                }

                // Bearish Sweep: 15M High Swept & Closed Below
                if (lastBar.High > recent15MHigh && lastBar.Close < recent15MHigh)
                {
                    double entryPrice = lastBar.Close;
                    double stopLossPrice = recent15MHigh + (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(stopLossPrice - entryPrice) / Symbol.PipSize;

                    if (riskPips >= 2.0 && riskPips <= 25.0)
                    {
                        double rewardPips = riskPips * RiskRewardRatio;
                        double takeProfitPrice = entryPrice - (rewardPips * Symbol.PipSize);

                        ExecuteMarketOrderWithProtection(TradeType.Sell, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Sweep-Sell");
                        return;
                    }
                }
                // Bullish Sweep: 15M Low Swept & Closed Above
                else if (lastBar.Low < recent15MLow && lastBar.Close > recent15MLow)
                {
                    double entryPrice = lastBar.Close;
                    double stopLossPrice = recent15MLow - (InvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;

                    if (riskPips >= 2.0 && riskPips <= 25.0)
                    {
                        double rewardPips = riskPips * RiskRewardRatio;
                        double takeProfitPrice = entryPrice + (rewardPips * Symbol.PipSize);

                        ExecuteMarketOrderWithProtection(TradeType.Buy, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Sweep-Buy");
                        return;
                    }
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

            // Track High/Low Watermark for Trailing Stop
            if (position.TradeType == TradeType.Buy)
            {
                if (currentPrice > _highestPriceSinceEntry) _highestPriceSinceEntry = currentPrice;
            }
            else
            {
                if (currentPrice < _lowestPriceSinceEntry) _lowestPriceSinceEntry = currentPrice;
            }

            // 1. Breakeven Lock at +1.5R
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
                        Print("[Breakeven Locked] Stop Loss moved to {0:F5} (+{1:F1}R profit)", bePrice, currentGainR);
                    }
                }
            }

            // 2. Partial TP at +2.5R
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

            // 3. Dynamic ATR Chandelier Trailing Stop
            if (EnableAtrTrailingStop && _isBreakevenSet)
            {
                double currentAtr = _atr5M.Result.Last(1);
                double trailDistance = currentAtr * TrailingAtrMultiplier;

                if (position.TradeType == TradeType.Buy)
                {
                    double targetSl = Math.Round(_highestPriceSinceEntry - trailDistance, Symbol.Digits);
                    if (targetSl > position.StopLoss.Value && targetSl < Symbol.Bid - (2.0 * Symbol.PipSize))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit);
                        Print("[ATR Trail Ratchet] Buy SL moved up to {0:F5}", targetSl);
                    }
                }
                else
                {
                    double targetSl = Math.Round(_lowestPriceSinceEntry + trailDistance, Symbol.Digits);
                    if (targetSl < position.StopLoss.Value && targetSl > Symbol.Ask + (2.0 * Symbol.PipSize))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit);
                        Print("[ATR Trail Ratchet] Sell SL moved down to {0:F5}", targetSl);
                    }
                }
            }
        }

        private void PlaceHybridLimitOrder(TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice, double riskPips, double rewardPips, string setupLabel)
        {
            CancelAllPendingOrders();

            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, riskPips);

            double requiredMargin = volumeInUnits / 30.0;
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                volumeInUnits = (Account.FreeMargin * 0.85) * 30.0;
            }

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (volumeInUnits < Symbol.VolumeInUnitsMin) volumeInUnits = Symbol.VolumeInUnitsMin;

            entryPrice = Math.Round(entryPrice, Symbol.Digits);
            stopLossPrice = Math.Round(stopLossPrice, Symbol.Digits);
            takeProfitPrice = Math.Round(takeProfitPrice, Symbol.Digits);

            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, entryPrice, "HybridSweepMss", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _isPendingOrderActive = true;
                _pendingBarCounter = 0;
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                Print("[{0} Limit Placed] {1} at {2:F5} | SL: {3:F5} ({4:F1} p) | TP: {5:F5} ({6:F1} p)", 
                    setupLabel, tradeType, entryPrice, stopLossPrice, riskPips, takeProfitPrice, rewardPips);
            }
        }

        private void ExecuteMarketOrderWithProtection(TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice, double riskPips, double rewardPips, string setupLabel)
        {
            CancelAllPendingOrders();

            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, riskPips);

            double requiredMargin = volumeInUnits / 30.0;
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                volumeInUnits = (Account.FreeMargin * 0.85) * 30.0;
            }

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            if (volumeInUnits < Symbol.VolumeInUnitsMin) volumeInUnits = Symbol.VolumeInUnitsMin;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "HybridSweepMss", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                _highestPriceSinceEntry = entryPrice;
                _lowestPriceSinceEntry = entryPrice;
                Print("[{0} Market Entry] {1} at {2:F5} | SL: {3:F5} | TP: {4:F5}", setupLabel, tradeType, entryPrice, stopLossPrice, takeProfitPrice);
            }
        }

        private double CalculateVolumeUnits(double riskAmount, double riskPips)
        {
            if (riskPips <= 0) return Symbol.VolumeInUnitsMin;
            double pipValuePerUnit = Symbol.PipValue;
            double units = riskAmount / (riskPips * pipValuePerUnit);
            return units;
        }

        private void CancelAllPendingOrders()
        {
            foreach (var order in PendingOrders)
            {
                if (order.Label == "HybridSweepMss")
                    CancelPendingOrder(order);
            }
            _isPendingOrderActive = false;
        }

        private void CloseAllPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == "HybridSweepMss")
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
                    CancelAllPendingOrders();
                    CloseAllPositions();
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
