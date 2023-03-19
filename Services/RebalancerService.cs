using Alpaca.Markets;
using Microsoft.EntityFrameworkCore.Internal;
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
        private readonly IAppCancellation _appCancellation;
        private readonly IBrokerService _brokerService;
        private readonly TickerService _tickerService;
        private readonly DataService _dataService;
        private readonly PredicterService _predicterService;
        private readonly IStocksContextFactory _contextFactory;
        private readonly string _bondsSymbol;
        private readonly double _stockBondRatio;

        public RebalancerService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, TickerService tickerService, 
                                    DataService dataService, IConfiguration configuration, PredicterService predicterService, IStocksContextFactory contextFactory)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _tickerService = tickerService;
            _bondsSymbol = configuration["BondsSymbol"] ?? "STIP";
            _stockBondRatio = double.TryParse(configuration["StockBondRatio"], out var stockBondRatio) ? stockBondRatio : 0.85;
            _dataService = dataService;
            _predicterService = predicterService;
            _contextFactory = contextFactory;
        }
        public async Task<IEnumerable<IRebalancer>> Rebalance(Position[] positions, Account account) => await Rebalance(positions, account, DateTime.Now);
        public async Task<IEnumerable<IRebalancer>> Rebalance(Position[] positions, Account account, DateTime day)
        {
            var equity = account.Balance.LastEquity;
            var cash = account.Balance.TradableCash;
            var allTickers = await _tickerService.GetFullTickerList();
            allTickers = allTickers.Where(t => t.PerformanceVector > TickerService.PERFORMANCE_CUTOFF);
            var maxCount = Math.Min(allTickers.Count(), 100.0);
            var sectors = await GetSectors(allTickers, day);
            var tickersPerSector = (int) Math.Ceiling(maxCount / Math.Min(Convert.ToDouble(sectors.Count), maxCount));
            var totalPerformance = 0.0;
            foreach (var sector in sectors)
            {
                if(sector.Tickers.Count > tickersPerSector) 
                { 
                    sector.Tickers = sector.Tickers.OrderByDescending(t => t.PerformanceVector).Take(tickersPerSector).ToList();
                }
                totalPerformance += sector.Tickers.Sum(t => t.PerformanceVector);
            }
            var rebalancers = new List<IRebalancer>();
            foreach(var sector in sectors)
            {
                var tickers = sector.Tickers;
                foreach (var ticker in tickers)
                {
                    Prediction prediction = await _predicterService.Predict(ticker, day);
                    //prediction = new Prediction
                    //{
                    //    Day = day,
                    //    BuyMultiplier = 1,
                    //    SellMultiplier = 1
                    //};
                    if(prediction == null)
                    {
                        continue;
                    }
                    var equityPerc = ticker.PerformanceVector * sector.Prediction * _stockBondRatio / totalPerformance;
                    equityPerc *= 1 + (prediction.BuyMultiplier * 0.1) + (prediction.SellMultiplier * -0.1);
                    var position = positions.FirstOrDefault(p => p.Symbol == ticker.Symbol);
                    var targetValue = equityPerc * equity;
                    if (position == null)
                    {
                        var multiplier = prediction.BuyMultiplier + ((1 - prediction.BuyMultiplier) * prediction.BuyMultiplier);
                        rebalancers.Add(new StockRebalancer(ticker, targetValue * multiplier));
                    }
                    else if (position.MarketValue.HasValue)
                    {
                        var marketValue = position.MarketValue.Value;
                        var diffPerc = (targetValue - marketValue) * 100.0 / marketValue;
                        if (diffPerc > 0)
                        {
                            diffPerc = diffPerc * prediction.BuyMultiplier;
                            if (day.Month == 12 && position.UnrealizedProfitLossPercent.HasValue && position.UnrealizedProfitLossPercent.Value < 0)
                            {
                                //diffPerc = 0;
                            }
                        }
                        else if (diffPerc < 0)
                        {
                            diffPerc = diffPerc * prediction.SellMultiplier;
                            if (day.Month == 12 && day.Day > 10 && position.UnrealizedProfitLossPercent.HasValue && position.UnrealizedProfitLossPercent.Value < 0)
                            {
                                //diffPerc = -11;
                                //targetValue = 0;
                            }
                        }
                        if (Math.Abs(diffPerc) > 10)
                        {
                            rebalancers.Add(new StockRebalancer(ticker, targetValue - marketValue));
                        }
                    }
                    else
                    {
                        _logger.LogError($"Unable to rebalance {ticker.Symbol}. Position unavailable");
                    }
                }
            }
            var bondPerc = 1 - _stockBondRatio;
            var bondTargetValue = bondPerc * equity;
            var bondPosition = positions.FirstOrDefault(p => p.Symbol == _bondsSymbol);

            if (bondPosition == null)
            {
                rebalancers.Add(new BondRebalancer(_bondsSymbol, bondTargetValue));
            }
            else
            {
                var marketValue = bondPosition.MarketValue;
                if (marketValue.HasValue)
                {
                    rebalancers.Add(new BondRebalancer(_bondsSymbol, bondTargetValue - marketValue.Value));
                }
            }
            return rebalancers;
        }
        private async Task<List<SectorInfo>> GetSectors(IEnumerable<Ticker> allTickers, DateTime day)
        {
            using var stocksContext = _contextFactory.CreateDbContext();
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
                sectors.Add(new SectorInfo
                {
                    Sector = sector,
                    Tickers = sectorTickers,
                    Prediction = 1//sectorTickers.Count > 2 ? await _predicterService.SectorPredict(sector, day) : 0.9
                });
            }
            return sectors;
        }
    }
    public class SectorInfo
    {
        public string Sector { get; set; }
        public List<Ticker> Tickers { get; set; }
        public double Prediction { get; set; }
    }
    public interface IRebalancer
    {
        bool IsBond { get; }
        bool IsStock { get; }
        string Symbol { get; }
        double Diff { get; }
    }
    public class StockRebalancer : IRebalancer
    {
        public StockRebalancer(Ticker ticker, double diff)
        {
            Ticker = ticker;
            Diff = diff;
        }
        public bool IsBond => false;
        public bool IsStock => true;
        public Ticker Ticker { get; }
        public string Symbol => Ticker.Symbol;
        public double Diff { get; }
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
    }
}
