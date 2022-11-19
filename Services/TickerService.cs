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
        public const double PERATIO_CUTOFF = 30;
        public const int MAX_TICKERS = 100;

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
            _blackList = configuration["TickerBlacklist"]?.Split(',') ?? new string[] { };
        }
        public async Task<IEnumerable<Ticker>> GetTickers()
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.Where(t => t.PerformanceVector > 25 && t.AvgMonthPerc > -1000)
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
            var now = DateTime.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Monday || now.DayOfWeek == DayOfWeek.Thursday) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                    var cutoff = new DateTimeOffset(now.AddDays(-15)).ToUnixTimeMilliseconds();
                    var tickersToUpdate = _runtimeSettings.ForceDataCollection ? await stocksContext.Tickers.ToListAsync(_appCancellation.Token) : await stocksContext.Tickers.Where(t => t.LastCalculatedPerformanceMillis == null || t.LastCalculatedPerformanceMillis < cutoff).ToListAsync(_appCancellation.Token);
                    if (tickersToUpdate.Any())
                    {
                        foreach (var ticker in tickersToUpdate) //filter any positions we currently hold in case we find we want to remove them
                        {
                            ticker.AvgMonthPerc = -1000; //default if ticker is invalid
                            //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                            var bars = await stocksContext.HistoryBars.Where(bar => bar.Symbol == ticker.Symbol).OrderBy(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
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
                            if(ticker.AvgMonthPerc > -1000 && ticker.PERatio > 0 && ticker.PERatio < PERATIO_CUTOFF)
                            {
                                toUpdate.Add(ticker);
                            }
                            else
                            {
                                ticker.PerformanceVector = 0;
                                ticker.LastCalculatedPerformance = now;
                                ticker.LastCalculatedPerformanceMillis = nowMillis;
                                stocksContext.Tickers.Update(ticker);
                            }
                        }
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                        Func<Ticker, double> performanceFn1 = (t) => t.MonthPercVariance > 0 ? t.AvgMonthPerc * (1 - t.MaxMonthConsecutiveLosses.DoubleReduce(12, 1)) / t.MonthPercVariance : 0.0;
                        Func<Ticker, double> performanceFn2 = (t) => t.ProfitLossStDev > 0 ? t.ProfitLossAvg / Math.Pow(t.ProfitLossStDev, 2) : 0.0;
                        Func<Ticker, double> performanceFn3 = (t) => t.SMASMAAvg / t.SMASMAStDev;
                        Func<Ticker, double> performanceFnEarnings = (t) => Math.Sqrt(t.EBIT) * 2;
                        var minmax1 = new MinMaxStore<Ticker>(performanceFn1);
                        var minmax2 = new MinMaxStore<Ticker>(performanceFn2);
                        var minmax3 = new MinMaxStore<Ticker>(performanceFn3);
                        var minmaxEarnings = new MinMaxStore<Ticker>(performanceFnEarnings);
                        foreach (var ticker in toUpdate)
                        {
                            minmax1.Run(ticker);
                            minmax2.Run(ticker);
                            minmax3.Run(ticker);
                            minmaxEarnings.Run(ticker);
                        }
                        Func<Ticker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 30) +
                                                                      (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 30) +
                                                                      (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 10) +
                                                                      (performanceFnEarnings(t).DoubleReduce(minmaxEarnings.Max, minmaxEarnings.Min) * 30);
                        var minmaxTotal = new MinMaxStore<Ticker>(performanceFnTotal);
                        foreach (var ticker in toUpdate)
                        {
                            minmaxTotal.Run(ticker);
                        }
                        foreach (var ticker in toUpdate)
                        {
                            ticker.LastCalculatedPerformance = now;
                            ticker.LastCalculatedPerformanceMillis = nowMillis;
                            ticker.PerformanceVector = performanceFnTotal(ticker).DoubleReduce(minmaxTotal.Max * 0.9, minmaxTotal.Min) * 100;
                            stocksContext.Tickers.Update(ticker);
                        }
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                }
            }
        }

        public async Task ApplyAverages()
        {
            var now = DateTime.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Monday || now.DayOfWeek == DayOfWeek.Thursday) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                    var cutoff = new DateTimeOffset(now.AddDays(-15)).ToUnixTimeMilliseconds();
                    var tickersToUpdate = _runtimeSettings.ForceDataCollection ? await stocksContext.Tickers.ToListAsync(_appCancellation.Token) : await stocksContext.Tickers.Where(t => t.LastCalculatedAvgsMillis == null || t.LastCalculatedAvgsMillis < cutoff).ToListAsync(_appCancellation.Token);
                    var updatedTickers = new List<Ticker>();
                    //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                    foreach (var ticker in tickersToUpdate)
                    {
                        var bars = await stocksContext.BarMetrics.Where(b => b.Symbol == ticker.Symbol).ToArrayAsync();
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

                            ticker.LastCalculatedAvgs = now;
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
            var now = DateTime.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Tuesday || now.DayOfWeek == DayOfWeek.Friday) //don't need to check every day
            {
                await _brokerService.Ready();
                var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                var offsetDays = -30 - new Random().Next(15); //don't want to calculate all at once since it takes forever
                var cutoff = new DateTimeOffset(now.AddDays(offsetDays)).ToUnixTimeMilliseconds();
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
                    var toUpdate = _runtimeSettings.ForceDataCollection ? tickers : tickers.Where(t => t.LastCalculatedMillis < cutoff).ToArray();
                    var bankTickers = await stocksContext.TickerBank.Where(t => t.PerformanceVector > 0).OrderByDescending(t => t.PerformanceVector).Take(225).ToArrayAsync(_appCancellation.Token);
                    if (toUpdate.Any())
                    {
                        var positions = await _brokerService.GetPositions();
                        foreach (var ticker in toUpdate)
                        {
                            var bankTicker = bankTickers.FirstOrDefault(t => ticker.Symbol == t.Symbol);
                            if (bankTicker != null)
                            {
                                ticker.PERatio = bankTicker.PERatio;
                                ticker.EPS = bankTicker.EPS;
                                ticker.EBIT = bankTicker.Earnings;
                                ticker.LastCalculated = now;
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
                                ticker.LastCalculated = now;
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
                            var info = await _brokerService.GetTickerInfo(bankTicker.Symbol);
                            _logger.LogDebug($"Completed ticker info retrieval for {bankTicker.Symbol}");
                            if(info != null && info.IsTradable)
                            {
                                stocksContext.Tickers.Add(new Ticker
                                {
                                    Symbol = bankTicker.Symbol,
                                    Sector = bankTicker.Sector,
                                    EBIT = bankTicker.Earnings,
                                    EPS = bankTicker.EPS,
                                    PERatio = bankTicker.PERatio,
                                    LastCalculated = now,
                                    LastCalculatedMillis = nowMillis
                                });
                            }
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
        }
        //private async Task<JToken> GetJson(string url)
        //{
        //    using (var client = _httpClientFactory.CreateClient())
        //    {
        //        var response = await client.GetAsync(url, _appCancellation.Token);
        //        response.EnsureSuccessStatusCode();
        //        using var streamReader = new StreamReader(await response.Content.ReadAsStreamAsync(_appCancellation.Token));
        //        using var jsonReader = new JsonTextReader(streamReader);
        //        var jtok = JToken.Load(jsonReader);
        //        return jtok;
        //    }
        //}

        //public async Task UpdateFinancialsOld()
        //{
        //    var now = DateTime.UtcNow;
        //    if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == DayOfWeek.Tuesday || now.DayOfWeek == DayOfWeek.Friday) //don't need to check every day
        //    {
        //        var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        //        LoadFinancialApiKeys();
        //        using (var stocksContext = _contextFactory.CreateDbContext())
        //        {
        //            var offsetDays = -45 - new Random().Next(30); //don't want to calculate all at once since it takes forever
        //            var cutoff = new DateTimeOffset(now.AddDays(offsetDays)).ToUnixTimeMilliseconds();
        //            var tickersToUpdate = _runtimeSettings.ForceDataCollection ? await stocksContext.Tickers.ToListAsync(_appCancellation.Token) : await stocksContext.Tickers.Where(t => t.LastCalculatedMillis < cutoff).ToListAsync(_appCancellation.Token);
        //            foreach (var ticker in tickersToUpdate)
        //            {
        //                var brokerFinancials = await LoadBrokerFinancials(ticker);
        //                if (brokerFinancials?.EPS == null)
        //                {
        //                    if (!await LoadAlphavantageFinancials(ticker))
        //                    {
        //                        if (!await LoadFinnHubEarnings(ticker))
        //                        {
        //                            await LoadSimfinEarnings(ticker);
        //                        }
        //                    }
        //                }
        //                if (brokerFinancials?.EBIT == null)
        //                {
        //                    await LoadFinnHubEBIT(ticker);
        //                }
        //                ticker.LastCalculated = now;
        //                ticker.LastCalculatedMillis = nowMillis;
        //                stocksContext.Tickers.Update(ticker);
        //                await stocksContext.SaveChangesAsync(_appCancellation.Token);
        //                _logger.LogDebug($"Stored financials for {ticker.Symbol}");
        //            }
        //        }
        //    }
        //}
        //private void LoadFinancialApiKeys()
        //{
        //    foreach (var kv in _financialsApiKeyLookup.ToList())
        //    {
        //        var apiKey = _configuration[$"{kv.Key}:{_environmentName}"];
        //        if (apiKey == null)
        //        {
        //            _logger.LogError($"No api key for {kv.Key} found!!!");
        //            return;
        //        }
        //        _financialsApiKeyLookup[kv.Key] = apiKey;
        //    }
        //}
        //private async Task<bool> LoadAlphavantageFinancials(Ticker ticker)
        //{
        //    var apiKey = _financialsApiKeyLookup[ALPHAVANTAGE_API_KEY];
        //    await _rateLimiter.LimitAlphavantageRate();
        //    try
        //    {
        //        var jobj = (JObject) await GetJson($"https://www.alphavantage.co/query?function=EARNINGS&symbol={ticker.Symbol.ToUpper()}&apikey={apiKey}");
        //        if (jobj.ContainsKey("quarterlyEarnings") && jobj["quarterlyEarnings"].Count() > 0)
        //        {
        //            var eps = 0.0;
        //            var foundQuarters = 0;
        //            for (var i = 0; i < 4; i++)
        //            {
        //                if(double.TryParse(jobj["quarterlyEarnings"][i]?["reportedEPS"]?.ToString(), out var quarterEPS))
        //                {
        //                    eps += quarterEPS;
        //                    foundQuarters++;
        //                }
        //                else
        //                {
        //                    _logger.LogError($"Error parsing json alphavantage earnings for {ticker.Symbol}");
        //                }
        //            }
        //            if(foundQuarters > 0)
        //            {
        //                eps += eps * (4 - foundQuarters) / foundQuarters; //will add 0 if all 4 quarters are found
        //                ticker.EPS = eps > 0 ? eps : 0.0;
        //                return true;
        //            }
        //        }
        //        else
        //        {
        //            _logger.LogError($"No alphavantage financial results for {ticker.Symbol}");
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"Error retrieving alphavantage earnings for {ticker.Symbol}");
        //    }
        //    return false;
        //}
        //private async Task<bool> LoadSimfinEarnings(Ticker ticker)
        //{
        //    var apiKey = _financialsApiKeyLookup[SIMFIN_API_KEY];
        //    try
        //    {
        //        var jobj = (await GetJson($"https://simfin.com/api/v2/companies/statements?api-key={apiKey}&ticker={ticker.Symbol.ToUpper()}&period=fy&fyear={DateTime.Now.Year-1}&statement=derived"))[0];
        //        if (jobj["found"].Value<bool>())
        //        {
        //            int? earningsColumn = null;
        //            var columns = jobj["columns"];
        //            for(var i = 0; i < columns.Count(); i++)
        //            {
        //                if(columns[i].Value<string>().Contains("Earnings Per Share"))
        //                {
        //                    earningsColumn = i;
        //                }
        //            }
        //            if(earningsColumn.HasValue)
        //            {
        //                if(double.TryParse(jobj["data"][0][earningsColumn.Value].ToString(), out var eps))
        //                {
        //                    ticker.EPS = eps > 0 ? eps : 0.0;
        //                    return true;
        //                }
        //            }
        //        }
        //        else
        //        {
        //            _logger.LogError($"No simfin financial earnings results for {ticker.Symbol}");
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"Error retrieving simfin earnings for {ticker.Symbol}");
        //    }
        //    return false;
        //}
        //private async Task<bool> LoadFinnHubEarnings(Ticker ticker)
        //{
        //    var apiKey = _financialsApiKeyLookup[FINNHUB_API_KEY];
        //    await _rateLimiter.LimitFinnhubRate();
        //    try
        //    {
        //        var jobj = await GetJson($"https://finnhub.io/api/v1/stock/financials-reported?symbol={ticker.Symbol.ToUpper()}&freq=annual&token={apiKey}");
        //        if (jobj["data"].Count() > 0)
        //        {
        //            foreach (JObject ic_item in jobj["data"][0]["report"]?["ic"])
        //            {
        //                if (ic_item["concept"].ToString().Contains("EarningsPerShareBasic") || ic_item["concept"].ToString().Contains("EarningsPerShareDiluted"))
        //                {
        //                    var eps = ic_item["value"].Value<double>();
        //                    ticker.EPS = eps > 0 ? eps : 0.0;
        //                    return true;
        //                }
        //            }
        //            _logger.LogInformation($"Could not parse finnhub financial earnings results for {ticker}");
        //        }
        //        else
        //        {
        //            _logger.LogError($"No finnhub financial earnings results for {ticker.Symbol}");
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"Error retrieving finnhub earnings for {ticker.Symbol}");
        //    }
        //    return false;
        //}
        //private async Task LoadFinnHubEBIT(Ticker ticker)
        //{
        //    var apiKey = _financialsApiKeyLookup[FINNHUB_API_KEY];
        //    await _rateLimiter.LimitFinnhubRate();
        //    try
        //    {
        //        var json = await GetJson($"https://finnhub.io/api/v1/stock/profile2?symbol={ticker.Symbol.ToUpper()}&token={apiKey}");
        //        ticker.EBIT = double.TryParse(((JValue)json.SelectTokens("$..ic[?(@.concept contains 'IncomeLossFromContinuingOperationsBeforeIncomeTaxesExtraordinaryItemsNoncontrollingInterest']").FirstOrDefault()["value"])?.Value?.ToString() ?? null, out var ebit) ? ebit : 0.0;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"Error retrieving finnhub market cap for {ticker.Symbol}");
        //    }
        //}
        //private async Task<Financials> LoadBrokerFinancials(Ticker ticker)
        //{
        //    var financials = await _brokerService.GetFinancials(ticker.Symbol);
        //    if(financials != null)
        //    {
        //        ticker.EPS = financials.EPS.HasValue && financials.EPS > 0 ? financials.EPS.Value : 0.0;
        //        ticker.EBIT = financials.EBIT.HasValue && financials.EBIT > 0 ? financials.EBIT.Value : 0.0;
        //        return financials;
        //    }
        //    return null;
        //}
        //private async Task LoadPolygonFinancials(Ticker ticker)
        //{
        //    var apiKey = _financialsApiKeyLookup[POLYGON_API_KEY];
        //    await _rateLimiter.LimitPolygonRate();
        //    var jobj = await GetJson($"https://api.polygon.io/vX/reference/financials?ticker={ticker.Symbol.ToUpper()}&timeframe=quarterly&apiKey={apiKey}");
        //    if (jobj["results"].Count() > 0)
        //    {
        //        var eps = (double?)jobj["results"][0]?["financials"]?["income_statement"]?["basic_earnings_per_share"]?["value"];
        //        eps = eps.HasValue ? eps : (double?)jobj["results"][0]?["financials"]?["income_statement"]?["diluted_earnings_per_share"]?["value"];
        //        ticker.EPS = eps.HasValue ? eps.Value * 4 : 0.0;
        //    }
        //    else
        //    {
        //        _logger.LogError($"No financial results for {ticker.Symbol}");
        //    }
        //}


        //private async Task LoadOld()
        //{
        //    await _brokerService.Ready();
        //    var now = DateTime.UtcNow;
        //    var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        //    var cutoff = new DateTimeOffset(now.AddDays(-10)).ToUnixTimeMilliseconds();
        //    var tickers = await _tickerProcessor.DownloadTickers(_runtimeSettings.ForceDataCollection);
        //    if(tickers.Count == 0) { return; }
        //    _logger.LogInformation($"Running updates on {tickers.Count} tickers");
        //    using (var stocksContext = _contextFactory.CreateDbContext())
        //    {
        //        var dbTickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
        //        foreach(var ticker in tickers)
        //        {
        //            var dbTicker = dbTickers.FirstOrDefault(t => t.Symbol == ticker.Symbol);
        //            var isValid = dbTicker != null && dbTicker.LastCalculatedMillis > cutoff;
        //            if (!isValid)
        //            {
        //                var info = await _brokerService.GetTickerInfo(ticker.Symbol);
        //                _logger.LogDebug($"Completed ticker info retrieval for {ticker.Symbol}");
        //                isValid = info != null && info.IsTradable;
        //            }
        //            if (isValid)
        //            {
        //                if (dbTicker == null)
        //                {
        //                    ticker.LastCalculated = now;
        //                    ticker.LastCalculatedMillis = nowMillis;
        //                    stocksContext.Tickers.Add(ticker);
        //                }
        //                else if(dbTicker.LastCalculatedMillis <= cutoff)
        //                {
        //                    dbTicker.LastCalculated = now;
        //                    dbTicker.LastCalculatedMillis = nowMillis;
        //                    stocksContext.Tickers.Update(dbTicker);
        //                }
        //            }
        //            else if (dbTicker != null)
        //            {
        //                stocksContext.Tickers.Remove(dbTicker);
        //            }
        //            await stocksContext.SaveChangesAsync(_appCancellation.Token);
        //        }
        //        var toRemove = dbTickers.Where(t1 => !tickers.Any(t2 => t1.Symbol == t2.Symbol)).ToArray();
        //        stocksContext.Tickers.RemoveRange(toRemove); //remove any tickers not found in file
        //        await stocksContext.SaveChangesAsync(_appCancellation.Token);
        //    }
        //}
    }
}
