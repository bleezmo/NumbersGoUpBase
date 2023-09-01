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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NumbersGoUp.Services
{
    public class TickerService
    {
        public const int MAX_TICKERS = 175;
        public const int MAX_BANK_TICKERS = MAX_TICKERS + 25;
        public const int PERFORMANCE_CUTOFF = 25;

        private const string POLYGON_API_KEY = "polygon_api_key";
        private const string FINNHUB_API_KEY = "finnhub_api_key";
        private const string ALPHAVANTAGE_API_KEY = "alphavantage_api_key";
        private const string SIMFIN_API_KEY = "simfin_api_key";

        private static readonly Dictionary<string, string> _financialsApiKeyLookup = new Dictionary<string, string>
        {
            { POLYGON_API_KEY, string.Empty },
            { FINNHUB_API_KEY, string.Empty },
            { ALPHAVANTAGE_API_KEY, string.Empty },
            { SIMFIN_API_KEY, string.Empty }
        };


        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerService> _logger;
        private readonly IBrokerService _brokerService;
        private readonly RateLimiter _rateLimiter;
        private readonly IConfiguration _configuration;
        private readonly string _environmentName;
        private readonly IStocksContextFactory _contextFactory;
        private readonly IRuntimeSettings _runtimeSettings;
        private readonly TickerBankService _tickerBankService;
        public string[] TickerBlacklist { get; }

        public double PERatioCutoff { get; }

        public TickerService(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory httpClientFactory, IStocksContextFactory contextFactory, IRuntimeSettings runtimeSettings,
                                IAppCancellation appCancellation, ILogger<TickerService> logger, IBrokerService brokerService, RateLimiter rateLimiter, TickerBankService tickerBankService)
        {
            _httpClientFactory = httpClientFactory;
            _appCancellation = appCancellation;
            _logger = logger;
            _brokerService = brokerService;
            _rateLimiter = rateLimiter;
            _environmentName = environment.EnvironmentName;
            _configuration = configuration;
            _contextFactory = contextFactory;
            _runtimeSettings = runtimeSettings;
            _tickerBankService = tickerBankService;
            PERatioCutoff = tickerBankService.EarningsMultipleCutoff * 0.85;
            TickerBlacklist = tickerBankService.TickerBlacklist;
        }
        public async Task<IEnumerable<Ticker>> GetTickers()
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.Where(t => t.PerformanceVector > PERFORMANCE_CUTOFF && t.MonthTrend > -1000)
                                                  .OrderByDescending(t => t.PerformanceVector).Take(MAX_TICKERS).ToListAsync(_appCancellation.Token);
                return tickers.Where(t => !TickerBlacklist.TickerAny(t));
            }
        }
        public async Task<IEnumerable<Ticker>> GetTickers(string[] symbols)
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.Where(t => symbols.Contains(t.Symbol)).ToListAsync(_appCancellation.Token);
                return tickers;
            }
        }
        public async Task<IEnumerable<Ticker>> GetFullTickerList()
        {
            using(var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                return tickers;
            }
        }
        public async Task LoadTickers()
        {
            _logger.LogInformation($"Using PE Ratio cutoff as {PERatioCutoff}");
            try
            {
                //await _tickerBankService.Load();
                //_logger.LogInformation("Loaded bank tickers");
                //await _tickerBankService.CalculatePerformance();
                //_logger.LogInformation("Completed bank ticker performance calculations");
                await Load();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "error occurred when loading ticker data");
                throw;
            }
        }
        public async Task CalculatePerformance()
        {
            var now = DateTimeOffset.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Thursday) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var nowMillis = now.ToUnixTimeMilliseconds();
                    var lookback = now.AddMonths(-6).ToUnixTimeMilliseconds();
                    var tickers = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                    foreach (var ticker in tickers) //filter any positions we currently hold in case we find we want to remove them
                    {
                        ticker.MonthTrend = -1000; //default if ticker is invalid
                        if(TickerBlacklist.Any(s => s == ticker.Symbol))
                        {
                            continue;
                        }
                        var bars = await stocksContext.HistoryBars.Where(bar => bar.Symbol == ticker.Symbol && bar.BarDayMilliseconds > lookback)
                                                      .OrderBy(b => b.BarDayMilliseconds).Include(b => b.BarMetric).ToArrayAsync(_appCancellation.Token);
                        if (bars.Any())
                        {
                            if (now.AddMonths(-5).CompareTo(bars.First().BarDay) > 0 && now.AddDays(-7).CompareTo(bars.Last().BarDay) < 0) //remove anything that doesn't have enough history
                            {
                                var mins = new List<double>(); var maxs = new List<double>();
                                const int monthPeriod = 20;
                                var initialPrice = bars[0].Price();
                                if(initialPrice == 0)
                                {
                                    _logger.LogError($"Error calculating performance. Inital price was zero for {ticker.Symbol}");
                                    continue;
                                }
                                for (var i = bars.Length - (monthPeriod * 5); i < bars.Length; i+=monthPeriod)
                                {
                                    mins.Add((bars.Skip(i).Take(monthPeriod).Min(b => b.Price()) - initialPrice) * 100.0 / initialPrice);
                                    maxs.Add((bars.Skip(i).Take(monthPeriod).Max(b => b.Price()) - initialPrice) * 100.0 / initialPrice);
                                }
                                ticker.PERatio = ticker.EPS > 0 ? (bars.Last().Price() / ticker.EPS) : 1000;
                                ticker.MarketCap = bars.Last().Price() * ticker.Shares;
                                ticker.EVEarnings = ticker.Earnings > 0 ? ((ticker.MarketCap + ticker.DebtMinusCash) / ticker.Earnings) : 1000;
                                if (mins.Count > 0)
                                {
                                    var monthMin = mins.ToArray().CalculateFutureRegression(1);
                                    ticker.MonthTrend = (monthMin - mins.First()) / initialPrice;
                                    ticker.RegressionAngle = maxs.Reverse<double>().ToArray().CalculateVelocityAlma() * bars.Reverse().Select(b => b.BarMetric.AlmaSMA3).ToArray().ApplyAlma(); ;
                                }
                            }
                        }
                    }
                    var toUpdate = new List<Ticker>();
                    foreach (var ticker in tickers)
                    {
                        if (ticker.MonthTrend > -1000 && ticker.PERatio > 0 && ticker.PERatio < PERatioCutoff &&
                             ticker.EVEarnings > 0 && ticker.EVEarnings < _tickerBankService.EarningsMultipleCutoff)
                        {
                            toUpdate.Add(ticker);
                        }
                        else
                        {
                            ticker.PerformanceVector = 0;
                            ticker.LastCalculatedPerformance = now.UtcDateTime;
                            ticker.LastCalculatedPerformanceMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    Func<Ticker, double, double> EarningsRatiosCalc = (t, maxFractional) => (0.5 * (1 - t.PERatio.DoubleReduce(PERatioCutoff, PERatioCutoff * maxFractional))) + (0.5 * (1 - t.EVEarnings.DoubleReduce(_tickerBankService.EarningsMultipleCutoff, _tickerBankService.EarningsMultipleCutoff * maxFractional)));
                    Func<Ticker, double, double> DebtCapCalc = (t, lowerBound) => t.MarketCap > 0 ? (1 - (t.DebtMinusCash / t.MarketCap).DoubleReduce(0.5, lowerBound)) : 0.0;
                    Func<Ticker, double> performanceFn1 = (t) => t.SMASMAAvg.ZeroReduce(20, -20);
                    Func<Ticker, double> performanceFn2 = (t) => (1 - t.MonthTrend.DoubleReduce(20, -20)) * (1 - t.SMASMAAvg.DoubleReduce(20, -20));
                    Func<Ticker, double> performanceFn3 = (t) => t.RegressionAngle * t.SMASMAAvg.ZeroReduce(20, -20);
                    Func<Ticker, double> performanceFnEarnings = (t) => Math.Sqrt(t.MarketCap) * EarningsRatiosCalc(t, 0.5) * DebtCapCalc(t, 0);
                    Func<Ticker, double> performanceFnEarningsRatios = (t) => EarningsRatiosCalc(t, 0);
                    Func<Ticker, double> performanceFnDebtCap = (t) => DebtCapCalc(t, -0.5);
                    var minmax1 = new MinMaxStore<Ticker>(performanceFn1);
                    var minmax2 = new MinMaxStore<Ticker>(performanceFn2);
                    var minmax3 = new MinMaxStore<Ticker>(performanceFn3);
                    var minmaxEarnings = new MinMaxStore<Ticker>(performanceFnEarnings);
                    var minmaxEarningsRatios = new MinMaxStore<Ticker>(performanceFnEarningsRatios);
                    var minmaxDebtCap = new MinMaxStore<Ticker>(performanceFnDebtCap);
                    foreach (var ticker in toUpdate)
                    {
                        minmax1.Run(ticker);
                        minmax2.Run(ticker);
                        minmax3.Run(ticker);
                        minmaxEarnings.Run(ticker);
                        minmaxEarningsRatios.Run(ticker);
                        minmaxDebtCap.Run(ticker);
                    }
                    Func<Ticker, double> performanceFnTotal = (t) =>
                                                                  (performanceFnEarnings(t).DoubleReduceSafe(minmaxEarnings.Max, minmaxEarnings.Min) * 30) +
                                                                  (performanceFnEarningsRatios(t).DoubleReduceSafe(minmaxEarningsRatios.Max, minmaxEarningsRatios.Min) * 10) +
                                                                  (performanceFnDebtCap(t).DoubleReduceSafe(minmaxDebtCap.Max, minmaxDebtCap.Min) * 10) +
                                                                  (performanceFn1(t).DoubleReduceSafe(minmax1.Max, minmax1.Min) * 10) +
                                                                  (performanceFn2(t).DoubleReduceSafe(minmax2.Max, minmax2.Min) * 25) +
                                                                  (performanceFn3(t).DoubleReduceSafe(minmax3.Max, minmax3.Min) * 15);

                    var minmaxTotal = new MinMaxStore<Ticker>(performanceFnTotal);
                    var perfAvg = 0.0;
                    foreach (var ticker in toUpdate)
                    {
                        perfAvg += minmaxTotal.Run(ticker) / toUpdate.Count;
                    }
                    if(minmaxTotal.Max == 0)
                    {
                        _logger.LogError($"No performances. Dunno what to do here");
                        return;
                    }
                    var idealAvg = minmaxTotal.Max / 2;
                    var maxMultiplier = idealAvg / Math.Max(minmaxTotal.Max - perfAvg, idealAvg);
                    foreach (var ticker in toUpdate)
                    {
                        ticker.LastCalculatedPerformance = now.UtcDateTime;
                        ticker.LastCalculatedPerformanceMillis = nowMillis;
                        ticker.PerformanceVector = performanceFnTotal(ticker).DoubleReduce(minmaxTotal.Max * maxMultiplier, minmaxTotal.Min) * 100;
                        stocksContext.Tickers.Update(ticker);
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
        }

        public async Task ApplyAverages()
        {
            var now = DateTimeOffset.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Wednesday) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var nowMillis = now.ToUnixTimeMilliseconds();
                    var tickers = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                    //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                    foreach (var ticker in tickers)
                    {
                        var bars = await stocksContext.BarMetrics.Where(b => b.Symbol == ticker.Symbol).OrderByDescending(b => b.BarDayMilliseconds).Take(500).ToArrayAsync(_appCancellation.Token);
                        if(bars.Length < 500)
                        {
                            _logger.LogError($"Insufficient bar history for {ticker.Symbol}");
                        }
                        if (bars.Any())
                        {
                            ticker.SMASMAAvg = bars.Average(b => b.SMASMA);
                            ticker.SMASMAStDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.SMASMA - ticker.SMASMAAvg, 2)) / bars.Length);

                            ticker.AlmaSma1Avg = bars.Average(b => b.AlmaSMA1);
                            ticker.AlmaSma1StDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.AlmaSMA1 - ticker.AlmaSma1Avg, 2)) / bars.Length);

                            ticker.AlmaSma2Avg = bars.Average(b => b.AlmaSMA2);
                            ticker.AlmaSma2StDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.AlmaSMA2 - ticker.AlmaSma2Avg, 2)) / bars.Length);

                            ticker.AlmaSma3Avg = bars.Average(b => b.AlmaSMA3);
                            ticker.AlmaSma3StDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.AlmaSMA3 - ticker.AlmaSma3Avg, 2)) / bars.Length);

                            ticker.ProfitLossAvg = bars.Average(b => b.ProfitLossPerc);
                            ticker.ProfitLossStDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.ProfitLossPerc - ticker.ProfitLossAvg, 2)) / bars.Length);

                            ticker.AlmaVelStDev = (bars.CalculateVelocityStDev(b => b.AlmaSMA1) + bars.CalculateVelocityStDev(b => b.AlmaSMA2) + bars.CalculateVelocityStDev(b => b.AlmaSMA3)) / 3;
                            ticker.SMAVelStDev = bars.CalculateVelocityStDev(b => b.SMASMA);

                            var maxMonthConsecutiveLosses = 0.0;
                            var consecutiveLosses = 0;
                            const int monthLength = 20;
                            for (var i = 0; i < bars.Length; i += monthLength)
                            {
                                consecutiveLosses = bars.Skip(i).Take(monthLength).Average(b => b.SMASMA) > 0 ? 0 : (consecutiveLosses + 1);
                                maxMonthConsecutiveLosses = maxMonthConsecutiveLosses < consecutiveLosses ? consecutiveLosses : maxMonthConsecutiveLosses;
                            }
                            ticker.MaxMonthConsecutiveLosses = maxMonthConsecutiveLosses;

                            ticker.LastCalculatedAvgs = now.UtcDateTime;
                            ticker.LastCalculatedAvgsMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
        }
        public async Task Load()
        {
            var now = DateTimeOffset.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Tuesday) //don't need to check every day
            {
                await _brokerService.Ready();
                var nowMillis = now.ToUnixTimeMilliseconds();
                var positions = await _brokerService.GetPositions();
                var (bankTickers, remainingPositions) = await _tickerBankService.GetTickers(positions.Select(p => p.Symbol).ToArray(), MAX_BANK_TICKERS);
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
                    var filteredBankTickers = GetFilteredBankTickers(bankTickers).ToArray();
                    var count = tickers.Length;
                    foreach (var ticker in tickers)
                    {
                        var bankTicker1 = filteredBankTickers.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                        var bankTicker2 = bankTickers.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                        var hasPosition = positions.Any(p => p.Symbol == ticker.Symbol);
                        if (bankTicker1 != null)
                        {
                            TickerCopy(ticker, bankTicker1);
                            ticker.LastCalculated = now.UtcDateTime;
                            ticker.LastCalculatedMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else if (bankTicker2 != null && hasPosition)
                        {
                            TickerCopy(ticker, bankTicker2);
                            ticker.LastCalculated = now.UtcDateTime;
                            ticker.LastCalculatedMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else if (hasPosition)
                        {
                            _logger.LogWarning($"Could not remove {ticker.Symbol}. Position exists. Modifying properties to encourage selling.");
                            var positionBankTicker = remainingPositions.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                            if(positionBankTicker != null)
                            {
                                var minEPS = Math.Min(ticker.EPS, positionBankTicker.EPS);
                                var minEarnings = Math.Min(ticker.Earnings, positionBankTicker.Earnings);
                                TickerCopy(ticker, positionBankTicker);
                                ticker.EPS = minEPS * (positionBankTicker.PerformanceVector > 0 ? 0.9 : 0.8);
                                ticker.Earnings = minEarnings * (positionBankTicker.PerformanceVector > 0 ? 0.9 : 0.8);
                            }
                            else
                            { 
                                ticker.EPS *= 0.75;
                                ticker.Earnings *= 0.75;
                            }
                            ticker.LastCalculated = now.UtcDateTime;
                            ticker.LastCalculatedMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else
                        {
                            _logger.LogInformation($"Removing {ticker.Symbol}");
                            stocksContext.Tickers.Remove(ticker);
                            count--;
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    foreach (var bankTicker in filteredBankTickers)
                    {
                        if(count > MAX_TICKERS) { break; }
                        if (!tickers.Any(t => t.Symbol == bankTicker.Symbol) && bankTicker.PERatio < PERatioCutoff)
                        {
                            stocksContext.Tickers.Add(TickerCopy(new Ticker
                            {
                                Symbol = bankTicker.Symbol,
                                LastCalculated = now.UtcDateTime,
                                LastCalculatedMillis = nowMillis
                            }, bankTicker));
                            count++;
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
        }
        private static IEnumerable<BankTicker> GetFilteredBankTickers(BankTicker[] bankTickersFull)
        {
            var sectorDict = new Dictionary<string, List<BankTicker>>();
            foreach (var ticker in bankTickersFull.Where(t => t.PerformanceVector > 0))
            {
                if (sectorDict.TryGetValue(ticker.Sector, out var sectorTickers))
                {
                    sectorTickers.Add(ticker);
                }
                else
                {
                    sectorDict.Add(ticker.Sector, new List<BankTicker>(new[] { ticker }));
                }
            }
            var sectorIterator = sectorDict.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.OrderByDescending(t => t.PerformanceVector).ToArray()));
            var filteredBankTickers = new List<BankTicker>();
            for (var i = 0; filteredBankTickers.Count < MAX_TICKERS && i < bankTickersFull.Length; i++)
            {
                foreach (var sector in sectorIterator)
                {
                    if (i < sector.Value.Length)
                    {
                        filteredBankTickers.Add(sector.Value[i]);
                    }
                }
            }
            return filteredBankTickers.OrderByDescending(t => t.PerformanceVector).Take(MAX_TICKERS);
        }
        private static Ticker TickerCopy(Ticker ticker, BankTicker bankTicker)
        {
            ticker.Sector = bankTicker.Sector;
            ticker.PERatio = bankTicker.PERatio;
            ticker.EVEarnings = bankTicker.EVEarnings;
            ticker.EPS = bankTicker.EPS;
            ticker.Earnings = bankTicker.Earnings;
            ticker.DividendYield = bankTicker.DividendYield;
            ticker.DebtMinusCash = bankTicker.DebtMinusCash;
            ticker.Shares = bankTicker.Shares;
            ticker.MarketCap = bankTicker.MarketCap;
            return ticker;
        }
    }
}
