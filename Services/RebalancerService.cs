using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using NumbersGoUp.Services;
using NumbersGoUp.Utils;
using MyUtils = NumbersGoUp.Utils.Utils;

namespace NumbersGoUpBase.Services
{
    public class RebalancerService
    {
        public double CashMinimum { get; }
        public const int MAX_TICKER_COUNT = 50;
        private readonly ILogger<PredicterService> _logger;
        private readonly TickerService _tickerService;
        private readonly PredicterService _predicterService;
        private readonly double _encouragementMultiplier;

        public RebalancerService(ILogger<PredicterService> logger, TickerService tickerService, IConfiguration configuration, 
                                 PredicterService predicterService)
        {
            _logger = logger;
            _tickerService = tickerService;
            _predicterService = predicterService;
            CashMinimum = double.TryParse(configuration[EnvParamKeys.CASH_MIN], out var cashMinimum) ? cashMinimum : 0;
            _encouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration[EnvParamKeys.ENCOURAGEMENT_MULTIPLIER], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
        }
        public async Task<IEnumerable<StockRebalancer>> Rebalance(IEnumerable<Position> positions, Balance balance)
        {
            var equity = balance.TradeableEquity;
            var cash = balance.TradableCash;
            if (equity < 1)
            {
                _logger.LogError($"No equity available! Skipping rebalance.");
                return Enumerable.Empty<StockRebalancer>();
            }
            if (cash < CashMinimum && (CashMinimum > 0 || cash < 0))
            {
                if (CashMinimum > 0)
                {
                    _predicterService.EncouragementMultiplier = Math.Min(_encouragementMultiplier, Math.Max(cash / CashMinimum, 0) - 1);
                }
                else if (cash < 0)
                {
                    _predicterService.EncouragementMultiplier = Math.Min(_encouragementMultiplier, Math.Max(Math.Abs(cash) * 20 / equity, 0) - 1);
                }
            }
            else
            {
                _predicterService.EncouragementMultiplier = _encouragementMultiplier;
            }
            var allTickers = await _tickerService.GetFullTickerList();
            foreach(var position in positions)
            {
                if(!allTickers.Any(t => t.Symbol == position.Symbol))
                {
                    _logger.LogError($"Ticker not found for position {position.Symbol}. Manual intervention required");
                }
            }
            var performanceCutoff = allTickers.Count() > MAX_TICKER_COUNT ? allTickers.OrderByDescending(t => t.PerformanceVector).Skip(MAX_TICKER_COUNT).First().PerformanceVector : 0;
            var selectedTickersBuffer = new List<PerformanceTicker>();
            foreach(var ticker in allTickers)
            {
                var meetsConditions = ticker.PerformanceVector > performanceCutoff;
                if (meetsConditions)
                {
                    selectedTickersBuffer.Add(new PerformanceTicker { Ticker = ticker, MeetsRequirements = true });
                }
                else if (positions.Any(p => p.Symbol == ticker.Symbol))
                {
                    //still have to pull in the ones we have positions for
                    selectedTickersBuffer.Add(new PerformanceTicker { Ticker = ticker });
                }
            }
            var selectedTickers = selectedTickersBuffer.OrderByDescending(t => t.Ticker.PerformanceVector).ToArray();
            double totalPerformance = 0.0;
            for (var i = 0; i < selectedTickers.Length; i++)
            {
                var performanceTicker = selectedTickers[i];
                var prediction = await _predicterService.Predict(performanceTicker.Ticker);
                if (prediction != null)
                {
                    prediction.SellMultiplier = prediction.SellMultiplier * (performanceTicker.MeetsRequirements ? 0.3 : 1);
                    prediction.BuyMultiplier = prediction.BuyMultiplier * (performanceTicker.MeetsRequirements ? 1 : 0);
                }
                performanceTicker.TickerPrediction = prediction;
                performanceTicker.Position = positions.FirstOrDefault(p => p.Symbol == performanceTicker.Ticker.Symbol);
                if(performanceTicker.Position != null && performanceTicker.TickerPrediction == null)
                {
                    _logger.LogError($"Position exists for {performanceTicker.Ticker.Symbol} but prediction returned null");
                }
                performanceTicker.FinalPerformance = PerformanceValue(performanceTicker) * MyUtils.AlmaNormalizedMultiplier(i, selectedTickers.Length, 0.8);
                totalPerformance += performanceTicker.FinalPerformance;
            }
            var rebalancers = new List<StockRebalancer>();
            if(totalPerformance == 0)
            {
                _logger.LogError("Total Performance calculation error. Cancelling rebalancer process.");
                return rebalancers;
            }
            var tickerEquity = equity * _predicterService.EncouragementMultiplier.DoubleReduce(0, -1);
            var barMetrics = new List<BarMetric>();
            foreach (var performanceTicker in selectedTickers)
            {
                var prediction = performanceTicker.TickerPrediction;
                if (prediction == null)
                {
                    continue;
                }
                if (prediction.RecentBarMetric != null) { barMetrics.Add(prediction.RecentBarMetric); }
                var calculatedPerformance = performanceTicker.FinalPerformance * performanceTicker.PerformanceMultiplier();
                var targetValue = totalPerformance > 0 ? Math.Round(tickerEquity * calculatedPerformance / totalPerformance, MidpointRounding.ToZero) : 0.0;
                var position = performanceTicker.Position;
                if (position == null && targetValue > 0 && performanceTicker.MeetsRequirements && cash > 0)
                {
                    rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, targetValue, prediction));

                }
                else if (position != null && position.MarketValue.HasValue && position.MarketValue.Value > 0)
                {
                    var marketValue = position.MarketValue.Value;
                    if (marketValue > 0)
                    {
                        var diffPerc = (targetValue - marketValue) * 100.0 / marketValue;
                        var diff = targetValue - marketValue;
                        if (diffPerc > 0)
                        {
                            if (performanceTicker.MeetsRequirements && cash > (position.AssetLastPrice ?? 0))
                            {
                                diffPerc *= 1 - prediction.SellMultiplier;
                            }
                            else { diffPerc = 0; }
                        }
                        else if (targetValue > 0)
                        {
                            double sellModifier = 2;
                            if (position.UnrealizedProfitLossPercent.HasValue)
                            {
                                var plperc = position.UnrealizedProfitLossPercent.Value.DoubleReduce(1.1, -1, 2, -1.9);
                                sellModifier += plperc;
                            }
                            diffPerc *= Math.Max(prediction.SellMultiplier - prediction.BuyMultiplier, 0) / sellModifier;
                        }

                        const double diffCutoff = 10.0;

                        if (Math.Abs(diffPerc) > diffCutoff)
                        {
                            rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, diff, prediction)
                            {
                                Position = position
                            });
                        }
                    }
                    else
                    {
                        _logger.LogError($"Stupid market value not positive, which is impossible. Ticker {position.Symbol}");
                    }
                }
                else if (cash > 0 && targetValue > 0)
                {
                    _logger.LogError($"Unable to rebalance {performanceTicker.Ticker.Symbol}. Position unavailable");
                }
            }
            if (cash < CashMinimum)
            {
                var sells = rebalancers.Where(r => r.Diff < 0).ToList();
                if (sells.Count > 0)
                {
                    var remaining = CashMinimum - cash;
                    var newRebalancers = new List<StockRebalancer>();
                    newRebalancers.AddRange(newRebalancers.Where(r => r.Diff > 0));
                    foreach (var rebalancer in sells.OrderBy(SellValue))
                    {
                        if (remaining > 0)
                        {
                            newRebalancers.Add(rebalancer);
                            remaining += rebalancer.Diff;
                        }
                    }
                    return newRebalancers;
                }
            }
            return rebalancers;
        }

        private static double SellValue(StockRebalancer rebalancer)
        {
            var sellValue = rebalancer.Ticker.PerformanceVector;
            sellValue *= 1 + (rebalancer.Position.UnrealizedProfitLossPercent ?? 0);
            sellValue *= 1 - rebalancer.Prediction.RecentBarMetric.SMASMA.DoubleReduce(100, 0);
            return sellValue;
        }
        private static double PerformanceValue(PerformanceTicker performanceTicker)
        {
            double predictMultiplier = 0;
            if (performanceTicker.TickerPrediction != null)
            {
                predictMultiplier = Math.Max(performanceTicker.TickerPrediction.BuyMultiplier - performanceTicker.TickerPrediction.SellMultiplier, 0) * performanceTicker.Ticker.PerformanceVector.DoubleReduce(100, 0, 2, 0);
            }
            return performanceTicker.Ticker.PerformanceVector.DoubleReduce(100, 0, 8, 0) + predictMultiplier;
        }
    }
    public class PerformanceTicker
    {
        public Ticker Ticker { get; set; }
        public Prediction TickerPrediction { get; set; }
        public Position Position { get; set; }
        public bool MeetsRequirements { get; set; }
        public double FinalPerformance { get; set; }
        public virtual double PerformanceMultiplier()
        {
            return MeetsRequirements ? 1.0 : 0.9;
        }
    }

    public class StockRebalancer
    {
        public StockRebalancer(Ticker ticker, double diff, Prediction prediction)
        {
            Ticker = ticker;
            Diff = diff;
            Prediction = prediction;
        }
        public Ticker Ticker { get; }
        public string Symbol => Ticker.Symbol;
        public double Diff { get; }
        public Prediction Prediction { get; }
        public Position Position { get; set; }
    }
}
