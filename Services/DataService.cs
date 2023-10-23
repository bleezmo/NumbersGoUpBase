using NumbersGoUp.Models;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NumbersGoUp.Utils;

namespace NumbersGoUp.Services
{
    public class DataService
    {

        private const int VOLEMA_LENGTH = 8;
        private const int SMA_LENGTH = 40;
        private const int SMA2_LENGTH = 80;
        private const int SMA3_LENGTH = 120;
        private const int RSI_LENGTH = 20;
        private const int ALMA_LENGTH = 10;
        private static readonly int _barLength = new int[] { VOLEMA_LENGTH, ALMA_LENGTH, SMA2_LENGTH, RSI_LENGTH, SMA_LENGTH, SMA3_LENGTH }.Max();
        private const long MILLIS_PER_HOUR = 60 * 60 * 1000;
        private const long MILLIS_PER_DAY = 24 * MILLIS_PER_HOUR;

        private readonly IBrokerService _brokerService;
        private readonly ILogger<DataService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly TickerService _tickerService;
        private readonly TickerBankService _tickerBankService;
        private readonly IStocksContextFactory _contextFactory;

        public int LookbackYears { get; }

        public DataService(IBrokerService brokerService, ILogger<DataService> logger, IAppCancellation appCancellation, IRuntimeSettings runtimeSettings,
                           TickerService tickerService, TickerBankService tickerBankService, IStocksContextFactory contextFactory)
        {
            _brokerService = brokerService;
            _logger = logger;
            _appCancellation = appCancellation;
            _tickerService = tickerService;
            _tickerBankService = tickerBankService;
            _contextFactory = contextFactory;
            LookbackYears = runtimeSettings.LookbackYears;
        }
        public async Task Run()
        {
            _logger.LogInformation($"{nameof(DataService)} awaiting broker service");
            await _brokerService.Ready();
            _logger.LogInformation("Running data collection");
            try
            {
                await _tickerService.LoadTickers();
                _logger.LogInformation("Ticker collection complete");
                var tickers = await _tickerService.GetFullTickerList();
                await StartCollection(tickers);
                _logger.LogInformation("Done collecting bar history");
                await GenerateMetrics(tickers);
                _logger.LogInformation($"Completed metrics generation");
                await CleanUp();
                _logger.LogInformation($"Completed clean up");
                await _tickerService.ApplyAverages();
                _logger.LogInformation($"Completed calculation of averages");
                await _tickerService.CalculatePerformance();
                _logger.LogInformation($"Completed performance calculation");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error during data processing");
                throw;
            }
            finally
            {
                _logger.LogInformation("Data retrieval and processing complete");
            }
        }
        public async Task CleanUp()
        {
#if DEBUG
            return;
#endif
            var now = DateTime.Now;
            if (now.DayOfWeek == DayOfWeek.Friday) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var cutoff = DateTimeOffset.Now.AddYears(-LookbackYears-1).ToUnixTimeMilliseconds();
                    var bars = await stocksContext.HistoryBars.Where(b => b.BarDayMilliseconds < cutoff).ToListAsync(_appCancellation.Token);
                    stocksContext.HistoryBars.RemoveRange(bars);
                    var removed = await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    _logger.LogInformation($"Removed {removed} old records from history bars");
                }
            }
        }
        public async Task ValidateData()
        {
            var now = DateTime.Now;
            var days = new List<MarketDay>();
            for (var i = now.Year - LookbackYears; i < now.Year; i++)
            {
                _logger.LogInformation($"Retrieving all calendar days for year {i}");
                days.AddRange(await _brokerService.GetMarketDays(i));
            }
            _logger.LogInformation($"Retrieving all calendar days for year {now.Year}");
            for (var i = 1; i < now.Month; i++)
            {
                days.AddRange(await _brokerService.GetMarketDays(now.Year, i));
            }
            var currentMonthDays = await _brokerService.GetMarketDays(now.Year, now.Month);
            days.AddRange(currentMonthDays.Where(d => d.Date.CompareTo(now) < 0));
            foreach (var ticker in await _tickerService.GetTickers())
            {
                _logger.LogInformation($"Checking data for ticker {ticker.Symbol}");
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var misses = 0;
                    var bars = await stocksContext.HistoryBars.Where(b => b.Symbol == ticker.Symbol).ToListAsync();
                    foreach (var bar in bars)
                    {
                        if (!days.Any(d => d.Date.CompareTo(bar.BarDay.Date) == 0))
                        {
                            misses++;
                        }
                    }
                    if (misses > 0)
                    {
                        _logger.LogWarning($"Missing {misses} days of bar data for {ticker.Symbol}");
                    }
                    else
                    {
                        _logger.LogInformation($"100% bar data for {ticker.Symbol}");
                    }
                }
            }
        }
        public async Task<BarMetric> GetLastMetric(string symbol)
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                return await stocksContext.BarMetrics.Where(b => b.Symbol == symbol).OrderByDescending(m => m.BarDayMilliseconds).Take(1).Include(m => m.HistoryBar).ThenInclude(h => h.Ticker).FirstOrDefaultAsync(_appCancellation.Token);
            }
        }
        private async Task StartCollection(IEnumerable<Ticker> tickers)
        {
            var length = tickers.Count();
            var batchSize = 10;
            var totalCount = 0;
            for (var i = 0; i < length; i += batchSize)
            {
                var batch = tickers.Skip(i).Take(batchSize);
                var tasks = new List<Task>();
                foreach (var ticker in batch)
                {
                    var tickerLocal = ticker;
                    tasks.Add(Task.Run(async () =>
                    {
                        if (_appCancellation.IsCancellationRequested) return;
                        try
                        {
                            _logger.LogDebug($"StartCollection for {tickerLocal.Symbol}");
                            await StartDayCollection(tickerLocal);
                            _logger.LogDebug($"Completed StartCollection for {tickerLocal.Symbol}");
                        }
                        catch (TaskCanceledException) { }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Error occurred collecting history for {tickerLocal.Symbol}");
                        }
                    }, _appCancellation.Token));
                    if (totalCount == 0) { await Task.Delay(1000); }
                    totalCount++;
                }
                await Task.WhenAll(tasks);
            }
            _logger.LogInformation($"Collected history for {totalCount} tickers");
        }
        private async Task StartDayCollection(Ticker ticker, DateTime? from = null)
        {
            var stockSplit = false;
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var symbol = ticker.Symbol;
                var lastBar = await stocksContext.HistoryBars.Where(t => t.Symbol == symbol).OrderByDescending(t => t.BarDayMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                if (!from.HasValue)
                {
                    if (lastBar != null)
                    {
                        var lastDay = await _brokerService.GetLastMarketDay();
                        if (lastBar.BarDay.Date.CompareTo(lastDay.Date) < 0)
                        {
                            from = lastBar.BarDay;
                        }
                    }
                    else
                    {
                        from = DateTime.Now.AddYears(0 - LookbackYears);
                    }
                }
                if (from.HasValue)
                {
                    _logger.LogDebug($"Collecting bar history for {symbol}");
                    var count = 0;
                    foreach (var bar in await _brokerService.GetBarHistoryDay(symbol, from.Value))
                    {
                        //just make sure
                        if (lastBar == null || lastBar.BarDay.CompareTo(bar.BarDay) < 0)
                        {
                            if (count == 0 && lastBar != null)
                            {
                                var diff = (bar.Price() - lastBar.Price()) / lastBar.Price();
                                if (Math.Abs(diff) > 0.4) //possible stock split detected
                                {
                                    _logger.LogWarning($"Stock split detected for {ticker.Symbol}");
                                    stockSplit = true;
                                    break;
                                }
                            }
                            else
                            {
                                bar.TickerId = ticker.Id;
                                stocksContext.HistoryBars.Add(bar);
                                count++;
                            }
                        }
                    }
                    if (stockSplit)
                    {
                        var remove = await stocksContext.HistoryBars.Where(t => t.Symbol == symbol).ToArrayAsync(_appCancellation.Token);
                        stocksContext.HistoryBars.RemoveRange(remove);
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
                _logger.LogDebug($"Completed bar history collection for {symbol}");
            }
            if (stockSplit)
            {
                await StartDayCollection(ticker, DateTime.Now.AddYears(0 - LookbackYears));
            }
        }

        private async Task GenerateMetrics(IEnumerable<Ticker> tickers)
        {
            var length = tickers.Count();
            var batchSize = 10;
            var totalCount = 0;
            for (var i = 0; i < length; i += batchSize)
            {
                var batch = tickers.Skip(i).Take(batchSize);
                var tasks = new List<Task>();
                foreach (var ticker in batch)
                {
                    var tickerLocal = ticker;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (_appCancellation.IsCancellationRequested) return;
                            _logger.LogDebug($"Generating day Metrics for {tickerLocal.Symbol}");
                            await GenerateDayMetrics(tickerLocal);
                            _logger.LogDebug($"Finished generating day Metrics for {tickerLocal.Symbol}");
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Error occurred generating metrics for {tickerLocal.Symbol}");
                        }
                    }, _appCancellation.Token));
                    if (totalCount == 0) { await Task.Delay(1000); }
                    totalCount++;
                }
                await Task.WhenAll(tasks);
            }
            _logger.LogInformation($"Generated metrics for {totalCount} tickers");
        }

        private async Task GenerateDayMetrics(Ticker ticker)
        {
            using var stocksContext = _contextFactory.CreateDbContext();
            var symbol = ticker.Symbol;
            var lookback = DateTime.Now.AddYears(0 - LookbackYears);
            var cutoff = new DateTimeOffset(lookback.Year, lookback.Month, lookback.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var currentBarMetric = await stocksContext.BarMetrics.Where(t => t.Symbol == symbol).OrderByDescending(t => t.BarDayMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
            cutoff = currentBarMetric != null ? currentBarMetric.BarDayMilliseconds - (_barLength * 2 * MILLIS_PER_DAY) : cutoff; //give buffer to cutoff
            var barsAll = await stocksContext.HistoryBars.Where(t => t.Symbol == symbol && t.BarDayMilliseconds > cutoff).Include(b => b.BarMetric).OrderBy(t => t.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
            for (var skip = 0; skip + _barLength <= barsAll.Length; skip++)
            {
                var bars = barsAll.Skip(skip).Take(_barLength).Reverse().ToArray();
                if (bars[0].BarMetric != null)
                {
                    continue;
                }
                var barMetric = new BarMetric
                {
                    Symbol = bars[0].Symbol,
                    BarDay = bars[0].BarDay,
                    BarDayMilliseconds = bars[0].BarDayMilliseconds,
                    HistoryBarId = bars[0].Id,
                };
                var (sma, smaUpper, smaLower) = BollingerBands(bars.Take(SMA_LENGTH).ToArray(), DefaultBarFn);
                var (sma2, sma2Upper, sma2Lower) = BollingerBands(bars.Take(SMA2_LENGTH).ToArray(), DefaultBarFn);
                var (sma3, sma3Upper, sma3Lower) = BollingerBands(bars.Take(SMA3_LENGTH).ToArray(), DefaultBarFn);
                var almaBars = bars.Take(ALMA_LENGTH).ToArray();
                var alma = almaBars.ApplyAlma(DefaultBarFn);
                barMetric.AlmaSMA1 = GetPerc(alma - sma, smaUpper - sma);
                barMetric.AlmaSMA2 = GetPerc(alma - sma2, sma2Upper - sma2);
                barMetric.AlmaSMA3 = GetPerc(alma - sma3, sma3Upper - sma3);
                barMetric.PriceSMA1 = GetPerc(bars[0].Price() - sma, smaUpper - sma);
                barMetric.PriceSMA2 = GetPerc(bars[0].Price() - sma2, sma2Upper - sma2);
                barMetric.PriceSMA3 = GetPerc(bars[0].Price() - sma3, sma3Upper - sma3);
                barMetric.SMASMA = GetPerc(sma - sma3, sma3Upper - sma3);
                barMetric.ProfitLossPerc = (bars.First().Price() - bars.Last().Price()) * 100 / bars.Last().Price();
                barMetric.WeekTrend = GetWeekTrend(bars.Take(SMA2_LENGTH).Reverse().ToArray());
                var volAlma = almaBars.ApplyAlma((bar) => Convert.ToDouble(bar.Volume));
                var (volSma, volSmaUpper, volSmaLower) = BollingerBands(bars.Take(SMA_LENGTH).ToArray(), (bar) => Convert.ToDouble(bar.Volume));
                barMetric.VolAlmaSMA = GetPerc(volAlma - volSma, volSmaUpper - volSma);
                stocksContext.BarMetrics.Add(barMetric);
            }
            await stocksContext.SaveChangesAsync(_appCancellation.Token);
        }
        private double GetWeekTrend(HistoryBar[] barsAsc)
        {
            var size = (int)Math.Floor(Convert.ToDouble(barsAsc.Length) / 10);

            var initialPrice = barsAsc[0].Price();
            var currentMin = 0.0;
            var slopes = new List<double>();
            for (var i = 0; i < barsAsc.Length; i += size)
            {
                var min = (barsAsc.Skip(i).Take(size).Min(b => b.LowPrice) - initialPrice) * 100 / initialPrice;
                slopes.Add(min - currentMin);
                currentMin = min;
            }
            return slopes.Reverse<double>().ToArray().ApplyAlma();
        }

        private static double DefaultBarFn(HistoryBar bar) => bar.Price();
        private static (double sma, double smaUpper, double smaLower) BollingerBands(HistoryBar[] bars, Func<HistoryBar, double> barFn)
        {
            var sma = bars.Aggregate(0.0, (acc, bar) => acc + barFn(bar)) / bars.Length;
            var stdev = Math.Sqrt(bars.Aggregate(0.0, (acc, bar) => acc + Math.Pow(barFn(bar) - sma, 2)) / bars.Length);
            var smaUpper = sma + (stdev * 2.5);
            var smaLower = sma - (stdev * 2.5);
            return (sma, smaUpper, smaLower);
        }
        private static double GetPerc(double num, double denom)
        {
            if (denom == 0) { return 0.0; }
            var perc = num / denom;
            return perc * 100;
        }
#if DEBUG
        public async Task GenerateMetricsExternal(bool collectHistory = true)
        {
            var tickers = await _tickerService.GetFullTickerList();
            if (collectHistory)
            {
                await StartCollection(tickers);
            }
            await GenerateMetrics(tickers);
        }
#endif
    }
}
