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
        public const int MAX_BANK_TICKERS = 200;

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
        public string[] TickerWhitelist { get; }
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
            TickerWhitelist = tickerBankService.TickerWhitelist;
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
                await _tickerBankService.Load();
                _logger.LogInformation("Loaded bank tickers");
                await _tickerBankService.CalculatePerformance();
                _logger.LogInformation("Completed bank ticker performance calculations");
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
                    var lookback = now.AddYears(-DataService.LOOKBACK_YEARS).ToUnixTimeMilliseconds();
                    var gaussianWeights = Utils.Utils.GaussianWeights(24 * DataService.LOOKBACK_YEARS);
                    var tickers = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                    foreach (var ticker in tickers) //filter any positions we currently hold in case we find we want to remove them
                    {
                        ticker.MonthTrend = -1000; //default if ticker is invalid
                                                     //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                        var bars = await stocksContext.HistoryBars.Where(bar => bar.Symbol == ticker.Symbol && bar.BarDayMilliseconds > lookback).OrderBy(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
                        if (bars.Any())
                        {
                            if (now.AddYears(1 - DataService.LOOKBACK_YEARS).CompareTo(bars.First().BarDay) > 0 && now.AddDays(-7).CompareTo(bars.Last().BarDay) < 0) //remove anything that doesn't have enough history
                            {
                                var currentMin = 0.0;
                                var slopes = new List<double>();
                                const int monthPeriod = 20;
                                var initialPrice = bars[0].Price();
                                if(initialPrice == 0)
                                {
                                    _logger.LogError($"Error calculating performance. Inital price was zero for {ticker.Symbol}");
                                    continue;
                                }
                                double x = 0.0, y = 0.0, xsqr = 0.0, xy = 0.0;
                                for (var i = 0; i < bars.Length; i++)
                                {
                                    if (i % monthPeriod == 0)
                                    {
                                        var min = (bars.Skip(i).Take(monthPeriod).Min(b => b.LowPrice) - initialPrice) * 100 / initialPrice;
                                        slopes.Add(min - currentMin);
                                        currentMin = min;
                                    }
                                    var perc = (bars[i].Price() - initialPrice) * 100 / initialPrice;
                                    y += perc;
                                    x += i;
                                    xsqr += Math.Pow(i, 2);
                                    xy += perc * i;
                                }
                                var regressionDenom = (bars.Length * xsqr) - Math.Pow(x, 2);
                                var regressionSlope = regressionDenom != 0 ? ((bars.Length * xy) - (x * y)) / regressionDenom : 0.0;
                                var yintercept = (y - (regressionSlope * x)) / bars.Length;
                                var regressionStDev = Math.Sqrt(bars.Select((bar, i) =>
                                {
                                    var perc = (bar.Price() - initialPrice) * 100 / initialPrice;
                                    var regression = (regressionSlope * i) + yintercept;
                                    return Math.Pow(perc - regression, 2);
                                }).Sum() / bars.Length) * 2;
                                var currentRegression = (regressionSlope * (bars.Length - 1)) + yintercept;
                                var currentPerc = (bars[bars.Length - 1].Price() - initialPrice) * 100 / initialPrice;
                                ticker.RegressionAngle = Utils.Utils.GetAngle(currentPerc - currentRegression, regressionStDev) * regressionSlope;
                                ticker.PERatio = ticker.EPS > 0 ? (bars.Last().Price() / ticker.EPS) : 1000;
                                ticker.MarketCap = bars.Last().Price() * ticker.Shares;
                                ticker.EVEarnings = ticker.Earnings > 0 ? ((ticker.MarketCap + ticker.DebtMinusCash) / ticker.Earnings) : 1000;
                                var debtCapRatio = ticker.DebtMinusCash / ticker.MarketCap;
                                if (slopes.Count > 0)
                                {
                                    ticker.MonthTrend = slopes.Reverse<double>().ToArray().ApplyAlma(gaussianWeights);
                                }
                            }
                        }
                    }
                    var toUpdate = new List<Ticker>();
                    foreach (var ticker in tickers)
                    {
                        if (TickerWhitelist.TickerAny(ticker))
                        {
                            ticker.PerformanceVector = 100;
                            ticker.LastCalculatedPerformance = now.UtcDateTime;
                            ticker.LastCalculatedPerformanceMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else if (ticker.MonthTrend > -1000 && ticker.PERatio > 0 && ticker.PERatio < PERatioCutoff &&
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
                    Func<Ticker, double> performanceFn1 = (t) => (0.9 * t.MonthTrend) + (0.1 * (1 - t.MaxMonthConsecutiveLosses.DoubleReduce(20, 1)));
                    Func<Ticker, double> performanceFn2 = (t) => t.ProfitLossStDev > 0 ? Math.Pow(t.ProfitLossAvg, 2) / Math.Pow(t.ProfitLossStDev, 2) : 0;
                    Func<Ticker, double> performanceFnRegression = (t) => 1 - t.RegressionAngle.DoubleReduce(45, -45);
                    Func<Ticker, double> performanceFnEarnings = (t) => Math.Sqrt(t.MarketCap) * EarningsRatiosCalc(t, 0.4) * DebtCapCalc(t, 0);
                    Func<Ticker, double> performanceFnEarningsRatios = (t) => EarningsRatiosCalc(t, 0);
                    Func<Ticker, double> performanceFnDebtCap = (t) => DebtCapCalc(t, -0.5);
                    var minmax1 = new MinMaxStore<Ticker>(performanceFn1);
                    var minmax2 = new MinMaxStore<Ticker>(performanceFn2);
                    var minmaxRegression = new MinMaxStore<Ticker>(performanceFnRegression);
                    var minmaxEarnings = new MinMaxStore<Ticker>(performanceFnEarnings);
                    var minmaxEarningsRatios = new MinMaxStore<Ticker>(performanceFnEarningsRatios);
                    var minmaxDebtCap = new MinMaxStore<Ticker>(performanceFnDebtCap);
                    foreach (var ticker in toUpdate)
                    {
                        minmax1.Run(ticker);
                        minmax2.Run(ticker);
                        minmaxRegression.Run(ticker);
                        minmaxEarnings.Run(ticker);
                        minmaxEarningsRatios.Run(ticker);
                        minmaxDebtCap.Run(ticker);
                    }
                    Func<Ticker, double> performanceFnTotal = (t) =>
                                                                  (performanceFnEarnings(t).DoubleReduce(minmaxEarnings.Max, minmaxEarnings.Min) * 30) +
                                                                  (performanceFnEarningsRatios(t).DoubleReduce(minmaxEarningsRatios.Max, minmaxEarningsRatios.Min) * 10) +
                                                                  (performanceFnDebtCap(t).DoubleReduce(minmaxDebtCap.Max, minmaxDebtCap.Min) * 10) +
                                                                  (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 15) +
                                                                  (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 15) +
                                                                  (performanceFnRegression(t).DoubleReduce(minmaxRegression.Max, minmaxRegression.Min) * 20);

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
                    var lookback = now.AddYears(-DataService.LOOKBACK_YEARS).ToUnixTimeMilliseconds();
                    var tickers = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                    //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                    foreach (var ticker in tickers)
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
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
                    var bankTickers = await stocksContext.TickerBank.Where(t => t.PerformanceVector > 0).ToArrayAsync(_appCancellation.Token);
                    bankTickers = GetFilteredBankTickers(bankTickers).Where(t => t.PERatio < PERatioCutoff || TickerWhitelist.Any(symbol => symbol == t.Symbol)).ToArray();
                    var positions = await _brokerService.GetPositions();
                    var positionSymbols = positions.Select(p => p.Symbol).ToArray();
                    var bankTickerPositions = await stocksContext.TickerBank.Where(t => positionSymbols.Contains(t.Symbol)).ToArrayAsync(_appCancellation.Token);
                    var count = tickers.Length;
                    foreach (var ticker in tickers)
                    {
                        var bankTicker = bankTickers.FirstOrDefault(t => ticker.Symbol == t.Symbol);
                        if (bankTicker != null)
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
                            ticker.LastCalculated = now.UtcDateTime;
                            ticker.LastCalculatedMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else if (bankTickers.Length < 100 && ticker.PerformanceVector > 50) //keep some tickers if the bank size is to small
                        {
                            ticker.LastCalculated = now.UtcDateTime;
                            ticker.LastCalculatedMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else if (positions.Any(p => p.Symbol == ticker.Symbol))
                        {
                            _logger.LogWarning($"Could not remove {ticker.Symbol}. Position exists. Modifying properties to encourage selling.");
                            var bankTickerPosition = bankTickerPositions.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                            if (bankTickerPosition != null)
                            {
                                ticker.Sector = bankTickerPosition.Sector;
                                ticker.PERatio = bankTickerPosition.PERatio;
                                ticker.EVEarnings = bankTickerPosition.EVEarnings;
                                ticker.EPS = Math.Min(bankTickerPosition.EPS, ticker.EPS) * 2 / 3;
                                ticker.Earnings = Math.Min(bankTickerPosition.Earnings, ticker.EPS) * 2 / 3;
                                ticker.DividendYield = bankTickerPosition.DividendYield;
                                ticker.DebtMinusCash = bankTickerPosition.DebtMinusCash;
                                ticker.Shares = bankTickerPosition.Shares;
                                ticker.MarketCap = bankTickerPosition.MarketCap;
                            }
                            else
                            {
                                ticker.EPS *= 0.5;
                                ticker.Earnings *= 0.5;
                                ticker.PerformanceVector *= 0.8;
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
                    foreach (var bankTicker in bankTickers)
                    {
                        if(count > 200) { break; }
                        if (!tickers.Any(t => t.Symbol == bankTicker.Symbol))
                        {
                            stocksContext.Tickers.Add(new Ticker
                            {
                                Symbol = bankTicker.Symbol,
                                Sector = bankTicker.Sector,
                                PERatio = bankTicker.PERatio,
                                EPS = bankTicker.EPS,
                                Earnings = bankTicker.Earnings,
                                EVEarnings = bankTicker.EVEarnings,
                                DividendYield = bankTicker.DividendYield,
                                DebtMinusCash = bankTicker.DebtMinusCash,
                                Shares = bankTicker.Shares,
                                MarketCap = bankTicker.MarketCap,
                                LastCalculated = now.UtcDateTime,
                                LastCalculatedMillis = nowMillis
                            });
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
            foreach (var ticker in bankTickersFull)
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
            for (var i = 0; filteredBankTickers.Count < MAX_BANK_TICKERS && i < bankTickersFull.Length; i++)
            {
                foreach (var sector in sectorIterator)
                {
                    if (i < sector.Value.Length)
                    {
                        filteredBankTickers.Add(sector.Value[i]);
                    }
                }
            }
            return filteredBankTickers.OrderByDescending(t => t.PerformanceVector).Take(MAX_BANK_TICKERS);
        }
    }
}
