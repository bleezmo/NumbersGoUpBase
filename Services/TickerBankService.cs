﻿using Microsoft.Extensions.Configuration;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.EntityFrameworkCore;
using NumbersGoUp.JsonModels;

namespace NumbersGoUp.Services
{
    public class TickerBankService
    {
        private const string EARNINGS_MULTIPLE_CUTOFF_KEY = "EarningsMultipleCutoff";

        private static readonly string[][] CountryTiers = new string[][]
        {
            new string[] {"United States"},
            new string[] {"United Kingdom", "Ireland", "Canada"},
            new string[] {"Australia", "New Zealand", "Israel", "Japan"},
            new string[] {"Denmark", "Netherlands", "Finland", "Iceland", "Belgium", "Germany", "Norway", "Sweden", "Taiwan"},
            new string[] {"Portugal", "France", "Hungary", "Spain", "Singapore", "South Korea", "Switzerland" }
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerBankService> _logger;
        private readonly RateLimiter _rateLimiter;
        private readonly ITickerBankProcessor _tickerProcessor;
        private readonly IConfiguration _configuration;
        private readonly string _environmentName;
        private readonly IStocksContextFactory _contextFactory;
        private readonly IRuntimeSettings _runtimeSettings;
        private readonly DateTime _lookbackDate = DateTime.Now.AddYears(-DataService.LOOKBACK_YEARS);
        public double EarningsMultipleCutoff { get; }
        public string[] TickerBlacklist { get; }

        private readonly IBrokerService _brokerService;

        public TickerBankService(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory httpClientFactory, IStocksContextFactory contextFactory, IRuntimeSettings runtimeSettings,
                                IAppCancellation appCancellation, ILogger<TickerBankService> logger, RateLimiter rateLimiter, ITickerBankProcessor tickerProcessor, IBrokerService brokerService)
        {
            _httpClientFactory = httpClientFactory;
            _appCancellation = appCancellation;
            _logger = logger;
            _rateLimiter = rateLimiter;
            _environmentName = environment.EnvironmentName;
            _tickerProcessor = tickerProcessor;
            _configuration = configuration;
            _contextFactory = contextFactory;
            _runtimeSettings = runtimeSettings;
            EarningsMultipleCutoff = double.TryParse(configuration[EARNINGS_MULTIPLE_CUTOFF_KEY], out var peratioCutoff) ? peratioCutoff : 40;
            _brokerService = brokerService;
            TickerBlacklist = configuration["TickerBlacklist"]?.Split(',') ?? new string[] { };
        }
        public async Task<BankTicker[]> GetTickers(string[] currentPositions, int maxTickers)
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var bankTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                var tickers = new List<BankTicker>(bankTickers.Where(t => currentPositions.Any(s => s == t.Symbol)));
                for(var i = 0; i < CountryTiers.Length; i++)
                {
                    var countryTier = CountryTiers[i];
                    if (tickers.Count < maxTickers)
                    {
                        var toAdd = bankTickers.Where(t =>
                            countryTier.Any(c => string.Equals(c, t.Country, StringComparison.OrdinalIgnoreCase)) &&
                            t.PerformanceVector > 0
                            ).OrderByDescending(t => t.PerformanceVector).Take(maxTickers - tickers.Count);
                        if (i == 0 && !toAdd.Any()) { throw new Exception("No tickers found for first tier country!!!!"); }
                        tickers.AddRange(toAdd);
                    }
                    else { break; }
                }
                return tickers.ToArray();
            }
        }
        public async Task Load()
        {
            try
            {
                var result = await _tickerProcessor.DownloadTickers(_runtimeSettings.ForceDataCollection);
                var processorTickers = result.BankTickers;
                if (processorTickers.Length == 0) { return; }
                _logger.LogInformation($"Loading {processorTickers.Length} tickers into ticker bank");
                var positions = await _brokerService.GetPositions();
                var marketHistory = await _brokerService.GetBarHistoryDay("VTI", _lookbackDate);
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                    List<BankTicker> toUpdate = new List<BankTicker>(), toAdd = new List<BankTicker>(), toRemove = new List<BankTicker>();
                    DateTime now = DateTime.UtcNow;
                    foreach (var processorTicker in processorTickers)
                    {
                        var ticker = processorTicker.Ticker;
                        var dbTicker = dbTickers.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                        var isBlacklisted = TickerBlacklist.TickerAny(ticker);
                        var lastModified = result.LastModified.HasValue ? result.LastModified.Value.DateTime : DateTime.UtcNow;
                        var isCarryover = !isBlacklisted && IsCarryover(processorTicker, lastModified);
                        var passCutoff = !isBlacklisted && LoadCutoff(processorTicker);
                        var hasPosition = dbTicker != null && positions.Any(p => p.Symbol == dbTicker.Symbol);
                        if (!isBlacklisted && processorTicker.RecentEarningsDate.HasValue && processorTicker.RecentEarningsDate.Value.Date.CompareTo(lastModified.Date) == 0)
                        {
                            _logger.LogInformation($"Ignoring {ticker.Symbol}. Recent Earnings Date matches last modified.");
                            continue;
                        }
                        try
                        {
                            if (isCarryover)
                            {
                                if(dbTicker != null) _logger.LogInformation($"Missing values due to recent earnings update for {ticker.Symbol}. Using old values.");
                            }
                            else if (passCutoff || hasPosition)
                            {
                                var bars = await stocksContext.HistoryBars.Where(b => b.Symbol == ticker.Symbol).ToArrayAsync(_appCancellation.Token);
                                var (priceChangeAvg, betaAvg) = CalculatePriceChangeAvg(bars.Length > 0 ? bars : (await _brokerService.GetBarHistoryDay(ticker.Symbol, _lookbackDate)).ToArray(), marketHistory.ToArray());
                                ticker.PriceChangeAvg = priceChangeAvg ?? 0.0;
                                ticker.BetaAvg = betaAvg ?? 0.0;
                                ticker.LastCalculatedFinancials = now;
                                ticker.LastCalculatedFinancialsMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                                if (dbTicker != null)
                                {
                                    _tickerProcessor.UpdateBankTicker(dbTicker, ticker);
                                    toUpdate.Add(dbTicker);
                                }
                                else { toAdd.Add(ticker); }
                            }
                            else if (dbTicker != null)
                            {
                                toRemove.Add(dbTicker);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Error loading info for bank ticker {ticker.Symbol}");
                        }
                    }
                    foreach(var dbTicker in dbTickers)
                    {
                        if(!processorTickers.Any(t => t.Ticker.Symbol == dbTicker.Symbol))
                        {
                            toRemove.Add(dbTicker);
                        }
                    }
                    stocksContext.TickerBank.RemoveRange(toRemove);
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    stocksContext.TickerBank.UpdateRange(toUpdate);
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    stocksContext.TickerBank.AddRange(toAdd);
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error occurred when loading ticker bank data");
            }
            _logger.LogInformation("Completed bank ticker loading.");
            await CalculatePerformance();
            _logger.LogInformation("Completed bank ticker performance calculation.");
        }
        private bool LoadCutoff(ProcessorBankTicker ticker) => ticker.Income > 50000000 && ticker.RevenueGrowth > -15 && BasicCutoff(ticker.Ticker);
        private bool BasicCutoff(BankTicker ticker) => (ticker.CurrentRatio > 1 || ticker.DebtEquityRatio > 0) && 
                                                       (ticker.CurrentRatio > (ticker.DebtEquityRatio * 1.2) || ticker.DebtEquityRatio < 0.9) && (ticker.DebtMinusCash / ticker.MarketCap) < 0.5 && 
                                                       ticker.Earnings > 0 && ticker.DividendYield > 0 && ticker.EPS > 0 && ticker.EVEarnings > 0 && ticker.EVEarnings < EarningsMultipleCutoff;
        private bool IsCarryover(ProcessorBankTicker ticker, DateTime lastDownloaded) => ticker.RecentEarningsDate.HasValue && ticker.RecentEarningsDate.Value.CompareTo(lastDownloaded.AddDays(-15)) > 0 && 
                                                                          ((ticker.Ticker.CurrentRatio == 0 && ticker.Ticker.DebtEquityRatio == 0) || 
                                                                            ticker.Ticker.EVEarnings == 0 || ticker.Ticker.EPS == 0 || 
                                                                            (!ticker.QoQEPSGrowth.HasValue && !ticker.YoYEPSGrowth.HasValue) ||
                                                                            !ticker.RevenueGrowth.HasValue || !ticker.Income.HasValue);
        private async Task CalculatePerformance()
        {
            var now = DateTime.UtcNow;
            var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                var tickers = new List<BankTicker>();
                foreach (var t in dbTickers)
                {
                    //try
                    //{
                    //    if (passCutoff)
                    //    {
                    //        var currentBar = await stocksContext.HistoryBars.Where(b => b.Symbol == t.Symbol).OrderByDescending(b => b.BarDayMilliseconds).FirstOrDefaultAsync(_appCancellation.Token);
                    //        var price = currentBar != null ? currentBar.ClosePrice : (await _brokerService.GetLastTrade(t.Symbol)).Price;
                    //        t.PERatio = price / t.EPS;
                    //        t.MarketCap = price * t.Shares;
                    //        if(t.Earnings > 0) { t.EVEarnings = (t.MarketCap + t.DebtMinusCash) / t.Earnings; }
                    //        t.LastCalculatedFinancials = now;
                    //        t.LastCalculatedFinancialsMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                    //    }
                    //}
                    //catch (Exception e)
                    //{
                    //    _logger.LogError(e, $"Error retrieving latest price info for bank ticker {t.Symbol}");
                    //}
                    if (!TickerBlacklist.TickerAny(t) && BasicCutoff(t) && t.PERatio < EarningsMultipleCutoff && t.PERatio > 1 && t.PriceChangeAvg > 0)
                    {
                        tickers.Add(t);
                    }
                    else
                    {
                        t.LastCalculatedPerformance = now;
                        t.LastCalculatedPerformanceMillis = nowMillis;
                        t.PerformanceVector = 0;
                        stocksContext.TickerBank.Update(t);
                    }
                }
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
                Func<BankTicker, double> performanceFn1 = (t) => Math.Sqrt(t.Earnings);
                Func<BankTicker, double> performanceFn2 = (t) => t.PriceChangeAvg * t.BetaAvg.ZeroReduce(2, 0);
                Func<BankTicker, double> performanceFn3 = (t) => Math.Min(t.DividendYield, 0.06);
                Func<BankTicker, double> performanceFn4 = (t) => 1 - t.EVEarnings.DoubleReduce(EarningsMultipleCutoff, 0);
                Func<BankTicker, double> performanceFn5 = (t) => 1 - (t.DebtMinusCash / t.MarketCap).DoubleReduce(0.5, -0.5);
                var minmax1 = new MinMaxStore<BankTicker>(performanceFn1);
                var minmax2 = new MinMaxStore<BankTicker>(performanceFn2);
                var minmax3 = new MinMaxStore<BankTicker>(performanceFn3);
                var minmax4 = new MinMaxStore<BankTicker>(performanceFn4);
                var minmax5 = new MinMaxStore<BankTicker>(performanceFn5);
                foreach (var ticker in tickers)
                {
                    minmax1.Run(ticker);
                    minmax2.Run(ticker);
                    minmax3.Run(ticker);
                    minmax4.Run(ticker);
                    minmax5.Run(ticker);
                }
                Func<BankTicker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 40) +
                                                                     (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 25) +
                                                                     (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 5) +
                                                                     (performanceFn4(t).DoubleReduce(minmax4.Max, minmax4.Min) * 15) +
                                                                     (performanceFn5(t).DoubleReduce(minmax5.Max, minmax5.Min) * 15);
                var minmaxTotal = new MinMaxStore<BankTicker>(performanceFnTotal);
                foreach (var ticker in tickers)
                {
                    minmaxTotal.Run(ticker);
                }
                foreach (var ticker in tickers)
                {
                    ticker.LastCalculatedPerformance = now;
                    ticker.LastCalculatedPerformanceMillis = nowMillis;
                    ticker.PerformanceVector = performanceFnTotal(ticker).DoubleReduce(minmaxTotal.Max, minmaxTotal.Min) * 100;
                    stocksContext.TickerBank.Update(ticker);
                }
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
            }
        }
        private (double? priceChangeAvg, double? betaAvg) CalculatePriceChangeAvg(HistoryBar[] bars, HistoryBar[] marketBars)
        {
            if(bars == null || bars.Length == 0) { return (null, null); }
            var now = DateTime.Now;
            var datePointer = _lookbackDate;
            List<Double> priceChanges = new List<double>(), betas = new List<double>();
            for (var i = 0; now.CompareTo(datePointer) > 0 && i < 1000; i++)
            {
                var from = datePointer;
                var to = datePointer.AddMonths(6);
                var priceWindow = bars.Where(b => b.BarDay.CompareTo(from) > 0 && b.BarDay.CompareTo(to) < 0).OrderBy(b => b.BarDayMilliseconds).ToArray();
                if (priceWindow.Length > 2 && priceWindow.Last().Price() > 0)
                {
                    var marketPriceWindow = marketBars.Where(b => b.BarDay.CompareTo(from) > 0 && b.BarDay.CompareTo(to) < 0).OrderBy(b => b.BarDayMilliseconds).ToArray();
                    if(marketPriceWindow.Length > 2 && marketPriceWindow.Last().Price() > 0)
                    {
                        betas.Add(priceWindow.CalculateBeta(marketPriceWindow));
                    }
                    var futurePrice = priceWindow.CalculateFutureRegression((b) => b.Price(), 1);
                    priceChanges.Add(priceWindow[0].Price().PercChange(futurePrice) * 100);
                }
                datePointer = to;
            }
            //double? betaAvg = null;
            //if (betas.Count > 0)
            //{
            //    var avg = betas.Average();
            //    betaAvg = alma - avg;
            //}
            if (priceChanges.Count > ((DataService.LOOKBACK_YEARS - 2) * 2))
            {
                double? betaAvg = betas.Count - 1 > 0 ? betas.Take(betas.Count - 1).Average() : null;
                var avg = priceChanges.Average();
                var stdev = Math.Sqrt(priceChanges.Sum(p => Math.Pow(p - avg, 2)) / priceChanges.Count);
                //var mode = priceChanges.OrderBy(p => p).Skip(priceChanges.Count / 2).FirstOrDefault();
                if (stdev > 0)
                {
                    return (avg / stdev, betaAvg);
                }
                else
                {
                    _logger.LogError($"Standard deviation was zero for some reason. Symbol {bars[0].Symbol}");
                    return (avg, betaAvg);
                }
            }
            else
            {
                _logger.LogDebug($"Insufficient price information for {bars[0].Symbol}");
                return (null, null);
            }
        }
    }
}
