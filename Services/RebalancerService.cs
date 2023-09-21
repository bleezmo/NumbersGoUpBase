﻿using CsvHelper.Configuration.Attributes;
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
        private readonly string[] _tickerBlacklist;

        public string[] BondSymbols { get; }

        public RebalancerService(ILogger<PredicterService> logger, TickerService tickerService, IConfiguration configuration, PredicterService predicterService)
        {
            _logger = logger;
            _tickerService = tickerService;
            var bondSymbols = configuration["BondSymbols"]?.Split(',');
            BondSymbols = bondSymbols != null && !bondSymbols.Any(s => string.IsNullOrWhiteSpace(s)) ? bondSymbols : new string[] { "VTIP", "STIP" };
            _stockBondPerc = double.TryParse(configuration["StockBondPerc"], out var stockBondPerc) ? stockBondPerc : 0.85;
            _predicterService = predicterService;
            _tickerBlacklist = tickerService.TickerBlacklist;
        }
        public async Task<IEnumerable<IRebalancer>> Rebalance(IEnumerable<Position> positions, Balance balance) => await Rebalance(positions, balance, DateTime.Now);
        public async Task<IEnumerable<IRebalancer>> Rebalance(IEnumerable<Position> positions, Balance balance, DateTime day)
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
                if(ticker.PerformanceVector > TickerService.PERFORMANCE_CUTOFF && !_tickerBlacklist.Contains(ticker.Symbol))
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
                performanceTicker.TickerPrediction = await _predicterService.Predict(performanceTicker.Ticker, day);
                performanceTicker.Position = positions.FirstOrDefault(p => p.Symbol == performanceTicker.Ticker.Symbol);
                totalPerformance += PerformanceValue(performanceTicker);
            }
            var rebalancers = new List<IRebalancer>();
            var tickerEquity = equity * Convert.ToDouble(selectedTickers.Where(t => t.MeetsRequirements).Count()).DoubleReduce(50, 0) * _predicterService.EncouragementMultiplier.DoubleReduce(0, -1) * _stockBondPerc;
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
                    rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, targetValue * prediction.BuyMultiplier, prediction));
                }
                else if (position != null && position.MarketValue.HasValue)
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
                                diff *= prediction.BuyMultiplier.Curve6(1);
                            }
                            else { diffPerc = 0; }
                        }
                        else if (diffPerc < 0)
                        {
                            diffPerc *= prediction.SellMultiplier;
                            if (targetValue > 0)
                            {
                                diff *= prediction.SellMultiplier.Curve6(1);
                            }
                        }
                        var diffCutoff = 12.0;
                        if(diff < 0)
                        {
                            var gain = position.MarketValue.Value - position.CostBasis;
                            diffCutoff = (gain * performanceTicker.Ticker.DividendYield.DoubleReduce(0.03, 0) / equity).DoubleReduce(0.1, 0, 99, diffCutoff);
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
                else if (cash > 0)
                {
                    _logger.LogError($"Unable to rebalance {performanceTicker.Ticker.Symbol}. Position unavailable");
                }
            }
            var bondPerc = 1 - _stockBondPerc;
            var perBondTargetValue = bondPerc * equity / BondSymbols.Length;
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
            return performanceValue * (1 + performanceValue.DoubleReduce(100, 0).Curve1(3));
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
                var predictionMax = MeetsRequirements ? 1.15 : 1.0;
                performanceMultiplier = 1 + (TickerPrediction.BuyMultiplier * (predictionMax - 1)) + (TickerPrediction.SellMultiplier * (predictionMax - 1.3));
            }
            if (Position != null && Position.UnrealizedProfitLossPercent.HasValue && Position.UnrealizedProfitLossPercent.Value > 0)
            {
                performanceMultiplier *= Math.Log((Position.UnrealizedProfitLossPercent.Value * Ticker.DividendYield.DoubleReduce(0.03, 0)) + Math.E);
            }
            return performanceMultiplier;
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
