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
        private readonly ILogger<PredicterService> _logger;
        private readonly TickerService _tickerService;
        private readonly PredicterService _predicterService;
        private readonly double _stockBondPerc;

        public string[] BondSymbols { get; }

        public RebalancerService(ILogger<PredicterService> logger, TickerService tickerService, IConfiguration configuration, PredicterService predicterService)
        {
            _logger = logger;
            _tickerService = tickerService;
            var bondSymbols = configuration["BondSymbols"]?.Split(',');
            BondSymbols = bondSymbols != null && !bondSymbols.Any(s => string.IsNullOrWhiteSpace(s)) ? bondSymbols : new string[] { "VTIP", "STIP" };
            _stockBondPerc = double.TryParse(configuration["StockBondPerc"], out var stockBondPerc) ? stockBondPerc : 1.0;
            _predicterService = predicterService;
        }
        public async Task<IEnumerable<IRebalancer>> Rebalance(IEnumerable<Position> positions, Balance balance, DateTime? day = null)
        {
            var equity = balance.TradeableEquity;
            var cash = balance.TradableCash;
            if(equity < 1)
            {
                _logger.LogError($"No equity available! Skipping rebalance.");
                return Enumerable.Empty<IRebalancer>();
            }
            var allTickers = await _tickerService.GetFullTickerList();
            foreach(var position in positions.Where(p => !BondSymbols.Contains(p.Symbol)))
            {
                if(!allTickers.Any(t => t.Symbol == position.Symbol))
                {
                    _logger.LogError($"Ticker not found for position {position.Symbol}. Manual intervention required");
                }
            }
            var totalPerformance = 0.0;
            var selectedTickers = new List<PerformanceTicker>();
            foreach(var ticker in allTickers)
            {
                if(ticker.PerformanceVector > TickerService.PERFORMANCE_CUTOFF)
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
            totalPerformance = totalPerformance * (1 - (cash / equity).DoubleReduce(1, 0.1, 0.9, 0));
            var rebalancers = new List<IRebalancer>();
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
                    rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, targetValue * Math.Max(prediction.BuyMultiplier - prediction.SellMultiplier, 0), prediction));
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
                                diffPerc *= prediction.BuyMultiplier;
                                diff = Math.Min(prediction.BuyMultiplier * targetValue, diff);
                            }
                            else { diffPerc = 0; }
                        }
                        else if (diffPerc < 0 && totalPerformance > 0)
                        {
                            diffPerc *= prediction.SellMultiplier;
                            if (targetValue > 0)
                            {
                                diff = Math.Max(-marketValue * prediction.SellMultiplier, diff);
                            }
                            else
                            {
                                diff = -marketValue * prediction.SellMultiplier.DoubleReduce(0.5, 0);
                            }
                        }
                        var diffCutoff = 6.0;
                        if(diff < 0)
                        {
                            diffCutoff = 10;
                            var gain = position.MarketValue.Value - position.CostBasis;
                            diffCutoff = (gain * performanceTicker.Ticker.DividendYield.DoubleReduce(0.04, 0) / equity).DoubleReduce(0.1, 0, 90, diffCutoff);
                        }
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
            var performanceValue = performanceTicker.Ticker.PerformanceVector;
            var performanceMultiplier = 3 - performanceTicker.Ticker.SMASMAAvg.DoubleReduce(20, -20, 2, 0);
            //if (performanceTicker.Position != null && performanceTicker.Position.UnrealizedProfitLossPercent.HasValue)
            //{
            //    var positionMultiplier = performanceTicker.Position.UnrealizedProfitLossPercent.Value.DoubleReduce(0, -0.25);
            //    performanceMultiplier = (1 - positionMultiplier) + (positionMultiplier * performanceMultiplier);
            //}
            return performanceValue * performanceMultiplier * (1 + performanceValue.DoubleReduce(100, 0).Curve1(2));
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
            var performanceMultiplier = MeetsRequirements ? 1.0 : 0.9;
            if (TickerPrediction != null)
            {
                performanceMultiplier += (TickerPrediction.BuyMultiplier - TickerPrediction.SellMultiplier).DoubleReduce(0.75, -0.75).Curve6(4).DoubleReduce(1, 0, 0.6, -0.2);
            }
            if (Position != null && Position.UnrealizedProfitLossPercent.HasValue && Position.UnrealizedProfitLossPercent.Value > 0)
            {
                performanceMultiplier *= Math.Log((Position.UnrealizedProfitLossPercent.Value * Ticker.DividendYield.DoubleReduce(0.03, 0)) + Math.E);
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
