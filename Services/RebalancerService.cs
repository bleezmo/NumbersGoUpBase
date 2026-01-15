using CsvHelper.Configuration.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using NumbersGoUp.Services;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.Services
{
    public class RebalancerService
    {
        public const int MAX_TICKER_COUNT = 50;
        private readonly ILogger<PredicterService> _logger;
        private readonly TickerService _tickerService;
        private readonly PredicterService _predicterService;
        private readonly double _stockBondPerc;
        private readonly ITickerPickProcessor _tickerPickProcessor;

        public string[] BondSymbols { get; }

        public RebalancerService(ILogger<PredicterService> logger, TickerService tickerService, IConfiguration configuration, 
                                 PredicterService predicterService, ITickerPickProcessor tickerPickProcessor)
        {
            _logger = logger;
            _tickerService = tickerService;
            var bondSymbols = configuration["BondSymbols"]?.Split(',');
            BondSymbols = bondSymbols != null && !bondSymbols.Any(s => string.IsNullOrWhiteSpace(s)) ? bondSymbols : new string[] { "VTIP", "STIP" };
            _stockBondPerc = double.TryParse(configuration["StockBondPerc"], out var stockBondPerc) ? stockBondPerc : 1.0;
            _predicterService = predicterService;
            _tickerPickProcessor = tickerPickProcessor;
        }
#if DEBUG
        public static bool OverridePerformance = false;
        public async Task<IEnumerable<Ticker>> PerformanceOverride()
        {
            if (!OverridePerformance)
            {
                return await _tickerService.GetFullTickerList();
            }
            var tickers = (await _tickerService.GetFullTickerList()).ToArray();
            var minmax = new MinMaxStore<Ticker>(t => t.SMASMAAvg);
            foreach (var ticker in tickers)
            {
                minmax.Run(ticker);
            }
            foreach (var ticker in tickers)
            {
                ticker.PerformanceVector = ticker.SMASMAAvg.DoubleReduce(minmax.Max, minmax.Min, 100, 0);
            }
            return tickers;
        }
#endif
        public async Task<IEnumerable<IRebalancer>> Rebalance(IEnumerable<Position> positions, Balance balance, DateTime? day = null)
        {
            var equity = balance.TradeableEquity;
            var cash = balance.TradableCash;
            if (equity < 1)
            {
                _logger.LogError($"No equity available! Skipping rebalance.");
                return Enumerable.Empty<IRebalancer>();
            }
#if DEBUG
            var allTickers = day.HasValue ? await PerformanceOverride() : await _tickerService.GetFullTickerList();
#else
            var allTickers = await _tickerService.GetFullTickerList();
#endif
            foreach(var position in positions.Where(p => !BondSymbols.Contains(p.Symbol)))
            {
                if(!allTickers.Any(t => t.Symbol == position.Symbol))
                {
                    _logger.LogError($"Ticker not found for position {position.Symbol}. Manual intervention required");
                }
            }
            var performanceCutoff = allTickers.Count() > MAX_TICKER_COUNT ? allTickers.OrderByDescending(t => t.PerformanceVector).Skip(MAX_TICKER_COUNT).First().PerformanceVector : 0;
            var selectedTickers = new List<PerformanceTicker>();
            foreach(var ticker in allTickers)
            {
                var meetsConditions = ticker.PerformanceVector > performanceCutoff;
                if (meetsConditions)
                {
                    selectedTickers.Add(new PerformanceTicker
                    {
                        Ticker = ticker,
                        MeetsRequirements = true
                    });
                }
                else if (positions.Any(p => p.Symbol == ticker.Symbol))
                {
                    //still have to pull in the ones we have positions for
                    selectedTickers.Add(new PerformanceTicker
                    {
                        Ticker = ticker
                    });
                }
            }
            double totalPerformance = 0.0;
            foreach (var performanceTicker in selectedTickers)
            {
                performanceTicker.TickerPrediction = day.HasValue ? await _predicterService.Predict(performanceTicker.Ticker, day.Value) : 
                                                                    await _predicterService.Predict(performanceTicker.Ticker);
                performanceTicker.Position = positions.FirstOrDefault(p => p.Symbol == performanceTicker.Ticker.Symbol);
                if(performanceTicker.Position != null && performanceTicker.TickerPrediction == null)
                {
                    _logger.LogError($"Position exists for {performanceTicker.Ticker.Symbol} but prediction returned null");
                }
                totalPerformance += PerformanceValue(performanceTicker);
            }
            var rebalancers = new List<IRebalancer>();
            if(totalPerformance == 0)
            {
                _logger.LogError("Total Performance calculation error. Cancelling rebalancer process.");
                return rebalancers;
            }
            var tickerEquity = equity * _predicterService.EncouragementMultiplier.DoubleReduce(0, -1) * _stockBondPerc;
            foreach (var performanceTicker in selectedTickers)
            {
                var prediction = performanceTicker.TickerPrediction;
                if (prediction == null)
                {
                    continue;
                }
                var calculatedPerformance = tickerEquity * PerformanceValue(performanceTicker) * performanceTicker.PerformanceMultiplier();
                var targetValue = totalPerformance > 0 ? (calculatedPerformance / totalPerformance) : 0.0;
                var position = performanceTicker.Position;
                if (position == null && targetValue > 0 && performanceTicker.MeetsRequirements && cash > 0)
                {
                    var cutoff = performanceTicker.TickerPrediction.BuyMultiplier - performanceTicker.TickerPrediction.SellMultiplier;
                    if (cutoff > 0)//2.328 //2.346
                    {
                        rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, targetValue * (1 - performanceTicker.TickerPrediction.SellMultiplier), prediction));
                    }
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
                                //diffPerc *= prediction.BuyMultiplier; //2.963//2.631 //3.848
                                diffPerc *= (1 - prediction.SellMultiplier) * prediction.BuyMultiplier.Curve2(1);
                            }
                            else { diffPerc = 0; }
                        }
                        else
                        {
                            double sellModifier = 2;
                            if (position.UnrealizedProfitLossPercent.HasValue)
                            {
                                var plperc = position.UnrealizedProfitLossPercent.Value.DoubleReduce(1.1, -1, 2, -1.9);
                                sellModifier += plperc;
                            }
                            //diffPerc *= prediction.SellMultiplier.Curve6(2) / sellModifier; //3.991
                            diffPerc *= prediction.SellMultiplier.Curve3(2) / sellModifier;
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
            var bondPerc = 1 - _stockBondPerc;
            var perBondTargetValue = BondSymbols.Length > 0 ? (bondPerc * equity / BondSymbols.Length) : 0;
            foreach(var bondSymbol in BondSymbols)
            {
                var bondPosition = positions.FirstOrDefault(p => bondSymbol == p.Symbol);

                if (bondPosition == null && perBondTargetValue > 0)
                {
                    rebalancers.Add(new BondRebalancer(bondSymbol, perBondTargetValue));
                }
                else if(bondPosition != null)
                {
                    if (bondPosition.MarketValue.HasValue)
                    {
                        var marketValue = bondPosition.MarketValue.Value;
                        if (marketValue > 0)
                        {
                            var diffPerc = (perBondTargetValue - marketValue) * 100.0 / marketValue;
                            if (Math.Abs(diffPerc) > 10)
                            {
                                rebalancers.Add(new BondRebalancer(bondSymbol, perBondTargetValue - marketValue)
                                {
                                    Position = bondPosition
                                });
                            }
                        }
                        else
                        {
                            _logger.LogError($"Stupid market value not positive, which is impossible. Bond Ticker {bondPosition.Symbol}");
                        }
                    }
                    else
                    {
                        _logger.LogError($"Unable to rebalance bond {bondPosition.Symbol}. Position unavailable");
                    }
                }
            }
            return rebalancers;
        }

        private static double PerformanceValue(PerformanceTicker performanceTicker)
        {
            double predictMultiplier = 0;
            double coeff = 0; //2.328 
            //coeff = 3; //2.298
            //coeff = 6; //2.265
            //coeff = 10; //2.049
            if (performanceTicker.TickerPrediction != null && coeff > 0)
            {
                predictMultiplier = Math.Max(performanceTicker.TickerPrediction.BuyMultiplier - performanceTicker.TickerPrediction.SellMultiplier, 0) * performanceTicker.Ticker.PerformanceVector.DoubleReduce(100, 0, coeff, 0);
                //predictMultiplier = Math.Max(performanceTicker.TickerPrediction.BuyMultiplier - performanceTicker.TickerPrediction.SellMultiplier, 0) * coeff;
            }
            return performanceTicker.Ticker.PerformanceVector.DoubleReduce(100, 0, 10 - coeff, 0) + predictMultiplier;
        }
    }
    public class PerformanceTicker
    {
        public Ticker Ticker { get; set; }
        public Prediction TickerPrediction { get; set; }
        public Position Position { get; set; }
        public bool MeetsRequirements { get; set; }

        public double PerformanceMultiplier()
        {
            var performanceMultiplier = MeetsRequirements ? 1.0 : 0.8;
            var buyMultiplierOffset = Ticker.PerformanceVector.DoubleReduce(100, 0);
            //var buyMultiplierOffset = Ticker.PerformanceVector.DoubleReduce(100, 0, 0.5, 0);
            if (TickerPrediction != null)
            {
                //performanceMultiplier += (TickerPrediction.BuyMultiplier - TickerPrediction.SellMultiplier).DoubleReduce(1, -1).Curve6(3.2).DoubleReduce(1, 0, buyMultiplierOffset, -0.5);
                
                var offset = (1 - TickerPrediction.SellMultiplier).DoubleReduce(1, 0, buyMultiplierOffset, -1);
                offset += (1 - offset.DoubleReduce(1, 0)) * (TickerPrediction.BuyMultiplier - TickerPrediction.SellMultiplier).DoubleReduce(1, 0, 0.5, 0);
                performanceMultiplier += offset;

                //performanceMultiplier += (1 - TickerPrediction.SellMultiplier).DoubleReduce(1, 0, buyMultiplierOffset, -1);
                //performanceMultiplier += (TickerPrediction.BuyMultiplier - TickerPrediction.SellMultiplier).DoubleReduce(1, 0, 0.5, 0); //4.002
            }
            return Math.Max(performanceMultiplier, 0);
        }
    }
    public interface IRebalancer
    {
        bool IsBond { get; }
        bool IsStock { get; }
        string Symbol { get; }
        double Diff { get; }
        Position Position { get; set; }
    }
    public class StockRebalancer : IRebalancer
    {
        public StockRebalancer(Ticker ticker, double diff, Prediction prediction)
        {
            Ticker = ticker;
            Diff = diff;
            Prediction = prediction;
        }
        public bool IsBond => false;
        public bool IsStock => true;
        public Ticker Ticker { get; }
        public string Symbol => Ticker.Symbol;
        public double Diff { get; }
        public Prediction Prediction { get; }
        public Position Position { get; set; }
    }
    public class BondRebalancer : IRebalancer
    {
        public BondRebalancer(string symbol, double diff)
        {
            Symbol = symbol;
            Diff = diff;
        }
        public bool IsBond => true;
        public bool IsStock => false;
        public string Symbol { get; }
        public double Diff { get; }
        public Position Position { get; set; }
    }
}
