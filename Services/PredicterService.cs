using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NumbersGoUp.JsonModels;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NumbersGoUp.Services
{
    public class PredicterService
    {
        public const int FEATURE_HISTORY_DAY = 5;
        public const int LOOKAHEAD_DAYS = 1;
        public const int LOOKAHEAD_DAYS_LONG = 15;
        public const int FEATURE_HISTORY_INTRADAY = 10;
        private readonly ILogger<PredicterService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;
        private readonly double _encouragementMultiplier;
        private readonly double _peratioCutoff;
        private readonly TickerService _tickerService;
        private Task _startTask;
        private static readonly SemaphoreSlim _taskSem = new SemaphoreSlim(1, 1);
        private double _buyCutoff = 100;

        public PredicterService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, TickerService tickerService, IStocksContextFactory contextFactory, IConfiguration configuration)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            _encouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration["EncouragementMultiplier"], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
            _peratioCutoff = tickerService.PERatioCutoff;
            _tickerService = tickerService;
        }
        private async Task Init()
        {
            var tickers = await _tickerService.GetTickers();
            _buyCutoff = tickers.Skip(4).Take(1).FirstOrDefault()?.PerformanceVector ?? _buyCutoff;
        }
        public async Task Ready()
        {
            if (_startTask == null)
            {
                await _taskSem.WaitAsync();
                try
                {
                    if (_startTask == null)
                    {
                        _startTask = Task.Run(Init);
                    }
                }
                finally
                {
                    _taskSem.Release();
                }
            }
            await _startTask;
        }
        public Task<double?> BuyPredict(string symbol) => Predict(symbol, true);
        public Task<double> BuyPredict(BarMetric barMetric) => Predict(barMetric, true);
        public Task<double?> SellPredict(string symbol) => Predict(symbol, false);
        public Task<double> SellPredict(BarMetric barMetric) => Predict(barMetric, false);
        private async Task<double?> Predict(string symbol, bool buy)
        {
            BarMetric[] barMetrics;
            Ticker ticker;
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    ticker = await stocksContext.Tickers.Where(t => t.Symbol == symbol).FirstOrDefaultAsync(_appCancellation.Token);
                    if (ticker == null)
                    {
                        _logger.LogCritical($"Ticker {symbol} not found. Manual intervention required");
                        return 0.0;
                    }
                    barMetrics = await stocksContext.BarMetrics.Where(p => p.Symbol == symbol).OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                if (barMetrics.Length != FEATURE_HISTORY_DAY)
                {
                    _logger.LogError($"BarMetrics for {symbol} did not return the required history (retrieved {barMetrics.Length} results). returning default prediction");
                    if (barMetrics.Length == 0)
                    {
                        _logger.LogError($"BarMetrics for {symbol} did not return any history. Assume ticker is no longer valid.");
                        return null;
                    }
                    return 0.0;
                }
                var lastMarketDay = await _brokerService.GetLastMarketDay();
                if (barMetrics.First().BarDay.CompareTo(lastMarketDay.Date.AddDays(-1)) < 0)
                {
                    _logger.LogError($"BarMetrics data for {symbol} isn't up to date! Returning default prediction.");
                    return 0.0;
                }
                return await Predict(ticker, barMetrics, buy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return 0.0;
        }
        private async Task<double> Predict(BarMetric barMetric, bool buy)
        {
            var symbol = barMetric.Symbol;
            BarMetric[] barMetrics;
            Ticker ticker;
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    ticker = await stocksContext.Tickers.Where(t => t.Symbol == symbol).FirstOrDefaultAsync(_appCancellation.Token);
                    barMetrics = await stocksContext.BarMetrics.Where(p => p.Symbol == barMetric.Symbol && p.BarDayMilliseconds <= barMetric.BarDayMilliseconds)
                                        .OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                return await Predict(ticker, barMetrics, buy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {barMetric.Symbol}");
            }
            return 0.0;
        }
        private async Task<double> Predict(Ticker ticker, BarMetric[] barMetrics, bool buy)
        {
            if (barMetrics.Length == FEATURE_HISTORY_DAY)
            {
                await Ready();
                double peRatio = ticker.EPS > 0 ? (barMetrics[0].HistoryBar.Price() / ticker.EPS) : _peratioCutoff;
                double pricePrediction;

                if (buy)
                {
                    var bullPricePrediction = (
                                            (barMetrics.CalculateAvgVelocity(b => b.AlmaSMA1).DoubleReduce(ticker.AlmaVelStDev * 0.75, -ticker.AlmaVelStDev) *
                                                (1 - barMetrics[0].AlmaSMA1.DoubleReduce(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 1.5), ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 1.5))).Curve4(2) * 0.25) +
                                            (barMetrics.CalculateAvgVelocity(b => b.AlmaSMA2).DoubleReduce(ticker.AlmaVelStDev * 0.75, -ticker.AlmaVelStDev) *
                                                (1 - barMetrics[0].AlmaSMA2.DoubleReduce(ticker.AlmaSma2Avg + ticker.AlmaSma2StDev, ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 1.5))).Curve4(2) * 0.25) +
                                            (barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(ticker.AlmaVelStDev * 0.75, -ticker.AlmaVelStDev) *
                                                (1 - barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg, ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 1.5))).Curve4(2) * 0.25) +
                                            ((1 - barMetrics[0].ProfitLossPerc.ZeroReduceFast(ticker.ProfitLossAvg + ticker.ProfitLossStDev, ticker.ProfitLossAvg - ticker.ProfitLossStDev)) * 0.25)
                                          ) * barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(0, -ticker.SMAVelStDev);

                    var bearPricePrediction = ((1 - barMetrics[0].AlmaSMA3.DoubleReduce(Math.Max(ticker.AlmaSma3Avg, 0), Math.Min(ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 1.5), 0))) * 0.2) +
                                          ((1 - barMetrics[0].AlmaSMA2.DoubleReduce(Math.Max(ticker.AlmaSma2Avg, 0), Math.Min(ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 1.5), 0))) * 0.2) +
                                          ((1 - barMetrics[0].AlmaSMA1.DoubleReduce(Math.Max(ticker.AlmaSma1Avg, 0), Math.Min(ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 1.5), 0))) * 0.2) +
                                          ((1 - barMetrics[0].ProfitLossPerc.DoubleReduce(ticker.ProfitLossAvg, ticker.ProfitLossAvg - (ticker.ProfitLossStDev * 1.5))).WExpCurve(4) * 0.4);

                    var shortPricePrediction = ((barMetrics.Average(b => b.AlmaSMA3) - barMetrics.Average(b => b.SMASMA)).DoubleReduce(ticker.AlmaSma3StDev, -ticker.AlmaSma3StDev) * 0.52) +
                                          ((barMetrics.Average(b => b.PriceSMA3) - barMetrics.Average(b => b.AlmaSMA3)).DoubleReduce(20, -20) * 0.12) +
                                          ((barMetrics.Average(b => b.PriceSMA2) - barMetrics.Average(b => b.AlmaSMA2)).DoubleReduce(20, -20) * 0.12) +
                                          ((barMetrics.Average(b => b.PriceSMA1) - barMetrics.Average(b => b.AlmaSMA1)).DoubleReduce(20, -20) * 0.12) +
                                          (barMetrics.CalculateAvgVelocity(b => b.PriceSMA1).DoubleReduce(15, 0) * 0.12);

                    var coeff = barMetrics.Average(b => b.SMASMA).DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - ticker.SMASMAStDev);
                    pricePrediction = (coeff * ((0.9 * bullPricePrediction) + (0.1 * shortPricePrediction))) + ((1 - coeff) * ((0.7 * bearPricePrediction) + (0.3 * shortPricePrediction)));
                    pricePrediction = pricePrediction.Curve1(3 - ticker.PerformanceVector.DoubleReduce(50, 0, 2, 1));
                    pricePrediction *= (1 - peRatio.DoubleReduce(_peratioCutoff, _peratioCutoff / 3));
                }
                else
                {
                    var bullPricePrediction = (
                                                ((1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA1).DoubleReduce(ticker.AlmaVelStDev, -0.75 * ticker.AlmaVelStDev)) *
                                                    barMetrics[0].AlmaSMA1.DoubleReduce(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 1.5), ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 1.5)).Curve4(2) * 0.25) +
                                                ((1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA2).DoubleReduce(ticker.AlmaVelStDev, -0.75 * ticker.AlmaVelStDev)) *
                                                    barMetrics[0].AlmaSMA2.DoubleReduce(ticker.AlmaSma2Avg + (ticker.AlmaSma2StDev * 1.5), ticker.AlmaSma2Avg - ticker.AlmaSma2StDev).Curve4(2) * 0.25) +
                                                ((1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(ticker.AlmaVelStDev, -0.75 * ticker.AlmaVelStDev)) *
                                                    barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 1.5), ticker.AlmaSma3Avg).Curve4(2) * 0.25) +
                                            (barMetrics[0].ProfitLossPerc.ZeroReduceFast(ticker.ProfitLossAvg + ticker.ProfitLossStDev, ticker.ProfitLossAvg - ticker.ProfitLossStDev) * 0.25)
                                              ) * (1 - barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(ticker.SMAVelStDev, 0));

                    var bearPricePrediction = (barMetrics[0].AlmaSMA3.DoubleReduce(Math.Min(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 1.5), 90), Math.Max(ticker.AlmaSma3Avg, 0)) * 0.2) +
                                          (barMetrics[0].AlmaSMA2.DoubleReduce(Math.Min(ticker.AlmaSma2Avg + (ticker.AlmaSma2StDev * 1.5), 90), Math.Max(ticker.AlmaSma2Avg, 0)) * 0.2) +
                                          (barMetrics[0].AlmaSMA1.DoubleReduce(Math.Min(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 1.5), 90), Math.Max(ticker.AlmaSma1Avg, 0)) * 0.2) +
                                          (barMetrics[0].ProfitLossPerc.DoubleReduce(ticker.ProfitLossAvg + (ticker.ProfitLossStDev * 1.5), ticker.ProfitLossAvg).WExpCurve(2) * 0.4);

                    var shortPricePrediction = ((barMetrics.Average(b => b.SMASMA) - barMetrics.Average(b => b.AlmaSMA3)).DoubleReduce(ticker.AlmaSma3StDev, -ticker.AlmaSma3StDev) * 0.52) +
                                          ((barMetrics.Average(b => b.AlmaSMA3) - barMetrics.Average(b => b.PriceSMA3)).DoubleReduce(20, -20) * 0.12) +
                                          ((barMetrics.Average(b => b.AlmaSMA2) - barMetrics.Average(b => b.PriceSMA2)).DoubleReduce(20, -20) * 0.12) +
                                          ((barMetrics.Average(b => b.AlmaSMA1) - barMetrics.Average(b => b.PriceSMA1)).DoubleReduce(20, -20) * 0.12) +
                                          ((1 - barMetrics.CalculateAvgVelocity(b => b.PriceSMA1).DoubleReduce(0, -15)) * 0.12);

                    var coeff = barMetrics.Average(b => b.SMASMA).DoubleReduce(ticker.SMASMAAvg + ticker.SMASMAStDev, ticker.SMASMAAvg - ticker.SMASMAStDev);
                    pricePrediction = (coeff * ((0.9 * bullPricePrediction) + (0.1 * shortPricePrediction))) + ((1 - coeff) * ((0.7 * bearPricePrediction) + (0.3 * shortPricePrediction)));
                    pricePrediction = pricePrediction.Curve1(ticker.PerformanceVector.DoubleReduce(100, 50, 2, 1));
                    pricePrediction += (1 - pricePrediction) * peRatio.DoubleReduce(_peratioCutoff * 2, _peratioCutoff);
                }

                if (buy)
                {
                    pricePrediction *= _encouragementMultiplier.DoubleReduce(0, -1);
                    pricePrediction += (1 - pricePrediction) * _encouragementMultiplier.DoubleReduce(1, 0);
                }
                else
                {
                    pricePrediction *= 1 - _encouragementMultiplier.DoubleReduce(1, 0);
                    pricePrediction += (1 - pricePrediction) * (1 - _encouragementMultiplier.DoubleReduce(0, -1));
                }

                return pricePrediction;
            }
            return 0.0;
        }

#if DEBUG
        private Dictionary<int, List<double>> valueLists = new Dictionary<int, List<double>>();
        private async Task DoAvgs(params double[] values)
        {
            await _taskSem.WaitAsync();
            try
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (!valueLists.ContainsKey(i))
                    {
                        valueLists.Add(i, new List<double>());
                    }
                    valueLists[i].Add(values[i]);
                }
                foreach (var kv in valueLists)
                {
                    if (kv.Value.Count > 0)
                    {
                        var avg = kv.Value.Average();
                        _logger.LogInformation($"avg{kv.Key}: {avg} stdev{kv.Key}: {Math.Sqrt(kv.Value.Sum(v => Math.Pow(v - 0, 2)) / kv.Value.Count)}");
                    }
                }
            }
            finally
            {
                _taskSem.Release();
            }
        }
#endif
    }
}