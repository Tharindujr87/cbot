#pragma warning disable CS0618
using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class SimpleEmaRsiBot : Robot
    {
        [Parameter("Symbol Name", DefaultValue = "EURUSD")]
        public string BotSymbol { get; set; }

        [Parameter("Fast EMA Period", DefaultValue = 9, MinValue = 3, MaxValue = 50)]
        public int FastEmaPeriod { get; set; }

        [Parameter("Slow EMA Period", DefaultValue = 21, MinValue = 10, MaxValue = 200)]
        public int SlowEmaPeriod { get; set; }

        [Parameter("Trend EMA (Filter)", DefaultValue = 200, MinValue = 50, MaxValue = 500)]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Enable 200 EMA Filter", DefaultValue = true)]
        public bool EnableTrendFilter { get; set; }

        [Parameter("RSI Period", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Long Level (>)", DefaultValue = 50.0, MinValue = 40.0, MaxValue = 65.0)]
        public double RsiLongLevel { get; set; }

        [Parameter("RSI Short Level (<)", DefaultValue = 50.0, MinValue = 35.0, MaxValue = 60.0)]
        public double RsiShortLevel { get; set; }

        [Parameter("ATR Period (SL/TP)", DefaultValue = 14, MinValue = 5, MaxValue = 30)]
        public int AtrPeriod { get; set; }

        [Parameter("Stop Loss ATR Mult", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double StopLossAtrMult { get; set; }

        [Parameter("Take Profit ATR Mult", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 10.0)]
        public double TakeProfitAtrMult { get; set; }

        [Parameter("Enable Breakeven at 1R", DefaultValue = true)]
        public bool EnableBreakeven { get; set; }

        [Parameter("Risk Per Trade %", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 10.0)]
        public double RiskPerTradePercent { get; set; }

        // Indicators
        private ExponentialMovingAverage _fastEma;
        private ExponentialMovingAverage _slowEma;
        private ExponentialMovingAverage _trendEma;
        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;

        protected override void OnStart()
        {
            _fastEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastEmaPeriod);
            _slowEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SlowEmaPeriod);
            _trendEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, TrendEmaPeriod);
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);

            Print("[Simple EMA + RSI Bot] Initialized on {0} ({1})", SymbolName, TimeFrame);
        }

        protected override void OnTick()
        {
            // Active Position Breakeven Management
            if (EnableBreakeven)
            {
                var position = Positions.Find("SimpleEmaRsi", SymbolName);
                if (position != null && position.StopLoss.HasValue)
                {
                    double entryPrice = position.EntryPrice;
                    double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                    double initialRisk = Math.Abs(entryPrice - position.StopLoss.Value);

                    if (initialRisk > 0)
                    {
                        double currentProfit = position.TradeType == TradeType.Buy 
                            ? (currentPrice - entryPrice) 
                            : (entryPrice - currentPrice);

                        // If profit >= 1.0x initial risk, move Stop Loss to Breakeven (+0.5 pips)
                        if (currentProfit >= initialRisk)
                        {
                            double bePrice = position.TradeType == TradeType.Buy 
                                ? entryPrice + (0.5 * Symbol.PipSize) 
                                : entryPrice - (0.5 * Symbol.PipSize);

                            bool shouldUpdate = position.TradeType == TradeType.Buy 
                                ? position.StopLoss.Value < bePrice 
                                : position.StopLoss.Value > bePrice;

                            if (shouldUpdate)
                            {
                                ModifyPosition(position, bePrice, position.TakeProfit);
                                Print("[Breakeven Locked] Position SL moved to {0:F5}", bePrice);
                            }
                        }
                    }
                }
            }
        }

        protected override void OnBar()
        {
            int requiredBars = Math.Max(TrendEmaPeriod, Math.Max(SlowEmaPeriod, RsiPeriod)) + 5;
            if (Bars.Count < requiredBars)
                return;

            // Prior completed bar values
            double fastPrev = _fastEma.Result.Last(2);
            double fastCurr = _fastEma.Result.Last(1);

            double slowPrev = _slowEma.Result.Last(2);
            double slowCurr = _slowEma.Result.Last(1);

            double rsiVal = _rsi.Result.Last(1);
            double closeVal = Bars.ClosePrices.Last(1);
            double trendEmaVal = _trendEma.Result.Last(1);
            double atrVal = _atr.Result.Last(1);

            // Fast EMA crosses above Slow EMA
            bool bullishCross = (fastPrev <= slowPrev) && (fastCurr > slowCurr);
            // Fast EMA crosses below Slow EMA
            bool bearishCross = (fastPrev >= slowPrev) && (fastCurr < slowCurr);

            var activePosition = Positions.Find("SimpleEmaRsi", SymbolName);

            // --- BUY ENTRY ---
            if (bullishCross)
            {
                // Close opposite Sell trade if open
                if (activePosition != null && activePosition.TradeType == TradeType.Sell)
                {
                    ClosePosition(activePosition);
                }

                bool trendOk = !EnableTrendFilter || (closeVal > trendEmaVal);
                bool rsiOk = rsiVal >= RsiLongLevel;

                if (trendOk && rsiOk && Positions.Count == 0)
                {
                    double slPips = (atrVal * StopLossAtrMult) / Symbol.PipSize;
                    double tpPips = (atrVal * TakeProfitAtrMult) / Symbol.PipSize;
                    if (slPips < 3.0) slPips = 3.0;

                    ExecuteTrade(TradeType.Buy, closeVal, slPips, tpPips);
                }
            }
            // --- SELL ENTRY ---
            else if (bearishCross)
            {
                // Close opposite Buy trade if open
                if (activePosition != null && activePosition.TradeType == TradeType.Buy)
                {
                    ClosePosition(activePosition);
                }

                bool trendOk = !EnableTrendFilter || (closeVal < trendEmaVal);
                bool rsiOk = rsiVal <= RsiShortLevel;

                if (trendOk && rsiOk && Positions.Count == 0)
                {
                    double slPips = (atrVal * StopLossAtrMult) / Symbol.PipSize;
                    double tpPips = (atrVal * TakeProfitAtrMult) / Symbol.PipSize;
                    if (slPips < 3.0) slPips = 3.0;

                    ExecuteTrade(TradeType.Sell, closeVal, slPips, tpPips);
                }
            }
        }

        private void ExecuteTrade(TradeType tradeType, double entryPrice, double slPips, double tpPips)
        {
            double riskCapital = Account.Balance * (RiskPerTradePercent / 100.0);
            double volumeInUnits = CalculateVolumeUnits(riskCapital, slPips);

            // 85% Free Margin Guard
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

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, "SimpleEmaRsi", slPips, tpPips);
            if (result.IsSuccessful)
            {
                Print("[ENTRY] {0} {1} units @ {2:F5} | SL: {3:F1} pips | TP: {4:F1} pips", 
                    tradeType, volumeInUnits, entryPrice, slPips, tpPips);
            }
            else
            {
                Print("[Order Error] {0}", result.Error);
            }
        }

        private double CalculateVolumeUnits(double riskAmount, double riskPips)
        {
            if (riskPips <= 0) return Symbol.VolumeInUnitsMin;
            double pipValuePerUnit = Symbol.PipValue;
            return riskAmount / (riskPips * pipValuePerUnit);
        }
    }
}
