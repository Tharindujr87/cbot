#pragma warning disable CS0618
using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum ImbalanceEntryStyle
    {
        Midpoint_50,          // 50% Consequent Encroachment (Highest R:R)
        Proximal_Edge,        // Imbalance boundary edge (High fill rate)
        Market_On_Confirmed   // Immediate market execution on displacement close
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class InstitutionalFvgLimitBot : Robot
    {
        // =========================================================================
        // RISK & SPREAD CONTROLS
        // =========================================================================
        [Parameter("Risk % of Balance", Group = "Risk Controls", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercentage { get; set; }

        [Parameter("Risk-Reward Ratio", Group = "Risk Controls", DefaultValue = 3.5, MinValue = 1.5, MaxValue = 8.0)]
        public double RiskReward { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Risk Controls", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 6.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Cancel Pending Order (Bars)", Group = "Risk Controls", DefaultValue = 4, MinValue = 1, MaxValue = 20)]
        public int OrderExpiryBars { get; set; }

        // =========================================================================
        // DYNAMIC SMC & IMBALANCE ENGINE
        // =========================================================================
        [Parameter("Entry Execution Style", Group = "Dynamic Decision Engine", DefaultValue = ImbalanceEntryStyle.Market_On_Confirmed)]
        public ImbalanceEntryStyle EntryStyle { get; set; }

        [Parameter("Enable Macro Trend Filter (50 EMA)", Group = "Dynamic Decision Engine", DefaultValue = true)]
        public bool EnableTrendFilter { get; set; }

        [Parameter("Trend EMA Period", Group = "Dynamic Decision Engine", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Swing Liquidity Lookback", Group = "Dynamic Decision Engine", DefaultValue = 16, MinValue = 8, MaxValue = 50)]
        public int SwingLookback { get; set; }

        [Parameter("Sweep Detection Window (Bars)", Group = "Dynamic Decision Engine", DefaultValue = 3, MinValue = 1, MaxValue = 6)]
        public int SweepWindowBars { get; set; }

        [Parameter("Displacement Multiplier", Group = "Dynamic Decision Engine", DefaultValue = 1.20, MinValue = 0.8, MaxValue = 2.5)]
        public double VolMultiplier { get; set; }

        [Parameter("Min Imbalance (Pips)", Group = "Dynamic Decision Engine", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 10.0)]
        public double MinFvgPips { get; set; }

        [Parameter("Enable OrderBlock Retest Fallback", Group = "Dynamic Decision Engine", DefaultValue = true)]
        public bool EnableOrderBlockFallback { get; set; }

        // =========================================================================
        // TRADE PROTECTION & BREAKEVEN
        // =========================================================================
        [Parameter("Enable Breakeven Lock", Group = "Trade Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Breakeven Trigger (+R)", Group = "Trade Management", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Breakeven Offset (Pips)", Group = "Trade Management", DefaultValue = 1.0, MinValue = 0.0, MaxValue = 3.0)]
        public double BreakevenOffsetPips { get; set; }

        [Parameter("Enable Partial Take Profit", Group = "Trade Management", DefaultValue = false)]
        public bool EnablePartialTp { get; set; }

        [Parameter("Partial TP Trigger (+R)", Group = "Trade Management", DefaultValue = 2.5, MinValue = 1.0, MaxValue = 5.0)]
        public double PartialTpTriggerR { get; set; }

        [Parameter("Partial TP Volume %", Group = "Trade Management", DefaultValue = 50.0, MinValue = 10.0, MaxValue = 90.0)]
        public double PartialTpPercent { get; set; }

        [Parameter("Bot Label", Group = "System", DefaultValue = "InstSMC_GBPJPY")]
        public string BotLabel { get; set; }

        private double _pipSize;
        private AverageTrueRange _atr;
        private ExponentialMovingAverage _trendEma;
        private bool _isBreakevenSet;
        private bool _isPartialTpSet;

        protected override void OnStart()
        {
            _pipSize = Symbol.PipSize;
            _atr = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);
            _trendEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, TrendEmaPeriod);
            _isBreakevenSet = false;
            _isPartialTpSet = false;

            Print("[Smart Institutional SMC Bot Started] Symbol: {0} | TimeFrame: {1}", SymbolName, TimeFrame);
        }

        protected override void OnTick()
        {
            var activePosition = Positions.Find(BotLabel, SymbolName);
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
            CancelStaleOrders();

            double spreadPips = (Symbol.Ask - Symbol.Bid) / _pipSize;
            if (spreadPips > MaxSpreadPips) return;

            // Strict single position rule
            if (Positions.FindAll(BotLabel, SymbolName).Length > 0 || PendingOrders.Any(o => o.Label == BotLabel && o.SymbolName == SymbolName))
                return;

            if (Bars.Count < Math.Max(SwingLookback + 15, TrendEmaPeriod + 5)) return;

            int i = 1; // Completed bar index
            double currentAtr = _atr.Result.Last(1);
            double currentEma = _trendEma.Result.Last(1);
            double closePrice = Bars.ClosePrices.Last(i);

            // =========================================================================
            // 1. DYNAMIC SWING HIGH & LOW LIQUIDITY CALCULATION
            // =========================================================================
            double swingHigh = double.MinValue;
            double swingLow = double.MaxValue;

            for (int k = i + 2; k <= i + 2 + SwingLookback; k++)
            {
                if (Bars.HighPrices.Last(k) > swingHigh) swingHigh = Bars.HighPrices.Last(k);
                if (Bars.LowPrices.Last(k) < swingLow) swingLow = Bars.LowPrices.Last(k);
            }

            // =========================================================================
            // 2. DISPLACEMENT MOMENTUM CALCULATION
            // =========================================================================
            double totalBody = 0;
            for (int k = i + 1; k <= i + 10; k++)
            {
                totalBody += Math.Abs(Bars.ClosePrices.Last(k) - Bars.OpenPrices.Last(k));
            }
            double avgBody = totalBody / 10.0;

            // =========================================================================
            // 3. MULTI-BAR LIQUIDITY SWEEP DETECTION
            // =========================================================================
            bool sweptSellSide = false;
            double lowestSweepPrice = double.MaxValue;
            for (int w = 1; w <= SweepWindowBars; w++)
            {
                if (Bars.LowPrices.Last(i + w) < swingLow)
                {
                    sweptSellSide = true;
                    if (Bars.LowPrices.Last(i + w) < lowestSweepPrice)
                        lowestSweepPrice = Bars.LowPrices.Last(i + w);
                }
            }

            bool sweptBuySide = false;
            double highestSweepPrice = double.MinValue;
            for (int w = 1; w <= SweepWindowBars; w++)
            {
                if (Bars.HighPrices.Last(i + w) > swingHigh)
                {
                    sweptBuySide = true;
                    if (Bars.HighPrices.Last(i + w) > highestSweepPrice)
                        highestSweepPrice = Bars.HighPrices.Last(i + w);
                }
            }

            // Displacement Requirements (Body size or ATR expansion)
            double lastBody = Math.Abs(Bars.ClosePrices.Last(i) - Bars.OpenPrices.Last(i));
            bool isBullishDisplacement = (Bars.ClosePrices.Last(i) > Bars.OpenPrices.Last(i)) &&
                                        (lastBody >= (avgBody * VolMultiplier) || lastBody >= (0.45 * currentAtr));

            bool isBearishDisplacement = (Bars.ClosePrices.Last(i) < Bars.OpenPrices.Last(i)) &&
                                        (lastBody >= (avgBody * VolMultiplier) || lastBody >= (0.45 * currentAtr));

            // Dynamic Imbalance Gap (adapts to GBPJPY volatility)
            double dynamicMinGap = Math.Max(MinFvgPips * _pipSize, currentAtr * 0.20);

            // =========================================================================
            // 4. BULLISH OPPORTUNITY DECISION
            // =========================================================================
            bool isUptrend = !EnableTrendFilter || (closePrice > currentEma);

            if (sweptSellSide && isBullishDisplacement && Bars.ClosePrices.Last(i) > swingLow && isUptrend)
            {
                double bullishFvgGap = Bars.LowPrices.Last(i) - Bars.HighPrices.Last(i + 2);
                bool hasBullishFvg = bullishFvgGap >= dynamicMinGap;

                double entryPrice;
                if (hasBullishFvg)
                {
                    if (EntryStyle == ImbalanceEntryStyle.Proximal_Edge)
                        entryPrice = Bars.LowPrices.Last(i);
                    else if (EntryStyle == ImbalanceEntryStyle.Midpoint_50)
                        entryPrice = Bars.HighPrices.Last(i + 2) + (bullishFvgGap / 2.0);
                    else
                        entryPrice = Bars.ClosePrices.Last(i);
                }
                else if (EnableOrderBlockFallback)
                {
                    entryPrice = (Bars.LowPrices.Last(i) + Bars.ClosePrices.Last(i)) / 2.0;
                }
                else
                {
                    return;
                }

                double stopLoss = lowestSweepPrice - (2.0 * _pipSize);
                double risk = entryPrice - stopLoss;

                if (risk >= (3.0 * _pipSize))
                {
                    double takeProfit = entryPrice + (risk * RiskReward);
                    ExecuteOrPlaceOrder(TradeType.Buy, entryPrice, stopLoss, takeProfit);
                    return;
                }
            }

            // =========================================================================
            // 5. BEARISH OPPORTUNITY DECISION
            // =========================================================================
            bool isDowntrend = !EnableTrendFilter || (closePrice < currentEma);

            if (sweptBuySide && isBearishDisplacement && Bars.ClosePrices.Last(i) < swingHigh && isDowntrend)
            {
                double bearishFvgGap = Bars.LowPrices.Last(i + 2) - Bars.HighPrices.Last(i);
                bool hasBearishFvg = bearishFvgGap >= dynamicMinGap;

                double entryPrice;
                if (hasBearishFvg)
                {
                    if (EntryStyle == ImbalanceEntryStyle.Proximal_Edge)
                        entryPrice = Bars.HighPrices.Last(i);
                    else if (EntryStyle == ImbalanceEntryStyle.Midpoint_50)
                        entryPrice = Bars.HighPrices.Last(i) + (bearishFvgGap / 2.0);
                    else
                        entryPrice = Bars.ClosePrices.Last(i);
                }
                else if (EnableOrderBlockFallback)
                {
                    entryPrice = (Bars.HighPrices.Last(i) + Bars.ClosePrices.Last(i)) / 2.0;
                }
                else
                {
                    return;
                }

                double stopLoss = highestSweepPrice + (2.0 * _pipSize);
                double risk = stopLoss - entryPrice;

                if (risk >= (3.0 * _pipSize))
                {
                    double takeProfit = entryPrice - (risk * RiskReward);
                    ExecuteOrPlaceOrder(TradeType.Sell, entryPrice, stopLoss, takeProfit);
                }
            }
        }

        private void ManageOpenPosition(Position position)
        {
            if (!position.StopLoss.HasValue) return;

            double entryPrice = position.EntryPrice;
            double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            double initialRiskPips = Math.Abs(entryPrice - position.StopLoss.Value) / _pipSize;

            if (initialRiskPips <= 0) return;

            double currentGainPips = position.TradeType == TradeType.Buy 
                ? (currentPrice - entryPrice) / _pipSize 
                : (entryPrice - currentPrice) / _pipSize;

            double currentGainR = currentGainPips / initialRiskPips;

            // 1. One-Time Breakeven Lock (+1.5R)
            if (EnableBreakeven && !_isBreakevenSet && currentGainR >= BreakevenTriggerR)
            {
                double bePrice = position.TradeType == TradeType.Buy 
                    ? Math.Round(entryPrice + (BreakevenOffsetPips * _pipSize), Symbol.Digits) 
                    : Math.Round(entryPrice - (BreakevenOffsetPips * _pipSize), Symbol.Digits);

                bool isSafe = position.TradeType == TradeType.Buy 
                    ? (bePrice < Symbol.Bid - (2.0 * _pipSize)) 
                    : (bePrice > Symbol.Ask + (2.0 * _pipSize));

                if (isSafe)
                {
                    var modifyResult = ModifyPosition(position, bePrice, position.TakeProfit);
                    if (modifyResult.IsSuccessful)
                    {
                        _isBreakevenSet = true;
                        Print("[Breakeven Locked] Stop Loss moved to {0:F5} (+{1:F1}R)", bePrice, currentGainR);
                    }
                }
            }

            // 2. Optional Partial Take Profit (+2.5R)
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

        private void ExecuteOrPlaceOrder(TradeType tradeType, double targetPrice, double sl, double tp)
        {
            double riskAmount = Account.Balance * (RiskPercentage / 100.0);
            double slPips = Math.Abs(targetPrice - sl) / _pipSize;
            double tpPips = Math.Abs(targetPrice - tp) / _pipSize;

            if (slPips <= 0) return;

            // Pip value conversion
            double pipValue = Symbol.PipValue;
            double volume = (slPips > 0 && pipValue > 0) ? (riskAmount / (slPips * pipValue)) : Symbol.VolumeInUnitsMin;

            // Robust broker-accurate Margin Calculation
            double maxAllowedMargin = Account.FreeMargin * 0.80;
            double estimatedMargin = Symbol.GetEstimatedMargin(tradeType, volume);
            if (estimatedMargin > maxAllowedMargin && estimatedMargin > 0)
            {
                volume = volume * (maxAllowedMargin / estimatedMargin);
            }

            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
            if (volume < Symbol.VolumeInUnitsMin) volume = Symbol.VolumeInUnitsMin;

            // Final safety: check if minimum volume fits in free margin
            if (Symbol.GetEstimatedMargin(tradeType, volume) > Account.FreeMargin)
            {
                Print("[Margin Warning] Insufficient margin for minimum lot size on {0}", SymbolName);
                return;
            }

            targetPrice = Math.Round(targetPrice, Symbol.Digits);
            sl = Math.Round(sl, Symbol.Digits);
            tp = Math.Round(tp, Symbol.Digits);

            if (EntryStyle == ImbalanceEntryStyle.Market_On_Confirmed)
            {
                var marketResult = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel, slPips, tpPips);
                if (marketResult.IsSuccessful)
                {
                    _isBreakevenSet = false;
                    _isPartialTpSet = false;
                    Print("[Market Executed] {0} at {1:F5} | SL: {2:F5} ({3:F1}p) | TP: {4:F5} ({5:F1}p)", 
                        tradeType, targetPrice, sl, slPips, tp, tpPips);
                }
            }
            else
            {
                DateTime? expiry = Server.Time.AddMinutes(OrderExpiryBars * TimeFrame.ToMinutes());
                var limitResult = PlaceLimitOrder(tradeType, SymbolName, volume, targetPrice, BotLabel, slPips, tpPips, expiry);
                if (limitResult.IsSuccessful)
                {
                    _isBreakevenSet = false;
                    _isPartialTpSet = false;
                    Print("[Limit Placed ({0})] {1} at {2:F5} | SL: {3:F5} ({4:F1}p) | TP: {5:F5} ({6:F1}p)", 
                        EntryStyle, tradeType, targetPrice, sl, slPips, tp, tpPips);
                }
            }
        }

        private void CancelStaleOrders()
        {
            var pending = PendingOrders.Where(o => o.Label == BotLabel && o.SymbolName == SymbolName).ToArray();
            foreach (var order in pending)
            {
                if (order.ExpirationTime.HasValue && Server.Time >= order.ExpirationTime.Value)
                {
                    CancelPendingOrder(order);
                }
            }
        }
    }

    public static class TimeFrameExtensions
    {
        public static int ToMinutes(this TimeFrame tf)
        {
            if (tf == TimeFrame.Minute) return 1;
            if (tf == TimeFrame.Minute2) return 2;
            if (tf == TimeFrame.Minute3) return 3;
            if (tf == TimeFrame.Minute5) return 5;
            if (tf == TimeFrame.Minute10) return 10;
            if (tf == TimeFrame.Minute15) return 15;
            if (tf == TimeFrame.Minute30) return 30;
            if (tf == TimeFrame.Hour) return 60;
            if (tf == TimeFrame.Hour4) return 240;
            if (tf == TimeFrame.Daily) return 1440;
            return 15;
        }
    }
}
