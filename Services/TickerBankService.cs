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
        private const string PERATIO_CUTOFF_KEY = "PERatioCutoff";
        private const string FINANCIAL_API_KEY = "fmp_api_key";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerBankService> _logger;
        private readonly RateLimiter _rateLimiter;
        private readonly ITickerBankProcessor _tickerProcessor;
        private readonly IConfiguration _configuration;
        private readonly string _environmentName;
        private readonly IStocksContextFactory _contextFactory;
        private readonly IRuntimeSettings _runtimeSettings;
        private readonly string _apiKey;
        private readonly DateTime _lookbackDate = DateTime.Now.AddYears(-DataService.LOOKBACK_YEARS);
        public double PERatioCutoff { get; }

        private const string BaseURL = "https://financialmodelingprep.com";
        private string QuotePath => $"{BaseURL}/api/v3/quote/{{0}}?apikey={_apiKey}";
        private string QuarterlyIncomePath => $"{BaseURL}/api/v3/income-statement/{{0}}?period=quarter&limit=4&apikey={_apiKey}";
        private string FYIncomePath => $"{BaseURL}/api/v3/income-statement/{{0}}?&limit=1&apikey={_apiKey}";
        private string QuarterlyBalancePath => $"{BaseURL}/api/v3/balance-sheet-statement/{{0}}?period=quarter&limit=4&apikey={_apiKey}";
        private string QuarterlyCashFlowPath => $"{BaseURL}/api/v3/cash-flow-statement/{{0}}?period=quarter&limit=4&apikey={_apiKey}";
        private string HistoricalPricesPath => $"{BaseURL}/api/v3/historical-price-full/{{0}}?from={_lookbackDate:yyyy-MM-dd}&apikey={_apiKey}";
        private string KeyMetricsPath => $"{BaseURL}/api/v3/key-metrics-ttm/{{0}}?limit=1&apikey={_apiKey}";


        public TickerBankService(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory httpClientFactory, IStocksContextFactory contextFactory, IRuntimeSettings runtimeSettings,
                                IAppCancellation appCancellation, ILogger<TickerBankService> logger, RateLimiter rateLimiter, ITickerBankProcessor tickerProcessor)
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
            _apiKey = _configuration[$"{FINANCIAL_API_KEY}:{_environmentName}"];
            PERatioCutoff = double.TryParse(configuration[PERATIO_CUTOFF_KEY], out var peratioCutoff) ? peratioCutoff : 30;
        }
        public async Task Load()
        {
            try
            {
                var tickers = await _tickerProcessor.DownloadTickers(_runtimeSettings.ForceDataCollection);
                if (tickers.Count == 0) { return; }
                _logger.LogInformation($"Loading {tickers.Count} tickers into ticker bank");
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                    //foreach(var ticker in tickers)
                    //{
                    //    var dbTicker = dbTickers.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                    //    if (dbTicker != null)
                    //    {
                    //        dbTicker.Sector = ticker.Sector;
                    //        dbTicker.MarketCap = ticker.MarketCap;
                    //        stocksContext.TickerBank.Update(dbTicker);
                    //    }
                    //}
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    var toRemove = dbTickers.Where(t1 => !tickers.Any(t2 => t1.Symbol == t2.Symbol)).ToArray();
                    stocksContext.TickerBank.RemoveRange(toRemove);
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    stocksContext.TickerBank.AddRange(tickers.Where(t => !dbTickers.Any(dbt => dbt.Symbol == t.Symbol)).ToArray());
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error occurred when loading ticker bank data");
            }
        }
        public async Task CalculatePerformance()
        {
            var now = DateTime.UtcNow;
            var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            var cutoff = new DateTimeOffset(now.AddDays(-15)).ToUnixTimeMilliseconds();
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var isUpdate = _runtimeSettings.ForceDataCollection || await stocksContext.TickerBank.Where(t => t.LastCalculatedPerformanceMillis == null || t.LastCalculatedPerformanceMillis < cutoff).AnyAsync(_appCancellation.Token);
                if (isUpdate)
                {
                    var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                    var tickers = new List<BankTicker>();
                    foreach(var t in dbTickers)
                    {
                        if(t.Earnings > 0 && t.PERatio < PERatioCutoff && t.EVEBITDA < PERatioCutoff && t.PERatio > t.EVEBITDA && t.PERatio > 1 && t.EVEBITDA > 1 && 
                            t.DebtEquityRatio < 2 && t.DebtEquityRatio > 0 && t.CurrentRatio > 1.2 && t.CurrentRatio > (t.DebtEquityRatio*0.75) && t.DividendYield > 0.005 && t.PriceChangeAvg > 0 && t.EPS > 1)
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
                    Func<BankTicker, double> performanceFn1 = (t) => Math.Sqrt(t.Earnings) * 2;
                    Func<BankTicker, double> performanceFn2 = (t) => t.PriceChangeAvg;
                    Func<BankTicker, double> performanceFn3 = (t) => Math.Min(t.DividendYield, 0.06);
                    //slope here is based on a graph where x-axis is MedianMonthPercVariance and y-axis is MedianMonthPerc
                    var minmax1 = new MinMaxStore<BankTicker>(performanceFn1);
                    var minmax2 = new MinMaxStore<BankTicker>(performanceFn2);
                    var minmax3 = new MinMaxStore<BankTicker>(performanceFn3);
                    foreach (var ticker in tickers)
                    {
                        minmax1.Run(ticker);
                        minmax2.Run(ticker);
                        minmax3.Run(ticker);
                    }
                    Func<BankTicker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 60) +
                                                                         (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 30) +
                                                                         (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 10);
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
        }
        public async Task UpdateFinancials(int limit = 1000)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                var cutoff = new DateTimeOffset(now.AddDays(-30)).ToUnixTimeMilliseconds();
                BankTicker[] tickersToUpdate;
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var query = _runtimeSettings.ForceDataCollection ? stocksContext.TickerBank : stocksContext.TickerBank.Where(t => t.LastCalculatedFinancialsMillis == null || t.LastCalculatedFinancialsMillis < cutoff);
                    tickersToUpdate = await query.OrderBy(t => t.LastCalculatedFinancialsMillis).Take(limit).ToArrayAsync(_appCancellation.Token);
                }
                foreach (var ticker in tickersToUpdate)
                {
                    if (_appCancellation.IsCancellationRequested)
                    {
                        break;
                    }
                    var retry = await PopulateFinancials(ticker);
                    if (retry && !_appCancellation.IsCancellationRequested)
                    {
                        _logger.LogWarning($"Financials calculation failed for {ticker.Symbol}. Retrying one last time");
                        await PopulateFinancials(ticker);
                    }
                    ticker.LastCalculatedFinancials = now;
                    ticker.LastCalculatedFinancialsMillis = nowMillis;
                }
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    for (var i = 0; i < tickersToUpdate.Length; i += 20)
                    {
                        stocksContext.TickerBank.UpdateRange(tickersToUpdate.Skip(i).Take(20));
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ticker bank financials update failed");
            }
        }
        private async Task<bool> PopulateFinancials(BankTicker ticker)
        {
            try
            {
                #region Quote section
                var quoteSuccess = false;
                var quote = (await GetResponse<IEnumerable<FMPQuote>>(string.Format(QuotePath, ticker.Symbol)))?.FirstOrDefault();
                if (quote != null)
                {
                    ticker.EPS = quote.EPS;
                    ticker.PERatio = quote.EPS > 0 ? quote.Price / quote.EPS : 1000;
                    ticker.Earnings = quote.EPS * quote.SharesOutstanding;
                    ticker.MarketCap = quote.MarketCap > 0 ? quote.MarketCap : ticker.MarketCap;
                }
                else
                {
                    _logger.LogWarning($"Quote not found for {ticker.Symbol}. Assume unavailable in FMP");
                    return false;
                }

                if (quote.SharesOutstanding > 0 && quote.Price > 0)
                {
                    quoteSuccess = true;
                }
                else
                {
                    _logger.LogWarning($"Shares outstanding or price was zero for {ticker.Symbol}.");
                }
                #endregion
                #region Price section
                var prices = (await GetResponse<FMPHistorical>(string.Format(HistoricalPricesPath, ticker.Symbol)))?.Prices?.ToArray();
                if (prices != null && prices.Length > 0)
                {
                    var now = DateTime.Now;
                    var datePointer = _lookbackDate;
                    var priceChanges = new List<double>();
                    for (var i = 0; now.CompareTo(datePointer) > 0 && i < 1000; i++)
                    {
                        var from = datePointer;
                        var to = datePointer.AddMonths(6);
                        var priceWindow = prices.Where(p => p.Date.HasValue && p.Date.Value.CompareTo(from) > 0 && p.Date.Value.CompareTo(to) < 0).OrderByDescending(p => p.Date).ToArray();
                        if (priceWindow.Length > 2)
                        {
                            priceChanges.Add((priceWindow.First().Price - priceWindow.Last().Price) * 100 / priceWindow.Last().Price);
                        }
                        datePointer = to;
                    }
                    if (priceChanges.Count > ((DataService.LOOKBACK_YEARS - 1) * 2))
                    {
                        var avg = priceChanges.Average();
                        var stdev = Math.Sqrt(priceChanges.Sum(p => Math.Pow(p - avg, 2)) / priceChanges.Count);
                        var mode = priceChanges.OrderBy(p => p).Skip(priceChanges.Count / 2).FirstOrDefault();
                        ticker.PriceChangeAvg = avg / stdev;
                    }
                    else
                    {
                        _logger.LogDebug($"Insufficient price information for {ticker.Symbol}");
                        ticker.PriceChangeAvg = 0;
                        return false;
                    }
                }
                else
                {
                    _logger.LogError($"Error retrieving FMP Price Change data for {ticker.Symbol}");
                    return false;
                }
                #endregion
                #region Dividend section
                var keyMetric = (await GetResponse<IEnumerable<KeyMetric>>(string.Format(KeyMetricsPath, ticker.Symbol)))?.FirstOrDefault();
                if (keyMetric != null)
                {
                    ticker.DividendYield = keyMetric.DividentYield;
                }
                else
                {
                    if (quote.SharesOutstanding > 0 && quote.Price > 0)
                    {
                        var cashFlow = (await GetResponse<IEnumerable<FMPCashFlowQuarter>>(string.Format(QuarterlyCashFlowPath, ticker.Symbol)))?.ToArray();
                        if (cashFlow != null && cashFlow.Length > 3)
                        {
                            var dividendsPaid = Math.Abs(cashFlow.Sum(c => c.DividendsPaid));
                            var dividendsPerShare = dividendsPaid / quote.SharesOutstanding;
                            ticker.DividendYield = dividendsPerShare / quote.Price;
                        }
                        else
                        {
                            _logger.LogError($"Error calculating dividend yield for {ticker.Symbol}");
                            return false;
                        }
                    }
                    else
                    {
                        _logger.LogError($"Unable to compute dividend yield for {ticker.Symbol}.");
                    }
                }
                #endregion
                #region Income section
                var incomeQuarters = (await GetResponse<IEnumerable<FMPIncomeQuarter>>(string.Format(QuarterlyIncomePath, ticker.Symbol)))?.ToArray();
                var ebitda = 0.0;
                if (incomeQuarters != null && incomeQuarters.Length > 3)
                {
                    ticker.EPS = quote.EPS > 0 ? quote.EPS : incomeQuarters.Take(4).Sum(i => i.EPS);
                    ebitda = incomeQuarters.Take(4).Sum(i => i.EBITDA);
                    ticker.Earnings = ticker.Earnings > 0 ? ticker.Earnings : (quote.SharesOutstanding > 0 ? ticker.EPS * quote.SharesOutstanding : ebitda);
                }
                else
                {
                    _logger.LogWarning($"Error retrieving quarterly income for {ticker.Symbol}. Trying FY");
                    var income = (await GetResponse<IEnumerable<FMPIncomeQuarter>>(string.Format(FYIncomePath, ticker.Symbol)))?.FirstOrDefault();
                    if (income != null)
                    {
                        ticker.EPS = quote.EPS > 0 ? quote.EPS : income.EPS;
                        ebitda = income.EBITDA;
                        var earnings = quote.SharesOutstanding > 0 ? ticker.EPS * quote.SharesOutstanding : ebitda;
                        ticker.Earnings = earnings > 0 ? earnings : ticker.Earnings;
                    }
                    else
                    {
                        _logger.LogError($"Error retrieving quarterly income for {ticker.Symbol}");
                    }
                }
                #endregion
                #region Balance section
                var balances = (await GetResponse<IEnumerable<FMPBalanceQuarter>>(string.Format(QuarterlyBalancePath, ticker.Symbol)))?.ToArray();
                var currentSuccess = false;
                var debtEquitySuccess = false;
                if (balances != null && balances.Length > 0)
                {
                    foreach (var balance in balances)
                    {
                        if (balance.TotalAssets > 0 && balance.TotalLiabilities > 0)
                        {
                            ticker.CurrentRatio = balance.TotalAssets / balance.TotalLiabilities;
                            currentSuccess = true;
                            break;
                        }
                    }
                    if (!currentSuccess)
                    {
                        _logger.LogError($"Unable to compute current ratio for {ticker.Symbol}");
                    }
                    foreach (var balance in balances)
                    {
                        if (balance.TotalEquity != 0 && balance.TotalDebt != 0)
                        {
                            ticker.DebtEquityRatio = balance.TotalEquity < 0 ? 1000 : (balance.TotalDebt / balance.TotalEquity);
                            debtEquitySuccess = true;
                            break;
                        }
                    }
                    if (!debtEquitySuccess)
                    {
                        _logger.LogError($"Unable to compute debt-equity ratio for {ticker.Symbol}");
                    }
                    if(ebitda != 0)
                    {
                        foreach (var balance in balances)
                        {
                            if (ticker.MarketCap > 0 && balance.TotalDebt > 0 && balance.CashAndCashEquivalents > 0)
                            {
                                var ev = ticker.MarketCap + balance.TotalDebt - balance.CashAndCashEquivalents;
                                ticker.EVEBITDA = ebitda < 0 ? 1000 : (ev / ebitda);
                                break;
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Unable to derive EVEBITDA for {ticker.Symbol}. Using key metric.");
                        ticker.EVEBITDA = keyMetric.EVEBITDA;
                    }
                }
                else
                {
                    _logger.LogError($"Error retrieving FMP Balance data for {ticker.Symbol}");
                }
                #endregion
                return !quoteSuccess;
            }
            catch(Exception e)
            {
                _logger.LogError(e, $"Error loading financials for {ticker.Symbol}");
                return true;
            }
        }
        private async Task<T> GetResponse<T>(string path)
        {
            await _rateLimiter.LimitFMPRate();
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(path, _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            return JsonConvert.DeserializeObject<T>(json);
        }
    }
}
