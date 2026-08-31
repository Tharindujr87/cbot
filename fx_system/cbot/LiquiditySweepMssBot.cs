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

        // =========================================================================
        // STEP 1: DYNAMIC VOLUME DETECTION (No clock limits)
        // =========================================================================
        [Parameter("Min Relative Volume (RVol)", Group = "Step 1: Dynamic Volume", DefaultValue = 1.3, MinValue = 1.0, MaxValue = 3.0)]
        public double MinRelativeVolume { get; set; }

        [Parameter("Volume Baseline Period", Group = "Step 1: Dynamic Volume", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int VolumeBaselinePeriod { get; set; }

        [Parameter("Min Displacement ATR Mult", Group = "Step 1: Dynamic Volume", DefaultValue = 0.6, MinValue = 0.3, MaxValue = 2.0)]
        public double DisplacementAtrMult { get; set; }

        [Parameter("Min Candle Body % of Range", Group = "Step 1: Dynamic Volume", DefaultValue = 55.0, MinValue = 30.0, MaxValue = 85.0)]
        public double MinBodyPercent { get; set; }

        // =========================================================================
        // STEP 2 & 3: DIRECTION & PREMIUM / DISCOUNT ZONES
        // =========================================================================
        [Parameter("Dealing Range Lookback Bars", Group = "Step 2 & 3: Order Flow & Value Zones", DefaultValue = 30, MinValue = 10, MaxValue = 100)]
        public int DealingRangeLookback { get; set; }

        [Parameter("15M Macro Trend EMA", Group = "Step 2 & 3: Order Flow & Value Zones", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int TrendEmaPeriod { get; set; }

        [Parameter("5M Fast Pullback EMA", Group = "Step 2 & 3: Order Flow & Value Zones", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int FastEmaPeriod { get; set; }

        [Parameter("Enforce Strict OTE (62%-79%)", Group = "Step 2 & 3: Order Flow & Value Zones", DefaultValue = false)]
        public bool EnforceStrictOte { get; set; }

        // =========================================================================
        // STEP 4 - STRATEGY 1: BUY ENGINE
        // =========================================================================
        [Parameter("Enable Buy Strategy", Group = "Strategy 1: BUY Engine", DefaultValue = true)]
        public bool EnableBuyStrategy { get; set; }

        [Parameter("Buy Risk:Reward Ratio", Group = "Strategy 1: BUY Engine", DefaultValue = 5.0, MinValue = 1.5, MaxValue = 15.0)]
        public double BuyRiskRewardRatio { get; set; }

        [Parameter("Buy Invalidation Buffer (Pips)", Group = "Strategy 1: BUY Engine", DefaultValue = 4.0, MinValue = 0.5, MaxValue = 10.0)]
        public double BuyInvalidationBufferPips { get; set; }

        [Parameter("Buy Breakeven Trigger (+R)", Group = "Strategy 1: BUY Engine", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double BuyBreakevenTriggerR { get; set; }

        [Parameter("Buy Partial TP Trigger (+R)", Group = "Strategy 1: BUY Engine", DefaultValue = 2.5, MinValue = 1.5, MaxValue = 5.0)]
        public double BuyPartialTpTriggerR { get; set; }

        [Parameter("Buy Partial TP %", Group = "Strategy 1: BUY Engine", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double BuyPartialTpPercent { get; set; }

        [Parameter("Buy Dynamic ATR Trailing", Group = "Strategy 1: BUY Engine", DefaultValue = true)]
        public bool BuyEnableAtrTrailing { get; set; }

        [Parameter("Buy Trailing ATR Mult", Group = "Strategy 1: BUY Engine", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 4.0)]
        public double BuyTrailingAtrMult { get; set; }

        // =========================================================================
        // STEP 4 - STRATEGY 2: SELL ENGINE
        // =========================================================================
        [Parameter("Enable Sell Strategy", Group = "Strategy 2: SELL Engine", DefaultValue = true)]
        public bool EnableSellStrategy { get; set; }

        [Parameter("Sell Risk:Reward Ratio", Group = "Strategy 2: SELL Engine", DefaultValue = 4.5, MinValue = 1.5, MaxValue = 15.0)]
        public double SellRiskRewardRatio { get; set; }

        [Parameter("Sell Invalidation Buffer (Pips)", Group = "Strategy 2: SELL Engine", DefaultValue = 4.0, MinValue = 0.5, MaxValue = 10.0)]
        public double SellInvalidationBufferPips { get; set; }

        [Parameter("Sell Breakeven Trigger (+R)", Group = "Strategy 2: SELL Engine", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double SellBreakevenTriggerR { get; set; }

        [Parameter("Sell Partial TP Trigger (+R)", Group = "Strategy 2: SELL Engine", DefaultValue = 2.5, MinValue = 1.5, MaxValue = 5.0)]
        public double SellPartialTpTriggerR { get; set; }

        [Parameter("Sell Partial TP %", Group = "Strategy 2: SELL Engine", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double SellPartialTpPercent { get; set; }

        [Parameter("Sell Dynamic ATR Trailing", Group = "Strategy 2: SELL Engine", DefaultValue = true)]
        public bool SellEnableAtrTrailing { get; set; }

        [Parameter("Sell Trailing ATR Mult", Group = "Strategy 2: SELL Engine", DefaultValue = 1.8, MinValue = 1.0, MaxValue = 4.0)]
        public double SellTrailingAtrMult { get; set; }

        // =========================================================================
        // SYSTEM RISK & PROTECTION
        // =========================================================================
        [Parameter("Risk Per Trade %", Group = "Risk Controls", DefaultValue = 2.5, MinValue = 0.1, MaxValue = 20.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Max Pending Bars", Group = "Risk Controls", DefaultValue = 16, MinValue = 3, MaxValue = 40)]
        public int MaxPendingBars { get; set; }

        [Parameter("Circuit Breaker Drawdown %", Group = "Risk Controls", DefaultValue = 15.0, MinValue = 5.0, MaxValue = 40.0)]
        public double CircuitBreakerDrawdownPercent { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Risk Controls", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Config File Path", Group = "Risk Controls", DefaultValue = "strategy_config.json")]
        public string ConfigFilePath { get; set; }

        // Indicators & State Machine
        private Bars _bars15M;
        private AverageTrueRange _atr5M;
        private ExponentialMovingAverage _emaTrend15M;
        private ExponentialMovingAverage _emaFast5M;

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
            _emaTrend15M = Indicators.ExponentialMovingAverage(_bars15M.ClosePrices, TrendEmaPeriod);
            _emaFast5M = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastEmaPeriod);

            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Volume-Adaptive Institutional Engine Started] Symbol: {0} | Chart: {1}", BotSymbol, TimeFrame);
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
            var activePosition = Positions.Find("VolAdaptSMC", SymbolName);
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
                    Print("[Expiration] Pending FVG Limit Order expired after {0} bars.", MaxPendingBars);
                }
                return;
            }

            if (_bars15M.Count < 50 || Bars.Count < DealingRangeLookback + 5) return;

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            if (Positions.Find("VolAdaptSMC", SymbolName) != null) return;

            // =========================================================================
            // STEP 1: DYNAMIC RELATIVE VOLUME (RVol) & DISPLACEMENT SURGE
            // =========================================================================
            var lastBar = Bars.Last(1);
            double sumVol = 0;
            int vLookback = Math.Min(VolumeBaselinePeriod, Bars.Count - 2);
            for (int i = 2; i <= vLookback + 1; i++) sumVol += Bars.TickVolumes.Last(i);
            double avgVol = sumVol / vLookback;
            double rVol = avgVol > 0 ? (lastBar.TickVolume / avgVol) : 1.0;

            if (rVol < MinRelativeVolume) return; // Only proceed if institutional volume surge is detected

            double candleRange = lastBar.High - lastBar.Low;
            if (candleRange <= 0) candleRange = Symbol.PipSize;
            double bodySize = Math.Abs(lastBar.Close - lastBar.Open);
            double bodyPercent = (bodySize / candleRange) * 100.0;
            if (bodyPercent < MinBodyPercent) return; // Must be a decisive directional candle

            double currentAtr = _atr5M.Result.Last(1);
            if (bodySize < (DisplacementAtrMult * currentAtr)) return; // Must have sufficient expansion

            // =========================================================================
            // STEP 2: DEALING RANGE & PREMIUM / DISCOUNT ZONE CALCULATION
            // =========================================================================
            double rangeHigh = double.MinValue;
            double rangeLow = double.MaxValue;
            for (int i = 1; i <= DealingRangeLookback; i++)
            {
                if (Bars.HighPrices.Last(i) > rangeHigh) rangeHigh = Bars.HighPrices.Last(i);
                if (Bars.LowPrices.Last(i) < rangeLow) rangeLow = Bars.LowPrices.Last(i);
            }

            double totalDealingRange = rangeHigh - rangeLow;
            if (totalDealingRange <= 0) return;

            double equilibrium50 = rangeLow + (totalDealingRange * 0.50);
            double oteDiscount79 = rangeLow + (totalDealingRange * 0.382); // 62% - 79% retracement discount zone
            double otePremium79 = rangeLow + (totalDealingRange * 0.618);  // 62% - 79% retracement premium zone

            double m15TrendEma = _emaTrend15M.Result.Last(1);
            double m15Close = _bars15M.ClosePrices.Last(1);

            // =========================================================================
            // STEP 3 & 4: STRATEGY 1 - BUY ENGINE (Discount Value Execution)
            // =========================================================================
            if (EnableBuyStrategy && lastBar.Close > lastBar.Open) // Bullish Volume Displacement
            {
                bool isDiscountZone = EnforceStrictOte ? (lastBar.Close <= oteDiscount79) : (lastBar.Close <= equilibrium50);
                bool isTrendAligned = m15Close > m15TrendEma;

                // Confluence Confirmation: Break of Structure (BOS)
                double recentM5SwingHigh = Bars.HighPrices.Maximum(8);
                bool isBreakOfStructure = lastBar.Close >= recentM5SwingHigh;

                if (isDiscountZone && isTrendAligned && isBreakOfStructure)
                {
                    double fvgLower = Bars.Last(3).High;
                    double fvgUpper = Bars.Last(1).Low;

                    double entryPrice;
                    if (fvgUpper > fvgLower)
                        entryPrice = (fvgUpper + fvgLower) / 2.0; // 50% FVG Midpoint
                    else
                        entryPrice = (lastBar.Low + lastBar.Close) / 2.0; // 50% Candle Retracement

                    double swingLow = Bars.LowPrices.Minimum(6);
                    double stopLossPrice = swingLow - (BuyInvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;

                    if (riskPips >= 2.0 && riskPips <= 28.0)
                    {
                        double rewardPips = riskPips * BuyRiskRewardRatio;
                        double takeProfitPrice = entryPrice + (rewardPips * Symbol.PipSize);

                        PlaceOrder(TradeType.Buy, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "BUY-Discount-SMC");
                        Print("[Institutional BUY] Surge RVol: {0:F2} | Discount: {1:F5} | SL: {2:F5} | TP: {3:F5}", 
                            rVol, entryPrice, stopLossPrice, takeProfitPrice);
                        return;
                    }
                }
            }

            // =========================================================================
            // STEP 3 & 4: STRATEGY 2 - SELL ENGINE (Premium Value Execution)
            // =========================================================================
            if (EnableSellStrategy && lastBar.Close < lastBar.Open) // Bearish Volume Displacement
            {
                bool isPremiumZone = EnforceStrictOte ? (lastBar.Close >= otePremium79) : (lastBar.Close >= equilibrium50);
                bool isTrendAligned = m15Close < m15TrendEma;

                // Confluence Confirmation: Break of Structure (BOS)
                double recentM5SwingLow = Bars.LowPrices.Minimum(8);
                bool isBreakOfStructure = lastBar.Close <= recentM5SwingLow;

                if (isPremiumZone && isTrendAligned && isBreakOfStructure)
                {
                    double fvgUpper = Bars.Last(3).Low;
                    double fvgLower = Bars.Last(1).High;

                    double entryPrice;
                    if (fvgUpper > fvgLower)
                        entryPrice = (fvgUpper + fvgLower) / 2.0; // 50% FVG Midpoint
                    else
                        entryPrice = (lastBar.High + lastBar.Close) / 2.0; // 50% Candle Retracement

                    double swingHigh = Bars.HighPrices.Maximum(6);
                    double stopLossPrice = swingHigh + (SellInvalidationBufferPips * Symbol.PipSize);
                    double riskPips = Math.Abs(stopLossPrice - entryPrice) / Symbol.PipSize;

                    if (riskPips >= 2.0 && riskPips <= 28.0)
                    {
                        double rewardPips = riskPips * SellRiskRewardRatio;
                        double takeProfitPrice = entryPrice - (rewardPips * Symbol.PipSize);

                        PlaceOrder(TradeType.Sell, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "SELL-Premium-SMC");
                        Print("[Institutional SELL] Surge RVol: {0:F2} | Premium: {1:F5} | SL: {2:F5} | TP: {3:F5}", 
                            rVol, entryPrice, stopLossPrice, takeProfitPrice);
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

            // Track Extremes for Trailing Stop
            if (position.TradeType == TradeType.Buy)
            {
                if (currentPrice > _highestPriceSinceEntry) _highestPriceSinceEntry = currentPrice;
            }
            else
            {
                if (currentPrice < _lowestPriceSinceEntry) _lowestPriceSinceEntry = currentPrice;
            }

            bool isBuy = position.TradeType == TradeType.Buy;
            double beTriggerR = isBuy ? BuyBreakevenTriggerR : SellBreakevenTriggerR;
            double partialTriggerR = isBuy ? BuyPartialTpTriggerR : SellPartialTpTriggerR;
            double partialPercent = isBuy ? BuyPartialTpPercent : SellPartialTpPercent;
            bool enableTrailing = isBuy ? BuyEnableAtrTrailing : SellEnableAtrTrailing;
            double trailingAtrMult = isBuy ? BuyTrailingAtrMult : SellTrailingAtrMult;

            // 1. Asymmetric Breakeven Lock (+1.5R)
            if (EnableBreakeven && !_isBreakevenSet && currentGainR >= beTriggerR)
            {
                double bePrice = isBuy 
                    ? Math.Round(entryPrice + (0.5 * Symbol.PipSize), Symbol.Digits) 
                    : Math.Round(entryPrice - (0.5 * Symbol.PipSize), Symbol.Digits);

                bool isSafe = isBuy 
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

            // 2. Asymmetric Partial Take Profit (+2.5R)
            if (EnablePartialTp && !_isPartialTpSet && currentGainR >= partialTriggerR)
            {
                double volumeToClose = position.VolumeInUnits * (partialPercent / 100.0);
                volumeToClose = Symbol.NormalizeVolumeInUnits(volumeToClose, RoundingMode.Down);
                if (volumeToClose >= Symbol.VolumeInUnitsMin && volumeToClose < position.VolumeInUnits)
                {
                    var closeResult = ClosePosition(position, volumeToClose);
                    if (closeResult.IsSuccessful)
                    {
                        _isPartialTpSet = true;
                        Print("[Partial TP Banked] Closed {0} units ({1}%) at +{2:F1}R profit.", volumeToClose, partialPercent, currentGainR);
                    }
                }
            }

            // 3. Asymmetric ATR Chandelier Trailing Stop for Runners
            if (enableTrailing && _isBreakevenSet)
            {
                double currentAtr = _atr5M.Result.Last(1);
                double trailDistance = currentAtr * trailingAtrMult;

                if (isBuy)
                {
                    double targetSl = Math.Round(_highestPriceSinceEntry - trailDistance, Symbol.Digits);
                    if (targetSl > position.StopLoss.Value && targetSl < Symbol.Bid - (2.0 * Symbol.PipSize))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit);
                        Print("[ATR Trail Ratchet] Buy SL trailed to {0:F5}", targetSl);
                    }
                }
                else
                {
                    double targetSl = Math.Round(_lowestPriceSinceEntry + trailDistance, Symbol.Digits);
                    if (targetSl < position.StopLoss.Value && targetSl > Symbol.Ask + (2.0 * Symbol.PipSize))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit);
                        Print("[ATR Trail Ratchet] Sell SL trailed to {0:F5}", targetSl);
                    }
                }
            }
        }

        private void PlaceOrder(TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice, double riskPips, double rewardPips, string setupLabel)
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

            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, entryPrice, "VolAdaptSMC", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _isPendingOrderActive = true;
                _pendingBarCounter = 0;
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                Print("[{0}] Placed {1} Limit at {2:F5} | SL: {3:F5} ({4:F1} p) | TP: {5:F5} ({6:F1} p)", 
                    setupLabel, tradeType, entryPrice, stopLossPrice, riskPips, takeProfitPrice, rewardPips);
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
                if (order.Label == "VolAdaptSMC")
                    CancelPendingOrder(order);
            }
            _isPendingOrderActive = false;
        }

        private void CloseAllPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == "VolAdaptSMC")
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
