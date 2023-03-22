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

        public string BondsSymbol { get; }

        public RebalancerService(ILogger<PredicterService> logger, TickerService tickerService, IConfiguration configuration, PredicterService predicterService)
        {
            _logger = logger;
            _tickerService = tickerService;
            BondsSymbol = configuration["BondsSymbol"] ?? "STIP";
            _stockBondPerc = double.TryParse(configuration["StockBondPerc"], out var stockBondPerc) ? stockBondPerc : 0.85;
            _predicterService = predicterService;
        }
        public async Task<IEnumerable<IRebalancer>> Rebalance(IEnumerable<Position> positions, Account account) => await Rebalance(positions, account, DateTime.Now);
        public async Task<IEnumerable<IRebalancer>> Rebalance(IEnumerable<Position> positions, Account account, DateTime day)
        {
            var equity = account.Balance.TradeableEquity;
            var cash = account.Balance.TradableCash;
            var allTickers = await _tickerService.GetFullTickerList();
            foreach(var position in positions.Where(p => p.Symbol != BondsSymbol))
            {
                if(!allTickers.Any(t => t.Symbol == position.Symbol))
                {
                    _logger.LogError($"Ticker not found for position {position.Symbol}. Manual intervention required");
                }
            }
            allTickers = allTickers.Where(t => t.PerformanceVector > TickerService.PERFORMANCE_CUTOFF);
            var maxCount = Math.Min(allTickers.Count(), 100.0);
            var sectors = await GetSectors(allTickers, day);
            var tickersPerSector = (int) Math.Ceiling(maxCount / Math.Min(Convert.ToDouble(sectors.Count), maxCount));
            var totalPerformance = 0.0;
            foreach (var sector in sectors)
            {
                if(sector.PerformanceTickers.Count > tickersPerSector) 
                { 
                    sector.PerformanceTickers = sector.PerformanceTickers.OrderByDescending(t => t.Ticker.PerformanceVector).Take(tickersPerSector).ToList();
                }
                foreach(var performanceTicker in sector.PerformanceTickers)
                {
                    performanceTicker.TickerPrediction = await _predicterService.Predict(performanceTicker.Ticker, day);
                    performanceTicker.Position = positions.FirstOrDefault(p => p.Symbol == performanceTicker.Ticker.Symbol);
                    totalPerformance += performanceTicker.PerformanceMultiplier() * performanceTicker.Ticker.PerformanceVector;
                }
            }
            var rebalancers = new List<IRebalancer>();
            foreach(var sector in sectors)
            {
                var performanceTickers = sector.PerformanceTickers;
                foreach (var performanceTicker in performanceTickers)
                {
                    var prediction = performanceTicker.TickerPrediction;
                    if (prediction == null)
                    {
                        continue;
                    }
                    var targetValue = equity * performanceTicker.Ticker.PerformanceVector * performanceTicker.PerformanceMultiplier() * _stockBondPerc / totalPerformance;
                    var position = performanceTicker.Position;
                    if (position == null)
                    {
                        rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, targetValue, prediction));
                    }
                    else if (position.MarketValue.HasValue)
                    {
                        var marketValue = position.MarketValue.Value;
                        var diffPerc = (targetValue - marketValue) * 100.0 / marketValue;
                        if (diffPerc > 0)
                        {
                            diffPerc = diffPerc * prediction.BuyMultiplier;
                        }
                        else if (diffPerc < 0)
                        {
                            diffPerc = diffPerc * prediction.SellMultiplier;
                        }
                        if (Math.Abs(diffPerc) > 10)
                        {
                            rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, targetValue - marketValue, prediction)
                            {
                                Position = position
                            });
                        }
                    }
                    else
                    {
                        _logger.LogError($"Unable to rebalance {performanceTicker.Ticker.Symbol}. Position unavailable");
                    }
                }
            }
            var bondPerc = 1 - _stockBondPerc;
            var bondTargetValue = bondPerc * equity;
            var bondPosition = positions.FirstOrDefault(p => p.Symbol == BondsSymbol);

            if (bondPosition == null)
            {
                rebalancers.Add(new BondRebalancer(BondsSymbol, bondTargetValue));
            }
            else
            {
                if (bondPosition.MarketValue.HasValue)
                {
                    var marketValue = bondPosition.MarketValue.Value;
                    var diffPerc = (bondTargetValue - marketValue) * 100.0 / marketValue;
                    if (Math.Abs(diffPerc) > 10)
                    {
                        rebalancers.Add(new BondRebalancer(BondsSymbol, bondTargetValue - marketValue)
                        {
                            Position = bondPosition
                        });
                    }
                }
                else
                {
                    _logger.LogError($"Unable to rebalance bond {bondPosition.Symbol}. Position unavailable");
                }
            }
            return rebalancers;
        }
        private async Task<List<SectorInfo>> GetSectors(IEnumerable<Ticker> allTickers, DateTime day)
        {
            var sectorDict = new Dictionary<string, List<Ticker>>();
            foreach (var ticker in allTickers)
            {
                if (sectorDict.TryGetValue(ticker.Sector, out var sectorTickers))
                {
                    sectorTickers.Add(ticker);
                }
                else
                {
                    sectorDict.Add(ticker.Sector, new List<Ticker>(new[] { ticker }));
                }
            }
            var sectors = new List<SectorInfo>();
            foreach (var (sector, sectorTickers) in sectorDict.Select(kv => (kv.Key, kv.Value)))
            {
                var prediction = sectorTickers.Count > 2 ? await _predicterService.SectorPredict(sector, day) : 0.5;
                sectors.Add(new SectorInfo
                {
                    Sector = sector,
                    PerformanceTickers = sectorTickers.Select(t => new PerformanceTicker
                    {
                        Ticker = t,
                        SectorPrediction = prediction
                    }).ToList(),
                    Prediction = prediction
                });
            }
            return sectors;
        }
    }
    public class PerformanceTicker
    {
        public Ticker Ticker { get; set; }
        public Prediction TickerPrediction { get; set; }
        public Position Position { get; set; }
        public double SectorPrediction { get; set; }

        public double PerformanceMultiplier()
        {
            if(TickerPrediction == null) { return 1.0; }
            var equityPercMultiplier = 1 + (TickerPrediction.BuyMultiplier * 0.1) + (TickerPrediction.SellMultiplier * -0.1);
            var sectorMultiplier = SectorPrediction.DoubleReduce(1, 0, 1.1, 0.9);
            var performanceMultiplier = (0.75 * equityPercMultiplier) + (0.25 * sectorMultiplier);
            if(Position != null && Position.UnrealizedProfitLossPercent.HasValue)
            {
                var dividendMultiplier = Position.UnrealizedProfitLossPercent.Value < 0 ? 1 : Ticker.DividendYield.DoubleReduce(0.05, 0, 1.25, 0.75);
                performanceMultiplier *= Math.Log((2 * Position.UnrealizedProfitLossPercent.Value * dividendMultiplier) + Math.E);
            }
            return performanceMultiplier;
        }
    }
    public class SectorInfo
    {
        public string Sector { get; set; }
        public List<PerformanceTicker> PerformanceTickers { get; set; }
        public double Prediction { get; set; }
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
