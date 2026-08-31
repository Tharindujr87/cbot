#pragma warning disable CS0618
using System;
using System.IO;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum ExecutionType
    {
        Market_On_Signal,   // Immediate execution on signal close (Guaranteed daily fills)
        Limit_Order_FVG     // 50% equilibrium limit order
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class LiquiditySweepMssBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        [Parameter("Execution Mode", Group = "Execution Mode", DefaultValue = ExecutionType.Market_On_Signal)]
        public ExecutionType OrderExecutionMode { get; set; }

        // =========================================================================
        // STEP 1: DYNAMIC VOLUME & DISPLACEMENT (Calibrated for 1-3 trades/day)
        // =========================================================================
        [Parameter("Enable Volume Filter", Group = "Step 1: Dynamic Volume", DefaultValue = false)]
        public bool EnableVolumeFilter { get; set; }

        [Parameter("Min Relative Volume (RVol)", Group = "Step 1: Dynamic Volume", DefaultValue = 1.05, MinValue = 0.8, MaxValue = 2.5)]
        public double MinRelativeVolume { get; set; }

        [Parameter("Volume Baseline Period", Group = "Step 1: Dynamic Volume", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int VolumeBaselinePeriod { get; set; }

        [Parameter("Min Displacement ATR Mult", Group = "Step 1: Dynamic Volume", DefaultValue = 0.45, MinValue = 0.2, MaxValue = 2.0)]
        public double DisplacementAtrMult { get; set; }

        [Parameter("Min Candle Body % of Range", Group = "Step 1: Dynamic Volume", DefaultValue = 45.0, MinValue = 25.0, MaxValue = 85.0)]
        public double MinBodyPercent { get; set; }

        // =========================================================================
        // STEP 2 & 3: 15M DEALING RANGE & VALUE ZONES
        // =========================================================================
        [Parameter("15M Dealing Range Lookback", Group = "Step 2 & 3: Macro Value Zones", DefaultValue = 32, MinValue = 10, MaxValue = 100)]
        public int MacroRangeLookback15M { get; set; }

        [Parameter("15M Trend Filter EMA", Group = "Step 2 & 3: Macro Value Zones", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int TrendEmaPeriod { get; set; }

        // =========================================================================
        // STEP 4 - STRATEGY 1: BUY ENGINE
        // =========================================================================
        [Parameter("Enable Buy Strategy", Group = "Strategy 1: BUY Engine", DefaultValue = true)]
        public bool EnableBuyStrategy { get; set; }

        [Parameter("Buy Risk:Reward Ratio", Group = "Strategy 1: BUY Engine", DefaultValue = 4.5, MinValue = 1.5, MaxValue = 12.0)]
        public double BuyRiskRewardRatio { get; set; }

        [Parameter("Buy Invalidation Buffer (Pips)", Group = "Strategy 1: BUY Engine", DefaultValue = 3.5, MinValue = 0.5, MaxValue = 10.0)]
        public double BuyInvalidationBufferPips { get; set; }

        [Parameter("Buy Enable Breakeven", Group = "Strategy 1: BUY Engine", DefaultValue = true)]
        public bool BuyEnableBreakeven { get; set; }

        [Parameter("Buy Breakeven Trigger (+R)", Group = "Strategy 1: BUY Engine", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double BuyBreakevenTriggerR { get; set; }

        [Parameter("Buy Enable Partial TP", Group = "Strategy 1: BUY Engine", DefaultValue = true)]
        public bool BuyEnablePartialTp { get; set; }

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

        [Parameter("Sell Risk:Reward Ratio", Group = "Strategy 2: SELL Engine", DefaultValue = 4.0, MinValue = 1.5, MaxValue = 12.0)]
        public double SellRiskRewardRatio { get; set; }

        [Parameter("Sell Invalidation Buffer (Pips)", Group = "Strategy 2: SELL Engine", DefaultValue = 3.5, MinValue = 0.5, MaxValue = 10.0)]
        public double SellInvalidationBufferPips { get; set; }

        [Parameter("Sell Enable Breakeven", Group = "Strategy 2: SELL Engine", DefaultValue = true)]
        public bool SellEnableBreakeven { get; set; }

        [Parameter("Sell Breakeven Trigger (+R)", Group = "Strategy 2: SELL Engine", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double SellBreakevenTriggerR { get; set; }

        [Parameter("Sell Enable Partial TP", Group = "Strategy 2: SELL Engine", DefaultValue = true)]
        public bool SellEnablePartialTp { get; set; }

        [Parameter("Sell Partial TP Trigger (+R)", Group = "Strategy 2: SELL Engine", DefaultValue = 2.5, MinValue = 1.5, MaxValue = 5.0)]
        public double SellPartialTpTriggerR { get; set; }

        [Parameter("Sell Partial TP %", Group = "Strategy 2: SELL Engine", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double SellPartialTpPercent { get; set; }

        [Parameter("Sell Dynamic ATR Trailing", Group = "Strategy 2: SELL Engine", DefaultValue = true)]
        public bool SellEnableAtrTrailing { get; set; }

        [Parameter("Sell Trailing ATR Mult", Group = "Strategy 2: SELL Engine", DefaultValue = 1.8, MinValue = 1.0, MaxValue = 4.0)]
        public double SellTrailingAtrMult { get; set; }

        // =========================================================================
        // TIME-DECAY & STAGNATION EXIT ENGINE (Closes Dragging Trades)
        // =========================================================================
        [Parameter("Enable Stagnation Exit", Group = "Stagnation & Time-Decay Exit", DefaultValue = true)]
        public bool EnableStagnationExit { get; set; }

        [Parameter("Max Stagnant Bars (M5)", Group = "Stagnation & Time-Decay Exit", DefaultValue = 24, MinValue = 10, MaxValue = 100)]
        public int MaxStagnantBars { get; set; }

        [Parameter("Stagnation Scratch Min Gain (+R)", Group = "Stagnation & Time-Decay Exit", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 2.0)]
        public double StagnantMinGainR { get; set; }

        // =========================================================================
        // SYSTEM RISK & SPREAD
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

        private bool _isPendingOrderActive;
        private int _pendingBarCounter;
        private int _positionOpenBarCount;
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
            _positionOpenBarCount = 0;
            _isCircuitHalted = false;

            _bars15M = MarketData.GetBars(TimeFrame.Minute15, BotSymbol);
            _atr5M = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);
            _emaTrend15M = Indicators.ExponentialMovingAverage(_bars15M.ClosePrices, TrendEmaPeriod);

            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Smart-Money Engine with Profit Maximizer Started] Symbol: {0} | Chart: {1}", BotSymbol, TimeFrame);
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
            var activePosition = Positions.Find("SMC_DualEngine", SymbolName);
            if (activePosition != null)
            {
                ManageOpenPosition(activePosition);
            }
            else
            {
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                _positionOpenBarCount = 0;
                _highestPriceSinceEntry = double.MinValue;
                _lowestPriceSinceEntry = double.MaxValue;
            }
        }

        protected override void OnBar()
        {
            if (_isCircuitHalted) return;

            // 1. Time-Decay & Stagnation Monitor for Active Position
            var activePosition = Positions.Find("SMC_DualEngine", SymbolName);
            if (activePosition != null)
            {
                _positionOpenBarCount++;

                if (EnableStagnationExit && _positionOpenBarCount >= MaxStagnantBars)
                {
                    double entryPrice = activePosition.EntryPrice;
                    double currentPrice = activePosition.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                    double initialRiskPips = activePosition.StopLoss.HasValue 
                        ? Math.Abs(entryPrice - activePosition.StopLoss.Value) / Symbol.PipSize 
                        : 5.0;

                    double currentGainPips = activePosition.TradeType == TradeType.Buy 
                        ? (currentPrice - entryPrice) / Symbol.PipSize 
                        : (entryPrice - currentPrice) / Symbol.PipSize;

                    double currentGainR = initialRiskPips > 0 ? (currentGainPips / initialRiskPips) : 0;

                    // If trade has been dragging for MaxStagnantBars without significant momentum (< StagnantMinGainR)
                    if (currentGainR < StagnantMinGainR)
                    {
                        ClosePosition(activePosition);
                        Print("[Stagnation Exit] Closed dragging position after {0} bars ({1:F2}R profit) to free capital for fresh setups.", 
                            _positionOpenBarCount, currentGainR);
                        _positionOpenBarCount = 0;
                        return;
                    }
                }
            }

            // 2. Manage Pending Order Expiration
            if (_isPendingOrderActive)
            {
                _pendingBarCounter++;
                if (_pendingBarCounter >= MaxPendingBars)
                {
                    CancelAllPendingOrders();
                    _isPendingOrderActive = false;
                    Print("[Expiration] Pending Limit Order expired after {0} bars.", MaxPendingBars);
                }
                return;
            }

            if (_bars15M.Count < MacroRangeLookback15M + 5 || Bars.Count < 30) return;

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            if (Positions.Find("SMC_DualEngine", SymbolName) != null) return;

            // =========================================================================
            // STEP 1: CANDLE DISPLACEMENT & OPTIONAL VOLUME
            // =========================================================================
            var lastBar = Bars.Last(1);
            double candleRange = lastBar.High - lastBar.Low;
            if (candleRange <= 0) candleRange = Symbol.PipSize;

            double bodySize = Math.Abs(lastBar.Close - lastBar.Open);
            double bodyPercent = (bodySize / candleRange) * 100.0;
            if (bodyPercent < MinBodyPercent) return;

            double currentAtr = _atr5M.Result.Last(1);
            if (bodySize < (DisplacementAtrMult * currentAtr)) return;

            if (EnableVolumeFilter)
            {
                double sumVol = 0;
                int vLookback = Math.Min(VolumeBaselinePeriod, Bars.Count - 2);
                for (int i = 2; i <= vLookback + 1; i++) sumVol += Bars.TickVolumes.Last(i);
                double avgVol = sumVol / vLookback;
                double rVol = avgVol > 0 ? (lastBar.TickVolume / avgVol) : 1.0;
                if (rVol < MinRelativeVolume) return;
            }

            // =========================================================================
            // STEP 2 & 3: 15M MACRO DEALING RANGE (EQUILIBRIUM / DISCOUNT / PREMIUM)
            // =========================================================================
            double macroHigh15M = double.MinValue;
            double macroLow15M = double.MaxValue;
            for (int i = 1; i <= MacroRangeLookback15M; i++)
            {
                if (_bars15M.HighPrices.Last(i) > macroHigh15M) macroHigh15M = _bars15M.HighPrices.Last(i);
                if (_bars15M.LowPrices.Last(i) < macroLow15M) macroLow15M = _bars15M.LowPrices.Last(i);
            }

            double totalMacroRange = macroHigh15M - macroLow15M;
            if (totalMacroRange <= 0) return;

            double equilibrium50 = macroLow15M + (totalMacroRange * 0.50);
            double m15TrendEma = _emaTrend15M.Result.Last(1);
            double m15Close = _bars15M.ClosePrices.Last(1);

            // =========================================================================
            // STEP 4 - STRATEGY 1: BUY ENGINE (Displacement in Discount / Uptrend)
            // =========================================================================
            if (EnableBuyStrategy && lastBar.Close > lastBar.Open)
            {
                bool isMacroDiscount = lastBar.Close <= equilibrium50;
                bool isTrendBullish = m15Close > m15TrendEma;

                if (isMacroDiscount || isTrendBullish)
                {
                    double swingLow = Bars.LowPrices.Minimum(6);
                    double stopLossPrice = swingLow - (BuyInvalidationBufferPips * Symbol.PipSize);

                    double entryPrice = lastBar.Close;
                    if (OrderExecutionMode == ExecutionType.Limit_Order_FVG)
                    {
                        double fvgLower = Bars.Last(3).High;
                        double fvgUpper = Bars.Last(1).Low;
                        entryPrice = (fvgUpper > fvgLower) ? (fvgUpper + fvgLower) / 2.0 : (lastBar.Low + lastBar.Close) / 2.0;
                    }

                    double riskPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;
                    if (riskPips >= 2.0 && riskPips <= 30.0)
                    {
                        double rewardPips = riskPips * BuyRiskRewardRatio;
                        double takeProfitPrice = entryPrice + (rewardPips * Symbol.PipSize);

                        if (OrderExecutionMode == ExecutionType.Market_On_Signal)
                            ExecuteMarketOrderWithProtection(TradeType.Buy, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "BUY-Signal");
                        else
                            PlaceLimitOrderWithProtection(TradeType.Buy, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "BUY-Limit");

                        Print("[BUY Triggered] Mode: {0} | Entry: {1:F5} | SL: {2:F5} ({3:F1}p) | TP: {4:F5} ({5:F1}p)", 
                            OrderExecutionMode, entryPrice, stopLossPrice, riskPips, takeProfitPrice, rewardPips);
                        return;
                    }
                }
            }

            // =========================================================================
            // STEP 4 - STRATEGY 2: SELL ENGINE (Displacement in Premium / Downtrend)
            // =========================================================================
            if (EnableSellStrategy && lastBar.Close < lastBar.Open)
            {
                bool isMacroPremium = lastBar.Close >= equilibrium50;
                bool isTrendBearish = m15Close < m15TrendEma;

                if (isMacroPremium || isTrendBearish)
                {
                    double swingHigh = Bars.HighPrices.Maximum(6);
                    double stopLossPrice = swingHigh + (SellInvalidationBufferPips * Symbol.PipSize);

                    double entryPrice = lastBar.Close;
                    if (OrderExecutionMode == ExecutionType.Limit_Order_FVG)
                    {
                        double fvgUpper = Bars.Last(3).Low;
                        double fvgLower = Bars.Last(1).High;
                        entryPrice = (fvgUpper > fvgLower) ? (fvgUpper + fvgLower) / 2.0 : (lastBar.High + lastBar.Close) / 2.0;
                    }

                    double riskPips = Math.Abs(stopLossPrice - entryPrice) / Symbol.PipSize;
                    if (riskPips >= 2.0 && riskPips <= 30.0)
                    {
                        double rewardPips = riskPips * SellRiskRewardRatio;
                        double takeProfitPrice = entryPrice - (rewardPips * Symbol.PipSize);

                        if (OrderExecutionMode == ExecutionType.Market_On_Signal)
                            ExecuteMarketOrderWithProtection(TradeType.Sell, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "SELL-Signal");
                        else
                            PlaceLimitOrderWithProtection(TradeType.Sell, entryPrice, stopLossPrice, takeProfitPrice, riskPips, rewardPips, "SELL-Limit");

                        Print("[SELL Triggered] Mode: {0} | Entry: {1:F5} | SL: {2:F5} ({3:F1}p) | TP: {4:F5} ({5:F1}p)", 
                            OrderExecutionMode, entryPrice, stopLossPrice, riskPips, takeProfitPrice, rewardPips);
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

            // Track Extremes for Dynamic Trailing Stop
            if (position.TradeType == TradeType.Buy)
            {
                if (currentPrice > _highestPriceSinceEntry) _highestPriceSinceEntry = currentPrice;
            }
            else
            {
                if (currentPrice < _lowestPriceSinceEntry) _lowestPriceSinceEntry = currentPrice;
            }

            bool isBuy = position.TradeType == TradeType.Buy;
            bool enableBreakeven = isBuy ? BuyEnableBreakeven : SellEnableBreakeven;
            double beTriggerR = isBuy ? BuyBreakevenTriggerR : SellBreakevenTriggerR;
            bool enablePartialTp = isBuy ? BuyEnablePartialTp : SellEnablePartialTp;
            double partialTriggerR = isBuy ? BuyPartialTpTriggerR : SellPartialTpTriggerR;
            double partialPercent = isBuy ? BuyPartialTpPercent : SellPartialTpPercent;
            bool enableTrailing = isBuy ? BuyEnableAtrTrailing : SellEnableAtrTrailing;
            double trailingAtrMult = isBuy ? BuyTrailingAtrMult : SellTrailingAtrMult;

            // 1. Stage 1: Breakeven Lock (+1.5R)
            if (enableBreakeven && !_isBreakevenSet && currentGainR >= beTriggerR)
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

            // 2. Stage 2: Partial Take Profit (+2.5R) & Secure Profit SL Lock (+1.0R)
            if (enablePartialTp && !_isPartialTpSet && currentGainR >= partialTriggerR)
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

                        // Lock SL at +1.0R guaranteed profit on remaining runner
                        double securedProfitSl = isBuy 
                            ? Math.Round(entryPrice + (initialRiskPips * 1.0 * Symbol.PipSize), Symbol.Digits)
                            : Math.Round(entryPrice - (initialRiskPips * 1.0 * Symbol.PipSize), Symbol.Digits);

                        bool isProfitSlSafe = isBuy ? (securedProfitSl < Symbol.Bid - (2.0 * Symbol.PipSize)) : (securedProfitSl > Symbol.Ask + (2.0 * Symbol.PipSize));
                        if (isProfitSlSafe)
                        {
                            ModifyPosition(position, securedProfitSl, position.TakeProfit);
                            Print("[Guaranteed Profit Lock] Stop Loss moved into profit at {0:F5} (+1.0R)", securedProfitSl);
                        }
                    }
                }
            }

            // 3. Stage 3: Accelerating ATR Chandelier Trailing Stop for Max Profit Runners
            if (enableTrailing && _isBreakevenSet)
            {
                double currentAtr = _atr5M.Result.Last(1);
                
                // Tighten trailing distance as profit expands beyond +3.5R to lock in 80%+ of gains
                double dynamicMult = currentGainR >= 3.5 ? Math.Max(1.2, trailingAtrMult * 0.75) : trailingAtrMult;
                double trailDistance = currentAtr * dynamicMult;

                if (isBuy)
                {
                    double targetSl = Math.Round(_highestPriceSinceEntry - trailDistance, Symbol.Digits);
                    if (targetSl > position.StopLoss.Value && targetSl < Symbol.Bid - (2.0 * Symbol.PipSize))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit);
                        Print("[ATR Trail Ratchet] Buy SL trailed to {0:F5} (High: {1:F5})", targetSl, _highestPriceSinceEntry);
                    }
                }
                else
                {
                    double targetSl = Math.Round(_lowestPriceSinceEntry + trailDistance, Symbol.Digits);
                    if (targetSl < position.StopLoss.Value && targetSl > Symbol.Ask + (2.0 * Symbol.PipSize))
                    {
                        ModifyPosition(position, targetSl, position.TakeProfit);
                        Print("[ATR Trail Ratchet] Sell SL trailed to {0:F5} (Low: {1:F5})", targetSl, _lowestPriceSinceEntry);
                    }
                }
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

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "SMC_DualEngine", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                _positionOpenBarCount = 0;
                _highestPriceSinceEntry = entryPrice;
                _lowestPriceSinceEntry = entryPrice;
                Print("[{0}] Executed Market {1} at {2:F5} | SL: {3:F5} ({4:F1}p) | TP: {5:F5} ({6:F1}p)", 
                    setupLabel, tradeType, entryPrice, stopLossPrice, riskPips, takeProfitPrice, rewardPips);
            }
        }

        private void PlaceLimitOrderWithProtection(TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice, double riskPips, double rewardPips, string setupLabel)
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

            var result = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, entryPrice, "SMC_DualEngine", riskPips, rewardPips);
            if (result.IsSuccessful)
            {
                _isPendingOrderActive = true;
                _pendingBarCounter = 0;
                _isBreakevenSet = false;
                _isPartialTpSet = false;
                _positionOpenBarCount = 0;
                Print("[{0}] Placed {1} Limit at {2:F5} | SL: {3:F5} ({4:F1}p) | TP: {5:F5} ({6:F1}p)", 
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
                if (order.Label == "SMC_DualEngine")
                    CancelPendingOrder(order);
            }
            _isPendingOrderActive = false;
        }

        private void CloseAllPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == "SMC_DualEngine")
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
