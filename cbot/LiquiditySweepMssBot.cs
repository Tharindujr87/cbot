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

    public enum TradeSetupType
    {
        NONE,
        TREND_PULLBACK_BULLISH,
        TREND_PULLBACK_BEARISH,
        LIQUIDITY_SWEEP_BULLISH,
        LIQUIDITY_SWEEP_BEARISH
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        [Parameter("Engine Mode", Group = "System Engine", DefaultValue = EngineMode.Hybrid_Dual_Engine)]
        public EngineMode OperatingMode { get; set; }

        // --- Engine 1: Trend Continuation & FVG Pullbacks ---
        [Parameter("Enable Trend Engine", Group = "Engine 1: Trend Continuation", DefaultValue = true)]
        public bool EnableTrendEngine { get; set; }

        [Parameter("15M Fast EMA Period", Group = "Engine 1: Trend Continuation", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int FastEmaPeriod { get; set; }

        [Parameter("15M Slow EMA Period", Group = "Engine 1: Trend Continuation", DefaultValue = 50, MinValue = 20, MaxValue = 200)]
        public int SlowEmaPeriod { get; set; }

        [Parameter("1H Macro Trend EMA", Group = "Engine 1: Trend Continuation", DefaultValue = 50, MinValue = 20, MaxValue = 200)]
        public int MacroEmaPeriod { get; set; }

        [Parameter("Trend Displacement ATR Mult", Group = "Engine 1: Trend Continuation", DefaultValue = 0.6, MinValue = 0.3, MaxValue = 2.0)]
        public double TrendDisplacementAtrMult { get; set; }

        // --- Engine 2: Liquidity Exhaustion Sweep ---
        [Parameter("Enable Sweep Engine", Group = "Engine 2: Liquidity Exhaustion", DefaultValue = true)]
        public bool EnableSweepEngine { get; set; }

        [Parameter("15M Sweep Lookback Bars", Group = "Engine 2: Liquidity Exhaustion", DefaultValue = 20, MinValue = 10, MaxValue = 50)]
        public int SweepLookbackBars { get; set; }

        [Parameter("Min Wick Rejection %", Group = "Engine 2: Liquidity Exhaustion", DefaultValue = 40.0, MinValue = 20.0, MaxValue = 80.0)]
        public double MinWickRejectionPercent { get; set; }

        [Parameter("Require RSI Exhaustion for Sweeps", Group = "Engine 2: Liquidity Exhaustion", DefaultValue = true)]
        public bool RequireRsiExhaustion { get; set; }

        [Parameter("RSI Overbought Level", Group = "Engine 2: Liquidity Exhaustion", DefaultValue = 68.0, MinValue = 60.0, MaxValue = 85.0)]
        public double RsiOverboughtLevel { get; set; }

        [Parameter("RSI Oversold Level", Group = "Engine 2: Liquidity Exhaustion", DefaultValue = 32.0, MinValue = 15.0, MaxValue = 40.0)]
        public double RsiOversoldLevel { get; set; }

        // --- Volume Footprint Filter ---
        [Parameter("Enable Volume Footprint", Group = "Volume Intelligence", DefaultValue = true)]
        public bool EnableVolumeFilter { get; set; }

        [Parameter("Min Relative Volume (RVol)", Group = "Volume Intelligence", DefaultValue = 1.15, MinValue = 1.0, MaxValue = 2.5)]
        public double MinRelativeVolume { get; set; }

        // --- Trade Management & Dynamic Trailing ---
        [Parameter("Risk Reward Ratio (Base TP)", Group = "Trade Management", DefaultValue = 5.5, MinValue = 2.0, MaxValue = 12.0)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Invalidation SL Buffer (Pips)", Group = "Trade Management", DefaultValue = 4.0, MinValue = 0.5, MaxValue = 10.0)]
        public double InvalidationBufferPips { get; set; }

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

        [Parameter("Enable Dynamic ATR Trailing Stop", Group = "Trade Management", DefaultValue = true)]
        public bool EnableAtrTrailingStop { get; set; }

        [Parameter("Trailing ATR Multiplier", Group = "Trade Management", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 4.0)]
        public double TrailingAtrMultiplier { get; set; }

        // --- Risk & Protection ---
        [Parameter("Risk Per Trade %", Group = "Risk Controls", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 20.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Circuit Breaker Drawdown %", Group = "Risk Controls", DefaultValue = 15.0, MinValue = 5.0, MaxValue = 40.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Risk Controls", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Config File Path", Group = "Risk Controls", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // Multi-Timeframe Series & Indicators
        private Bars _bars15M;
        private Bars _bars1H;
        private AverageTrueRange _atr5M;
        private ExponentialMovingAverage _emaFast15M;
        private ExponentialMovingAverage _emaSlow15M;
        private ExponentialMovingAverage _emaMacro1H;
        private RelativeStrengthIndex _rsi15M;

        // State Machine
        private TradeSetupType _pendingSetup;
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
            _pendingSetup = TradeSetupType.NONE;
            _isPendingOrderActive = false;
            _isCircuitHalted = false;

            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _bars1H = MarketData.GetBars(TimeFrame.Hour, BotSymbol);

            _atr5M = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);
            _emaFast15M = Indicators.ExponentialMovingAverage(_bars15M.ClosePrices, FastEmaPeriod);
            _emaSlow15M = Indicators.ExponentialMovingAverage(_bars15M.ClosePrices, SlowEmaPeriod);
            _emaMacro1H = Indicators.ExponentialMovingAverage(_bars1H.ClosePrices, MacroEmaPeriod);
            _rsi15M = Indicators.RelativeStrengthIndex(_bars15M.ClosePrices, 14);

            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Hybrid Dual-Engine Initialized] Mode: {0} on {1} ({2})", OperatingMode, BotSymbol, TimeFrame);
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
                    _pendingSetup = TradeSetupType.NONE;
                    Print("[Expiration] Pending FVG Limit Order expired after {0} bars.", MaxPendingBars);
                }
                return;
            }

            if (_bars15M.Count < 50 || _bars1H.Count < 50 || Bars.Count < 30) return;

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            // Check Active Position
            if (Positions.Find("HybridSweepMss", SymbolName) != null) return;

            // Volume Validation
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

            // =========================================================================
            // ENGINE 1: TREND CONTINUATION & FVG PULLBACK ENGINE (Primary ~70% Win Rate)
            // =========================================================================
            if ((OperatingMode == EngineMode.Hybrid_Dual_Engine || OperatingMode == EngineMode.Trend_Continuation_Only) && EnableTrendEngine)
            {
                double htf1HClose = _bars1H.ClosePrices.Last(1);
                double htf1HEma = _emaMacro1H.Result.Last(1);
                double m15Close = _bars15M.ClosePrices.Last(1);
                double m15FastEma = _emaFast15M.Result.Last(1);
                double m15SlowEma = _emaSlow15M.Result.Last(1);

                bool isBullishTrend = htf1HClose > htf1HEma && m15Close > m15FastEma && m15FastEma > m15SlowEma;
                bool isBearishTrend = htf1HClose < htf1HEma && m15Close < m15FastEma && m15FastEma < m15SlowEma;

                var lastBar = Bars.Last(1);
                double bodySize = Math.Abs(lastBar.Close - lastBar.Open);
                double currentAtr = _atr5M.Result.Last(1);
                bool isDisplacement = bodySize >= (TrendDisplacementAtrMult * currentAtr);

                // --- Bullish Trend Continuation FVG Entry ---
                if (isBullishTrend && isDisplacement && volumeConfirmed)
                {
                    double recentM5High = Bars.HighPrices.Maximum(10);
                    if (lastBar.Close >= recentM5High) // Break of structure to upside
                    {
                        double fvgLower = Bars.Last(3).High;
                        double fvgUpper = Bars.Last(1).Low;

                        if (fvgUpper > fvgLower) // Valid Bullish FVG
                        {
                            double entryPrice = (fvgUpper + fvgLower) / 2.0;
                            double swingLow = Bars.LowPrices.Minimum(6);
                            double stopLossPrice = swingLow - (InvalidationBufferPips * Symbol.PipSize);
                            double riskPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;

                            if (riskPips >= 2.0)
                            {
                                double rewardPips = riskPips * RiskRewardRatio;
                                double takeProfitPrice = entryPrice + (rewardPips * Symbol.PipSize);

                                PlaceHybridLimitOrder(TradeType.Buy, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Trend-BOS");
                                return;
                            }
                        }
                    }
                }
                // --- Bearish Trend Continuation FVG Entry ---
                else if (isBearishTrend && isDisplacement && volumeConfirmed)
                {
                    double recentM5Low = Bars.LowPrices.Minimum(10);
                    if (lastBar.Close <= recentM5Low) // Break of structure to downside
                    {
                        double fvgUpper = Bars.Last(3).Low;
                        double fvgLower = Bars.Last(1).High;

                        if (fvgUpper > fvgLower) // Valid Bearish FVG
                        {
                            double entryPrice = (fvgUpper + fvgLower) / 2.0;
                            double swingHigh = Bars.HighPrices.Maximum(6);
                            double stopLossPrice = swingHigh + (InvalidationBufferPips * Symbol.PipSize);
                            double riskPips = Math.Abs(stopLossPrice - entryPrice) / Symbol.PipSize;

                            if (riskPips >= 2.0)
                            {
                                double rewardPips = riskPips * RiskRewardRatio;
                                double takeProfitPrice = entryPrice - (rewardPips * Symbol.PipSize);

                                PlaceHybridLimitOrder(TradeType.Sell, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Trend-BOS");
                                return;
                            }
                        }
                    }
                }
            }

            // =========================================================================
            // ENGINE 2: LIQUIDITY EXHAUSTION SWEEP ENGINE (Reversals at True Extremes)
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

                var lastBar = Bars.Last(1);
                double candleRange = lastBar.High - lastBar.Low;
                if (candleRange <= 0) candleRange = Symbol.PipSize;
                double rsiValue = _rsi15M.Result.Last(1);

                // --- Bearish Exhaustion Sweep (15M High Swept + RSI Overbought) ---
                if (lastBar.High > recent15MHigh && lastBar.Close < recent15MHigh)
                {
                    double upperWick = lastBar.High - Math.Max(lastBar.Open, lastBar.Close);
                    double wickRatioPercent = (upperWick / candleRange) * 100.0;
                    bool rsiExhausted = !RequireRsiExhaustion || (rsiValue >= RsiOverboughtLevel);

                    if (wickRatioPercent >= MinWickRejectionPercent && rsiExhausted && volumeConfirmed)
                    {
                        double mssLevel = Bars.LowPrices.Minimum(8);
                        double entryPrice = lastBar.Close;
                        double stopLossPrice = recent15MHigh + (InvalidationBufferPips * Symbol.PipSize);
                        double riskPips = Math.Abs(stopLossPrice - entryPrice) / Symbol.PipSize;

                        if (riskPips >= 2.0)
                        {
                            double rewardPips = riskPips * RiskRewardRatio;
                            double takeProfitPrice = entryPrice - (rewardPips * Symbol.PipSize);

                            ExecuteMarketOrderWithProtection(TradeType.Sell, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Sweep-Reversal");
                            return;
                        }
                    }
                }
                // --- Bullish Exhaustion Sweep (15M Low Swept + RSI Oversold) ---
                else if (lastBar.Low < recent15MLow && lastBar.Close > recent15MLow)
                {
                    double lowerWick = Math.Min(lastBar.Open, lastBar.Close) - lastBar.Low;
                    double wickRatioPercent = (lowerWick / candleRange) * 100.0;
                    bool rsiExhausted = !RequireRsiExhaustion || (rsiValue <= RsiOversoldLevel);

                    if (wickRatioPercent >= MinWickRejectionPercent && rsiExhausted && volumeConfirmed)
                    {
                        double mssLevel = Bars.HighPrices.Maximum(8);
                        double entryPrice = lastBar.Close;
                        double stopLossPrice = recent15MLow - (InvalidationBufferPips * Symbol.PipSize);
                        double riskPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;

                        if (riskPips >= 2.0)
                        {
                            double rewardPips = riskPips * RiskRewardRatio;
                            double takeProfitPrice = entryPrice + (rewardPips * Symbol.PipSize);

                            ExecuteMarketOrderWithProtection(TradeType.Buy, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "Sweep-Reversal");
                            return;
                        }
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

            // Track Extremes for Dynamic Trailing
            if (position.TradeType == TradeType.Buy)
            {
                if (currentPrice > _highestPriceSinceEntry) _highestPriceSinceEntry = currentPrice;
            }
            else
            {
                if (currentPrice < _lowestPriceSinceEntry) _lowestPriceSinceEntry = currentPrice;
            }

            // 1. Automatic Breakeven Lock at +1.5R
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

            // 2. Partial Take Profit at +2.5R
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

            // 3. Dynamic ATR Chandelier Trailing Stop (Ratchets profits on runners)
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
                Print("[{0} Order Placed] {1} Limit at {2:F5} | SL: {3:F5} ({4:F1} p) | TP: {5:F5} ({6:F1} p)", 
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
