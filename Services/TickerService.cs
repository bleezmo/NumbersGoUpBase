using Microsoft.Extensions.Configuration;
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
        public const int MAX_TICKERS = 100;
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
        private readonly string[] _blackList;
        
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
            PERatioCutoff = tickerBankService.PERatioCutoff;
            _blackList = configuration["TickerBlacklist"]?.Split(',') ?? new string[] { };
        }
        public async Task<IEnumerable<Ticker>> GetTickers()
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.Where(t => t.PerformanceVector > PERFORMANCE_CUTOFF && t.AvgMonthPerc > -1000)
                                                  .OrderByDescending(t => t.PerformanceVector).Take(MAX_TICKERS).ToListAsync(_appCancellation.Token);
                return tickers.Where(t => !_blackList.Any(s => string.Equals(s, t.Symbol, StringComparison.InvariantCultureIgnoreCase)));
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
                await _tickerBankService.Load();
                await _tickerBankService.CalculatePerformance();
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
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Monday || now.DayOfWeek == DayOfWeek.Thursday) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var nowMillis = now.ToUnixTimeMilliseconds();
                    var cutoff = now.AddDays(-15).ToUnixTimeMilliseconds();
                    var lookback = now.AddYears(-DataService.LOOKBACK_YEARS).ToUnixTimeMilliseconds();
                    var tickersToUpdate = _runtimeSettings.ForceDataCollection ? await stocksContext.Tickers.ToListAsync(_appCancellation.Token) : await stocksContext.Tickers.Where(t => t.LastCalculatedPerformanceMillis == null || t.LastCalculatedPerformanceMillis < cutoff).ToListAsync(_appCancellation.Token);
                    if (tickersToUpdate.Any())
                    {
                        foreach (var ticker in tickersToUpdate) //filter any positions we currently hold in case we find we want to remove them
                        {
                            ticker.AvgMonthPerc = -1000; //default if ticker is invalid
                            //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                            var bars = await stocksContext.HistoryBars.Where(bar => bar.Symbol == ticker.Symbol && bar.BarDayMilliseconds > lookback).OrderBy(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
                            if (bars.Any())
                            {
                                if (now.AddYears(1 - DataService.LOOKBACK_YEARS).CompareTo(bars.First().BarDay) > 0 && now.AddDays(-7).CompareTo(bars.Last().BarDay) < 0) //remove anything that doesn't have enough history
                                {
                                    var maxMonthConsecutiveLosses = 0.0;
                                    var consecutiveLosses = 0;
                                    var slopes = new List<double>();
                                    var monthPeriod = 20;
                                    for (var i = monthPeriod; i < bars.Length; i += monthPeriod)
                                    {
                                        var monthEnd = i + monthPeriod < bars.Length ? monthPeriod + i : bars.Length;
                                        var monthBegin = bars.Skip(i).First().Price();
                                        var avgMonth = ((bars.Skip(monthEnd - 1).First().Price() - monthBegin) * 100) / monthBegin;
                                        slopes.Add(avgMonth);
                                        consecutiveLosses = avgMonth > 0 ? 0 : (consecutiveLosses + 1);
                                        maxMonthConsecutiveLosses = consecutiveLosses > maxMonthConsecutiveLosses ? consecutiveLosses : maxMonthConsecutiveLosses;
                                    }
                                    if (slopes.Count > 0)
                                    {
                                        ticker.AvgMonthPerc = slopes.Average();
                                        ticker.MonthPercVariance = slopes.Sum(s => Math.Pow(s - ticker.AvgMonthPerc, 2)) / slopes.Count;
                                        ticker.MaxMonthConsecutiveLosses = maxMonthConsecutiveLosses;
                                        //ticker.AvgMonthPerc = slopes.OrderBy(s => s).Skip((int)(Convert.ToDouble(slopes.Count) / 2)).Take(1).First();
                                        ticker.PERatio = ticker.EPS > 0 ? bars.Last().Price() / ticker.EPS : 1000;
                                    }
                                }
                            }
                            stocksContext.Tickers.Update(ticker);
                        }
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                        var toUpdate = new List<Ticker>();
                        var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
                        foreach(var ticker in tickers)
                        {
                            if(ticker.AvgMonthPerc > -1000 && ticker.PERatio > 0 && ticker.PERatio < PERatioCutoff)
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
                        Func<Ticker, double> performanceFn1 = (t) => t.MonthPercVariance > 0 ? t.AvgMonthPerc * (1 - t.MaxMonthConsecutiveLosses.DoubleReduce(12, 1)) / Math.Sqrt(t.MonthPercVariance) : 0.0;
                        Func<Ticker, double> performanceFn2 = (t) => t.ProfitLossStDev > 0 ? t.ProfitLossAvg / t.ProfitLossStDev : 0.0;
                        Func<Ticker, double> performanceFn3 = (t) => t.SMASMAStDev > 0 ? t.SMASMAAvg / t.SMASMAStDev : 0.0;
                        Func<Ticker, double> performanceFnEarnings = (t) => Math.Sqrt(t.EBIT) * 2;
                        Func<Ticker, double> performanceFnPE = (t) => 1 - t.PERatio.DoubleReduce(PERatioCutoff, 0);
                        var minmax1 = new MinMaxStore<Ticker>(performanceFn1);
                        var minmax2 = new MinMaxStore<Ticker>(performanceFn2);
                        var minmax3 = new MinMaxStore<Ticker>(performanceFn3);
                        var minmaxEarnings = new MinMaxStore<Ticker>(performanceFnEarnings);
                        var minmaxPE = new MinMaxStore<Ticker>(performanceFnPE);
                        foreach (var ticker in toUpdate)
                        {
                            minmax1.Run(ticker);
                            minmax2.Run(ticker);
                            minmax3.Run(ticker);
                            minmaxEarnings.Run(ticker);
                            minmaxPE.Run(ticker);
                        }
                        Func<Ticker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 30) +
                                                                      (performanceFnEarnings(t).DoubleReduce(minmaxEarnings.Max, minmaxEarnings.Min) * 30) +
                                                                      (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 25) +
                                                                      (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 10) +
                                                                      (performanceFnPE(t).DoubleReduce(minmaxPE.Max, minmaxPE.Min) * 5);
                        var minmaxTotal = new MinMaxStore<Ticker>(performanceFnTotal);
                        var perfAvg = 0.0;
                        foreach (var ticker in toUpdate)
                        {
                            perfAvg += minmaxTotal.Run(ticker) / toUpdate.Count;
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
        }

        public async Task ApplyAverages()
        {
            var now = DateTimeOffset.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Monday || now.DayOfWeek == DayOfWeek.Thursday) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var nowMillis = now.ToUnixTimeMilliseconds();
                    var cutoff = now.AddDays(-15).ToUnixTimeMilliseconds();
                    var lookback = now.AddYears(-DataService.LOOKBACK_YEARS).ToUnixTimeMilliseconds();
                    var tickersToUpdate = _runtimeSettings.ForceDataCollection ? await stocksContext.Tickers.ToListAsync(_appCancellation.Token) : await stocksContext.Tickers.Where(t => t.LastCalculatedAvgsMillis == null || t.LastCalculatedAvgsMillis < cutoff).ToListAsync(_appCancellation.Token);
                    var updatedTickers = new List<Ticker>();
                    //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                    foreach (var ticker in tickersToUpdate)
                    {
                        var bars = await stocksContext.BarMetrics.Where(b => b.Symbol == ticker.Symbol && b.BarDayMilliseconds > lookback).OrderByDescending(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
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

                            ticker.LastCalculatedAvgs = now.UtcDateTime;
                            ticker.LastCalculatedAvgsMillis = nowMillis;
                            updatedTickers.Add(ticker);
                        }
                    }
                    if (updatedTickers.Count > 0)
                    {
                        stocksContext.Tickers.UpdateRange(updatedTickers);
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                }
            }
        }

        public async Task Load()
        {
            var now = DateTimeOffset.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Tuesday || now.DayOfWeek == DayOfWeek.Friday) //don't need to check every day
            {
                await _brokerService.Ready();
                var nowMillis = now.ToUnixTimeMilliseconds();
                var cutoff = now.AddDays(-30).ToUnixTimeMilliseconds();
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
                    var toUpdate = _runtimeSettings.ForceDataCollection ? tickers : tickers.Where(t => t.LastCalculatedMillis < cutoff).ToArray();
                    var bankTickers = await stocksContext.TickerBank.Where(t => t.PerformanceVector > 0).OrderByDescending(t => t.PerformanceVector).Take(215).ToArrayAsync(_appCancellation.Token);
                    if (toUpdate.Any())
                    {
                        var positions = await _brokerService.GetPositions();
                        foreach (var ticker in toUpdate)
                        {
                            var bankTicker = bankTickers.FirstOrDefault(t => ticker.Symbol == t.Symbol);
                            if (bankTicker != null)
                            {
                                ticker.Sector = bankTicker.Sector;
                                ticker.PERatio = bankTicker.PERatio;
                                ticker.EPS = bankTicker.EPS;
                                ticker.EBIT = bankTicker.Earnings;
                                ticker.DividendYield = bankTicker.DividendYield;
                                ticker.LastCalculated = now.UtcDateTime;
                                ticker.LastCalculatedMillis = nowMillis;
                                stocksContext.Tickers.Update(ticker);
                            }
                            else if(bankTickers.Length < 100 && ticker.PerformanceVector > 50) //keep some tickers if the bank size is to small
                            {
                                ticker.LastCalculated = now.UtcDateTime;
                                ticker.LastCalculatedMillis = nowMillis;
                                stocksContext.Tickers.Update(ticker);
                            }
                            else if (positions.Any(p => p.Symbol == ticker.Symbol))
                            {
                                _logger.LogWarning($"Could not remove {ticker.Symbol}. Position exists. Modifying properties to encourage selling.");
                                ticker.PERatio = 1000;
                                ticker.EPS = 0.01;
                                ticker.EBIT = 1;
                                ticker.PerformanceVector = 0;
                                ticker.LastCalculated = now.UtcDateTime;
                                ticker.LastCalculatedMillis = nowMillis;
                                stocksContext.Tickers.Update(ticker);
                            }
                            else
                            {
                                _logger.LogDebug($"Removing {ticker.Symbol}");
                                stocksContext.Tickers.Remove(ticker);
                            }
                        }
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                    foreach (var bankTicker in bankTickers)
                    {
                        if (!tickers.Any(t => t.Symbol == bankTicker.Symbol))
                        {
                            stocksContext.Tickers.Add(new Ticker
                            {
                                Symbol = bankTicker.Symbol,
                                Sector = bankTicker.Sector,
                                EBIT = bankTicker.Earnings,
                                DividendYield = bankTicker.DividendYield,
                                EPS = bankTicker.EPS,
                                PERatio = bankTicker.PERatio,
                                LastCalculated = now.UtcDateTime,
                                LastCalculatedMillis = nowMillis
                            });
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
        }
#if DEBUG

        public async Task TestAveragesAndPerformance(DateTime? cutoff = null)
        {
            var now = cutoff.HasValue ? new DateTimeOffset(cutoff.Value) : DateTimeOffset.UtcNow;
            var nowMillis = now.ToUnixTimeMilliseconds();
            var lookback = now.AddYears(-DataService.LOOKBACK_YEARS);
            var lookbackMillis = lookback.ToUnixTimeMilliseconds();
            _logger.LogInformation($"Calculating performance from {lookback:yyyy-MM-dd} to {cutoff:yyyy-MM-dd}");
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickersToUpdate = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                var updatedTickers = new List<Ticker>();
                //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                foreach (var ticker in tickersToUpdate)
                {
                    var bars = await stocksContext.BarMetrics.Where(b => b.Symbol == ticker.Symbol && b.BarDayMilliseconds > lookbackMillis && b.BarDayMilliseconds < nowMillis).OrderByDescending(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
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

                        ticker.LastCalculatedAvgs = now.UtcDateTime;
                        ticker.LastCalculatedAvgsMillis = nowMillis;
                        updatedTickers.Add(ticker);
                    }
                }
                if (updatedTickers.Count > 0)
                {
                    stocksContext.Tickers.UpdateRange(updatedTickers);
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickersToUpdate = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                if (tickersToUpdate.Any())
                {
                    foreach (var ticker in tickersToUpdate) //filter any positions we currently hold in case we find we want to remove them
                    {
                        ticker.AvgMonthPerc = -1000; //default if ticker is invalid
                                                     //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                        var bars = await stocksContext.HistoryBars.Where(bar => bar.Symbol == ticker.Symbol && bar.BarDayMilliseconds > lookbackMillis && bar.BarDayMilliseconds < nowMillis).OrderBy(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
                        if (bars.Any())
                        {
                            if (now.AddYears(1 - DataService.LOOKBACK_YEARS).CompareTo(bars.First().BarDay) > 0 && now.AddDays(-7).CompareTo(bars.Last().BarDay) < 0)
                            {
                                var maxMonthConsecutiveLosses = 0.0;
                                var consecutiveLosses = 0;
                                var slopes = new List<double>();
                                var monthPeriod = 20;
                                for (var i = monthPeriod; i < bars.Length; i += monthPeriod)
                                {
                                    var monthEnd = i + monthPeriod < bars.Length ? monthPeriod + i : bars.Length;
                                    var monthBegin = bars.Skip(i).First().Price();
                                    var avgMonth = ((bars.Skip(monthEnd - 1).First().Price() - monthBegin) * 100) / monthBegin;
                                    slopes.Add(avgMonth);
                                    consecutiveLosses = avgMonth > 0 ? 0 : (consecutiveLosses + 1);
                                    maxMonthConsecutiveLosses = consecutiveLosses > maxMonthConsecutiveLosses ? consecutiveLosses : maxMonthConsecutiveLosses;
                                }
                                if (slopes.Count > 0)
                                {
                                    ticker.AvgMonthPerc = slopes.Average();
                                    ticker.MonthPercVariance = slopes.Sum(s => Math.Pow(s - ticker.AvgMonthPerc, 2)) / slopes.Count;
                                    ticker.MaxMonthConsecutiveLosses = maxMonthConsecutiveLosses;
                                    ticker.PERatio = ticker.EPS > 0 ? bars.Last().Price() / ticker.EPS : 1000;
                                }
                            }
                        }
                        stocksContext.Tickers.Update(ticker);
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    var toUpdate = new List<Ticker>();
                    var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
                    foreach (var ticker in tickers)
                    {
                        if (ticker.AvgMonthPerc > -1000 && ticker.PERatio > 0 && ticker.PERatio < PERatioCutoff)
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
                    Func<Ticker, double> performanceFn1 = (t) => t.MonthPercVariance > 0 ? t.AvgMonthPerc * (1 - t.MaxMonthConsecutiveLosses.DoubleReduce(12, 1)) / Math.Sqrt(t.MonthPercVariance) : 0.0;
                    Func<Ticker, double> performanceFn2 = (t) => t.ProfitLossStDev > 0 ? t.ProfitLossAvg / t.ProfitLossStDev : 0.0;
                    Func<Ticker, double> performanceFn3 = (t) => t.SMASMAStDev > 0 ? t.SMASMAAvg / t.SMASMAStDev : 0.0;
                    Func<Ticker, double> performanceFnEarnings = (t) => Math.Sqrt(t.EBIT) * 2;
                    Func<Ticker, double> performanceFnPE = (t) => 1 - t.PERatio.DoubleReduce(PERatioCutoff, 0);
                    var minmax1 = new MinMaxStore<Ticker>(performanceFn1);
                    var minmax2 = new MinMaxStore<Ticker>(performanceFn2);
                    var minmax3 = new MinMaxStore<Ticker>(performanceFn3);
                    var minmaxEarnings = new MinMaxStore<Ticker>(performanceFnEarnings);
                    var minmaxPE = new MinMaxStore<Ticker>(performanceFnPE);
                    foreach (var ticker in toUpdate)
                    {
                        minmax1.Run(ticker);
                        minmax2.Run(ticker);
                        minmax3.Run(ticker);
                        minmaxEarnings.Run(ticker);
                        minmaxPE.Run(ticker);
                    }
                    Func<Ticker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 30) +
                                                                  (performanceFnEarnings(t).DoubleReduce(minmaxEarnings.Max, minmaxEarnings.Min) * 30) +
                                                                  (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 25) +
                                                                  (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 10) +
                                                                  (performanceFnPE(t).DoubleReduce(minmaxPE.Max, minmaxPE.Min) * 5);
                    var minmaxTotal = new MinMaxStore<Ticker>(performanceFnTotal);
                    var perfAvg = 0.0;
                    foreach (var ticker in toUpdate)
                    {
                        perfAvg += minmaxTotal.Run(ticker) / toUpdate.Count;
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
#endif
    }
}
