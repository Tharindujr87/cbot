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
        Proximal_Edge,        // Imbalance boundary edge (Highest fill rate)
        Market_On_Confirmed   // Immediate market execution on displacement close
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class InstitutionalFvgLimitBot : Robot
    {
        // =========================================================================
        // RISK & SPREAD CONTROLS
        // =========================================================================
        [Parameter("Risk % of Balance", Group = "Risk Controls", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercentage { get; set; }

        [Parameter("Risk-Reward Ratio", Group = "Risk Controls", DefaultValue = 2.5, MinValue = 1.5, MaxValue = 8.0)]
        public double RiskReward { get; set; }

        [Parameter("Max Spread (Pips)", Group = "Risk Controls", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 6.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Cancel Pending Order (Bars)", Group = "Risk Controls", DefaultValue = 6, MinValue = 1, MaxValue = 20)]
        public int OrderExpiryBars { get; set; }

        // =========================================================================
        // DYNAMIC SMC & IMBALANCE ENGINE
        // =========================================================================
        [Parameter("Entry Execution Style", Group = "Dynamic Decision Engine", DefaultValue = ImbalanceEntryStyle.Proximal_Edge)]
        public ImbalanceEntryStyle EntryStyle { get; set; }

        [Parameter("Swing Liquidity Lookback", Group = "Dynamic Decision Engine", DefaultValue = 20, MinValue = 8, MaxValue = 50)]
        public int SwingLookback { get; set; }

        [Parameter("Sweep Detection Window (Bars)", Group = "Dynamic Decision Engine", DefaultValue = 3, MinValue = 1, MaxValue = 6)]
        public int SweepWindowBars { get; set; }

        [Parameter("Displacement Multiplier", Group = "Dynamic Decision Engine", DefaultValue = 1.25, MinValue = 1.0, MaxValue = 2.5)]
        public double VolMultiplier { get; set; }

        [Parameter("Min Imbalance (Pips)", Group = "Dynamic Decision Engine", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0)]
        public double MinFvgPips { get; set; }

        [Parameter("Enable OrderBlock Retest Fallback", Group = "Dynamic Decision Engine", DefaultValue = true)]
        public bool EnableOrderBlockFallback { get; set; }

        // =========================================================================
        // TRADE PROTECTION & BREAKEVEN
        // =========================================================================
        [Parameter("Enable Breakeven Lock", Group = "Trade Management", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Breakeven Trigger (+R)", Group = "Trade Management", DefaultValue = 1.2, MinValue = 0.5, MaxValue = 3.0)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Breakeven Offset (Pips)", Group = "Trade Management", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 3.0)]
        public double BreakevenOffsetPips { get; set; }

        [Parameter("Bot Label", Group = "System", DefaultValue = "Dynamic_SMC_Bot")]
        public string BotLabel { get; set; }

        private double _pipSize;
        private AverageTrueRange _atr;
        private bool _isBreakevenSet;

        protected override void OnStart()
        {
            _pipSize = Symbol.PipSize;
            _atr = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);
            _isBreakevenSet = false;

            Print("[Dynamic Institutional SMC Bot Started] Symbol: {0} | Chart: {1} | Style: {2}", 
                SymbolName, TimeFrame, EntryStyle);
        }

        protected override void OnTick()
        {
            // Active Position Protection
            var activePosition = Positions.Find(BotLabel, SymbolName);
            if (activePosition != null)
            {
                ManageOpenPosition(activePosition);
            }
            else
            {
                _isBreakevenSet = false;
            }
        }

        protected override void OnBar()
        {
            // 1. Clean up stale unmitigated pending orders
            CancelStaleOrders();

            double spreadPips = (Symbol.Ask - Symbol.Bid) / _pipSize;
            if (spreadPips > MaxSpreadPips) return;

            // Only allow 1 trade or pending order at a time
            if (Positions.FindAll(BotLabel, SymbolName).Length > 0 || PendingOrders.Any(o => o.Label == BotLabel && o.SymbolName == SymbolName))
                return;

            if (Bars.Count < SwingLookback + 15) return;

            int i = 1; // Completed bar index
            double currentAtr = _atr.Result.Last(1);

            // =========================================================================
            // 1. DYNAMIC SWING HIGH & LOW CALCULATION
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
            // 3. MULTI-BAR FLEXIBLE LIQUIDITY SWEEP DETECTION
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
                                        (lastBody >= (avgBody * VolMultiplier) || lastBody >= (0.50 * currentAtr));

            bool isBearishDisplacement = (Bars.ClosePrices.Last(i) < Bars.OpenPrices.Last(i)) &&
                                        (lastBody >= (avgBody * VolMultiplier) || lastBody >= (0.50 * currentAtr));

            // Dynamic Imbalance Gap (adapts to volatility)
            double dynamicMinGap = Math.Max(MinFvgPips * _pipSize, currentAtr * 0.20);

            // =========================================================================
            // 4. BULLISH OPPORTUNITY DECISION
            // =========================================================================
            if (sweptSellSide && isBullishDisplacement && Bars.ClosePrices.Last(i) > swingLow)
            {
                double bullishFvgGap = Bars.LowPrices.Last(i) - Bars.HighPrices.Last(i + 2);
                bool hasBullishFvg = bullishFvgGap >= dynamicMinGap;

                double entryPrice;
                if (hasBullishFvg)
                {
                    if (EntryStyle == ImbalanceEntryStyle.Proximal_Edge)
                        entryPrice = Bars.LowPrices.Last(i); // Boundary
                    else if (EntryStyle == ImbalanceEntryStyle.Midpoint_50)
                        entryPrice = Bars.HighPrices.Last(i + 2) + (bullishFvgGap / 2.0); // 50% Consequent Encroachment
                    else
                        entryPrice = Bars.ClosePrices.Last(i);
                }
                else if (EnableOrderBlockFallback)
                {
                    // Fallback: 50% retracement of the displacement candle
                    entryPrice = (Bars.LowPrices.Last(i) + Bars.ClosePrices.Last(i)) / 2.0;
                }
                else
                {
                    return;
                }

                double stopLoss = lowestSweepPrice - (1.5 * _pipSize);
                double risk = entryPrice - stopLoss;

                if (risk >= (2.5 * _pipSize))
                {
                    double takeProfit = entryPrice + (risk * RiskReward);
                    ExecuteOrPlaceOrder(TradeType.Buy, entryPrice, stopLoss, takeProfit);
                    return;
                }
            }

            // =========================================================================
            // 5. BEARISH OPPORTUNITY DECISION
            // =========================================================================
            if (sweptBuySide && isBearishDisplacement && Bars.ClosePrices.Last(i) < swingHigh)
            {
                double bearishFvgGap = Bars.LowPrices.Last(i + 2) - Bars.HighPrices.Last(i);
                bool hasBearishFvg = bearishFvgGap >= dynamicMinGap;

                double entryPrice;
                if (hasBearishFvg)
                {
                    if (EntryStyle == ImbalanceEntryStyle.Proximal_Edge)
                        entryPrice = Bars.HighPrices.Last(i); // Boundary
                    else if (EntryStyle == ImbalanceEntryStyle.Midpoint_50)
                        entryPrice = Bars.HighPrices.Last(i) + (bearishFvgGap / 2.0); // 50% Consequent Encroachment
                    else
                        entryPrice = Bars.ClosePrices.Last(i);
                }
                else if (EnableOrderBlockFallback)
                {
                    // Fallback: 50% retracement of the displacement candle
                    entryPrice = (Bars.HighPrices.Last(i) + Bars.ClosePrices.Last(i)) / 2.0;
                }
                else
                {
                    return;
                }

                double stopLoss = highestSweepPrice + (1.5 * _pipSize);
                double risk = stopLoss - entryPrice;

                if (risk >= (2.5 * _pipSize))
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

            // One-Time Breakeven Lock (+1.2R)
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
        }

        private void ExecuteOrPlaceOrder(TradeType tradeType, double targetPrice, double sl, double tp)
        {
            double riskAmount = Account.Balance * (RiskPercentage / 100.0);
            double slPips = Math.Abs(targetPrice - sl) / _pipSize;
            double tpPips = Math.Abs(targetPrice - tp) / _pipSize;

            if (slPips <= 0) return;

            // Dynamic volume sizing with 85% free margin buffer
            double pipValue = Symbol.PipValue;
            double volume = (slPips > 0 && pipValue > 0) ? (riskAmount / (slPips * pipValue)) : Symbol.VolumeInUnitsMin;

            double requiredMargin = volume / 30.0;
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                volume = (Account.FreeMargin * 0.85) * 30.0;
            }

            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
            if (volume < Symbol.VolumeInUnitsMin) volume = Symbol.VolumeInUnitsMin;

            targetPrice = Math.Round(targetPrice, Symbol.Digits);
            sl = Math.Round(sl, Symbol.Digits);
            tp = Math.Round(tp, Symbol.Digits);

            if (EntryStyle == ImbalanceEntryStyle.Market_On_Confirmed)
            {
                var marketResult = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel, slPips, tpPips);
                if (marketResult.IsSuccessful)
                {
                    _isBreakevenSet = false;
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
            return 5;
        }
    }
}
