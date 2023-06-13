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
            var allTickers = await _tickerService.GetFullTickerList();
            foreach(var position in positions.Where(p => !BondSymbols.Contains(p.Symbol)))
            {
                if(!allTickers.Any(t => t.Symbol == position.Symbol))
                {
                    _logger.LogError($"Ticker not found for position {position.Symbol}. Manual intervention required");
                }
            }
            var maxCount = Math.Min(allTickers.Count(), 100.0);
            var sectors = await GetSectors(allTickers, day);
            //var tickersPerSector = (int) Math.Ceiling(maxCount / Math.Min(Convert.ToDouble(sectors.Count), maxCount));
            var totalPerformance = 0.0;
            //var performanceCutoff = sectors.SelectMany(s => s.PerformanceTickers.Select(pt => pt.Ticker.PerformanceVector)).Where(pv => pv > 0).OrderByDescending(pv => pv).Take(100).Last();
            foreach (var sector in sectors)
            {
                var selectedTickers = sector.PerformanceTickers.Where(t => t.Ticker.PerformanceVector > TickerService.PERFORMANCE_CUTOFF && !_tickerBlacklist.Contains(t.Ticker.Symbol)).ToList();
                //if (selectedTickers.Count > tickersPerSector) 
                //{
                //    selectedTickers = selectedTickers.OrderByDescending(t => t.Ticker.PerformanceVector).Take(tickersPerSector).ToList();
                //}
                foreach (var performanceTicker in sector.PerformanceTickers)
                {
                    if(selectedTickers.Any(t => t.Ticker.Symbol == performanceTicker.Ticker.Symbol))
                    {
                        performanceTicker.MeetsRequirements = true;
                    }
                    else if(positions.Any(p => p.Symbol == performanceTicker.Ticker.Symbol))
                    {
                        selectedTickers.Add(performanceTicker); //still have to pull in the ones we have positions for
                    }
                }
                sector.PerformanceTickers = selectedTickers;

                foreach(var performanceTicker in sector.PerformanceTickers)
                {
                    performanceTicker.TickerPrediction = await _predicterService.Predict(performanceTicker.Ticker, day);
                    performanceTicker.Position = positions.FirstOrDefault(p => p.Symbol == performanceTicker.Ticker.Symbol);
                    totalPerformance += Math.Pow(performanceTicker.Ticker.PerformanceVector, 2);
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
                    var buyMultiplier = FinalBuyMultiplier(Math.Max(cash / equity, 0), prediction.BuyMultiplier);
                    var sellMultiplier = FinalSellMultiplier(Math.Max(cash / equity, 0), prediction.SellMultiplier);
                    var calculatedPerformance = equity * Math.Pow(performanceTicker.Ticker.PerformanceVector, 2) * performanceTicker.PerformanceMultiplier() * _predicterService.EncouragementMultiplier.DoubleReduce(0, -1) * _stockBondPerc;
                    var targetValue = totalPerformance > 0 ? (calculatedPerformance / totalPerformance) : 0.0;
                    var position = performanceTicker.Position;
                    if (position == null && targetValue > 0 && performanceTicker.MeetsRequirements && cash > 0)
                    {
                        rebalancers.Add(new StockRebalancer(performanceTicker.Ticker, targetValue * buyMultiplier, prediction));
                    }
                    else if (position != null && position.MarketValue.HasValue)
                    {
                        var marketValue = position.MarketValue.Value;
                        if(marketValue > 0)
                        {
                            var diffPerc = (targetValue - marketValue) * 100.0 / marketValue;
                            var diff = targetValue - marketValue;
                            if (diffPerc > 0)
                            {
                                if (performanceTicker.MeetsRequirements && cash > (position.AssetLastPrice ?? 0))
                                {
                                    diffPerc = diffPerc * buyMultiplier;
                                    if (sector.PerformanceTickers.Count > 2)
                                    {
                                        diffPerc *= sector.Prediction.DoubleReduce(1, 0.6, 1.25, 1);
                                    }
                                    diff *= buyMultiplier.Curve6(1);
                                }
                                else { diffPerc = 0; }
                            }
                            else if (diffPerc < 0)
                            {
                                diffPerc = diffPerc * sellMultiplier;
                                if(sector.PerformanceTickers.Count > 2)
                                {
                                    diffPerc *= 2 - sector.Prediction.DoubleReduce(1, 0.6, 1.25, 1);
                                }
                                diff *= sellMultiplier.Curve6(1);
                            }
                            if (Math.Abs(diffPerc) > 10)
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
                    else if(cash > 0)
                    {
                        _logger.LogError($"Unable to rebalance {performanceTicker.Ticker.Symbol}. Position unavailable");
                    }
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

        private static double FinalSellMultiplier(double cashEquityRatio, double sellMultiplier) => sellMultiplier;//.Curve1(cashEquityRatio.DoubleReduce(0.3, 0, 3, 1));
        private static double FinalBuyMultiplier(double cashEquityRatio, double buyMultiplier) => buyMultiplier;//.Curve3((1 - cashEquityRatio).DoubleReduce(1, 0.7, 4, 2));
    }
    public class PerformanceTicker
    {
        public Ticker Ticker { get; set; }
        public Prediction TickerPrediction { get; set; }
        public Position Position { get; set; }
        public double SectorPrediction { get; set; }
        public bool MeetsRequirements { get; set; }

        public double PerformanceMultiplier()
        {
            var performanceMultiplier = MeetsRequirements ? 1 : 0.9;
            if (TickerPrediction != null)
            {
                var predictionMax = MeetsRequirements ? 1.1 : 1.0;
                var equityPercMultiplier = 1 + (TickerPrediction.BuyMultiplier * (predictionMax - 1)) + (TickerPrediction.SellMultiplier * (predictionMax - 1.2));
                var sectorMultiplier = SectorPrediction.DoubleReduce(1, 0, predictionMax, predictionMax - 0.2);
                performanceMultiplier = (0.8 * equityPercMultiplier) + (0.2 * sectorMultiplier);
            }
            if (Position != null && Position.UnrealizedProfitLossPercent.HasValue && Position.UnrealizedProfitLossPercent.Value > 0)
            {
                performanceMultiplier *= Math.Log(Position.UnrealizedProfitLossPercent.Value + Math.E);
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
