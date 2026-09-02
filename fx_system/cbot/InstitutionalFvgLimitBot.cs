#pragma warning disable CS0618
using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class InstitutionalFvgLimitBot : Robot
    {
        [Parameter("Max Risk % of Balance", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercentage { get; set; }

        [Parameter("Lookback Bars for Swing Liquidity", DefaultValue = 20, MinValue = 10, MaxValue = 50)]
        public int SwingLookback { get; set; }

        [Parameter("Min Imbalance (Pips)", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 10.0)]
        public double MinFvgPips { get; set; }

        [Parameter("Displacement Vol Multiplier", DefaultValue = 1.5, MinValue = 1.1, MaxValue = 3.0)]
        public double VolMultiplier { get; set; }

        [Parameter("Risk-Reward Ratio", DefaultValue = 2.5, MinValue = 1.5, MaxValue = 6.0)]
        public double RiskReward { get; set; }

        [Parameter("Cancel Pending Order After (Bars)", DefaultValue = 4, MinValue = 1, MaxValue = 15)]
        public int OrderExpiryBars { get; set; }

        [Parameter("Label", DefaultValue = "InstScalp_50FVG")]
        public string BotLabel { get; set; }

        private double _pipSize;

        protected override void OnStart()
        {
            _pipSize = Symbol.PipSize;
            Print("[Institutional FVG Limit Bot Started] Symbol: {0} | TimeFrame: {1}", SymbolName, TimeFrame);
        }

        protected override void OnBar()
        {
            // Clean up expired pending orders that were never mitigated
            CancelStaleOrders();

            // Only allow 1 active position or pending order at a time on small accounts
            if (Positions.FindAll(BotLabel, SymbolName).Length > 0 || PendingOrders.Any(o => o.Label == BotLabel && o.SymbolName == SymbolName))
                return;

            if (Bars.Count < SwingLookback + 15) return;

            int i = 1; // Completed bar

            // 1. Calculate Swing High and Low from prior completed bars
            double swingHigh = double.MinValue;
            double swingLow = double.MaxValue;

            for (int k = i + 2; k <= i + 2 + SwingLookback; k++)
            {
                if (Bars.HighPrices.Last(k) > swingHigh) swingHigh = Bars.HighPrices.Last(k);
                if (Bars.LowPrices.Last(k) < swingLow) swingLow = Bars.LowPrices.Last(k);
            }

            // 2. Average Body Calculation for Displacement
            double totalBody = 0;
            for (int k = i + 1; k <= i + 10; k++)
            {
                totalBody += Math.Abs(Bars.ClosePrices.Last(k) - Bars.OpenPrices.Last(k));
            }
            double avgBody = totalBody / 10.0;

            // 3. Bullish Scenario: Sweep Low -> Displacement Up -> Bullish FVG
            bool sweptSellSide = (Bars.LowPrices.Last(i + 1) < swingLow) && (Bars.ClosePrices.Last(i + 1) >= swingLow);
            bool bullishDisplacement = (Bars.ClosePrices.Last(i) > Bars.OpenPrices.Last(i)) &&
                                       ((Bars.ClosePrices.Last(i) - Bars.OpenPrices.Last(i)) >= avgBody * VolMultiplier);

            double bullishFvgGap = Bars.LowPrices.Last(i) - Bars.HighPrices.Last(i + 2);
            bool hasBullishFvg = bullishFvgGap >= (MinFvgPips * _pipSize);

            if (sweptSellSide && bullishDisplacement && hasBullishFvg)
            {
                // 50% Consequent Encroachment (Midpoint of the Gap between Bar i Low and Bar i+2 High)
                double fvgMidpoint = Bars.HighPrices.Last(i + 2) + (bullishFvgGap / 2.0);
                double stopLoss = Math.Min(Bars.LowPrices.Last(i + 1), Bars.LowPrices.Last(i)) - (1.0 * _pipSize);

                double risk = fvgMidpoint - stopLoss;
                if (risk > (2.0 * _pipSize)) // Ensure minimum viable stop distance
                {
                    double takeProfit = fvgMidpoint + (risk * RiskReward);
                    PlaceLimitOrder(TradeType.Buy, fvgMidpoint, stopLoss, takeProfit);
                }
                return;
            }

            // 4. Bearish Scenario: Sweep High -> Displacement Down -> Bearish FVG
            bool sweptBuySide = (Bars.HighPrices.Last(i + 1) > swingHigh) && (Bars.ClosePrices.Last(i + 1) <= swingHigh);
            bool bearishDisplacement = (Bars.ClosePrices.Last(i) < Bars.OpenPrices.Last(i)) &&
                                       ((Bars.OpenPrices.Last(i) - Bars.ClosePrices.Last(i)) >= avgBody * VolMultiplier);

            double bearishFvgGap = Bars.LowPrices.Last(i + 2) - Bars.HighPrices.Last(i);
            bool hasBearishFvg = bearishFvgGap >= (MinFvgPips * _pipSize);

            if (sweptBuySide && bearishDisplacement && hasBearishFvg)
            {
                // 50% Consequent Encroachment (Midpoint of the Gap between Bar i+2 Low and Bar i High)
                double fvgMidpoint = Bars.HighPrices.Last(i) + (bearishFvgGap / 2.0);
                double stopLoss = Math.Max(Bars.HighPrices.Last(i + 1), Bars.HighPrices.Last(i)) + (1.0 * _pipSize);

                double risk = stopLoss - fvgMidpoint;
                if (risk > (2.0 * _pipSize))
                {
                    double takeProfit = fvgMidpoint - (risk * RiskReward);
                    PlaceLimitOrder(TradeType.Sell, fvgMidpoint, stopLoss, takeProfit);
                }
            }
        }

        private void PlaceLimitOrder(TradeType tradeType, double targetPrice, double sl, double tp)
        {
            double riskAmount = Account.Balance * (RiskPercentage / 100.0);
            double slPips = Math.Abs(targetPrice - sl) / _pipSize;
            double tpPips = Math.Abs(targetPrice - tp) / _pipSize;

            if (slPips <= 0) return;

            // Volume calculation with 85% Free Margin buffer
            double pipValue = Symbol.PipValue;
            double volume = (slPips > 0 && pipValue > 0) ? (riskAmount / (slPips * pipValue)) : Symbol.VolumeInUnitsMin;

            double requiredMargin = volume / 30.0;
            if (requiredMargin > (Account.FreeMargin * 0.85))
            {
                volume = (Account.FreeMargin * 0.85) * 30.0;
            }

            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
            if (volume < Symbol.VolumeInUnitsMin)
                volume = Symbol.VolumeInUnitsMin;

            targetPrice = Math.Round(targetPrice, Symbol.Digits);
            sl = Math.Round(sl, Symbol.Digits);
            tp = Math.Round(tp, Symbol.Digits);

            // Expiration timestamp based on user parameter
            DateTime? expiry = Server.Time.AddMinutes(OrderExpiryBars * TimeFrame.ToMinutes());

            var result = PlaceLimitOrder(tradeType, SymbolName, volume, targetPrice, BotLabel, slPips, tpPips, expiry);
            if (result.IsSuccessful)
            {
                Print("[Limit Order Placed] {0} at {1:F5} | SL: {2:F5} ({3:F1}p) | TP: {4:F5} ({5:F1}p)", 
                    tradeType, targetPrice, sl, slPips, tp, tpPips);
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
