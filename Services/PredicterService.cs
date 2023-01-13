using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        public const int FEATURE_HISTORY_DAY = 4;
        public const int LOOKAHEAD_DAYS = 1;
        public const int LOOKAHEAD_DAYS_LONG = 15;
        public const int FEATURE_HISTORY_INTRADAY = 10;
        private readonly ILogger<PredicterService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;
        private readonly double _encouragementMultiplier;
        private readonly double _peratioCutoff;

        public PredicterService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, TickerService tickerService, IStocksContextFactory contextFactory, IConfiguration configuration)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            _encouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration["EncouragementMultiplier"], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
            _peratioCutoff = tickerService.PERatioCutoff;
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
                        _logger.LogCritical($"Ticker {ticker.Symbol} not found. Manual intervention required");
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
                return Predict(ticker, barMetrics, buy);
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
                return Predict(ticker, barMetrics, buy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {barMetric.Symbol}");
            }
            return 0.0;
        }
        private double Predict(Ticker ticker, BarMetric[] barMetrics, bool buy)
        {
            if (barMetrics.Length == FEATURE_HISTORY_DAY)
            {
                double peRatio = ticker.EPS > 0 ? (barMetrics[0].HistoryBar.Price() / ticker.EPS) : _peratioCutoff;
                const double alpha = 0.5;
                const double defaultExp = 4;
                double pricePrediction; double longPricePrediction;
                if (buy)
                {
                    longPricePrediction = ((1 - barMetrics[0].SMASMA.DoubleReduce(Math.Max(ticker.SMASMAAvg, 0), Math.Min(ticker.SMASMAAvg - (ticker.SMASMAStDev * 1.5), 0))) * 0.25) +
                                          ((1 - barMetrics[0].AlmaSMA3.DoubleReduce(Math.Max(ticker.AlmaSma3Avg, 0), Math.Min(ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 1.5), 0))) * 0.15) +
                                          ((1 - barMetrics[0].AlmaSMA2.DoubleReduce(Math.Max(ticker.AlmaSma2Avg, 0), Math.Min(ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 1.5), 0))) * 0.15) +
                                          ((1 - barMetrics[0].AlmaSMA1.DoubleReduce(Math.Max(ticker.AlmaSma1Avg, 0), Math.Min(ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 1.5), 0))) * 0.15) +
                                          ((barMetrics.Average(b => b.AlmaSMA3) - barMetrics.Average(b => b.SMASMA)).DoubleReduce(ticker.AlmaSma3StDev, -ticker.AlmaSma3StDev) * 0.3);
                    longPricePrediction *= ticker.PerformanceVector.DoubleReduce(75, 0).Curve4(2);
                }
                else
                {
                    longPricePrediction = (barMetrics[0].SMASMA.DoubleReduce(Math.Min(ticker.SMASMAAvg + (ticker.SMASMAStDev * 1.5), 90), Math.Max(ticker.SMASMAAvg, 0)) * 0.25) +
                                          (barMetrics[0].AlmaSMA3.DoubleReduce(Math.Min(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 1.5), 90), Math.Max(ticker.AlmaSma3Avg, 0)) * 0.15) +
                                          (barMetrics[0].AlmaSMA2.DoubleReduce(Math.Min(ticker.AlmaSma2Avg + (ticker.AlmaSma2StDev * 1.5), 90), Math.Max(ticker.AlmaSma2Avg, 0)) * 0.15) +
                                          (barMetrics[0].AlmaSMA1.DoubleReduce(Math.Min(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 1.5), 90), Math.Max(ticker.AlmaSma1Avg, 0)) * 0.15) +
                                          ((barMetrics.Average(b => b.SMASMA) - barMetrics.Average(b => b.AlmaSMA3)).DoubleReduce(ticker.AlmaSma3StDev, -ticker.AlmaSma3StDev) * 0.3);
                    longPricePrediction *= 1 - ticker.PerformanceVector.DoubleReduce(200, 0);
                    longPricePrediction += (1 - longPricePrediction) * (1 - ticker.PerformanceVector.DoubleReduce(TickerService.PERFORMANCE_CUTOFF, -TickerService.PERFORMANCE_CUTOFF));
                }

                if (buy)
                {
                    var profitLossMin = ticker.ProfitLossAvg - (ticker.ProfitLossStDev * 1.5);
                    pricePrediction = ((1 - (barMetrics[0].PriceSMA3.DoubleReduce(30, -90))).Curve4(defaultExp) * 0.15) +
                                          ((1 - (barMetrics[0].PriceSMA2.DoubleReduce(60, -90))).Curve4(defaultExp) * 0.15) +
                                          ((1 - (barMetrics[0].PriceSMA1.DoubleReduce(90, -90))).Curve4(defaultExp) * 0.15) +
                    ((1 - (barMetrics[0].ProfitLossPerc.DoubleReduce(ticker.ProfitLossAvg, profitLossMin))).Curve4(defaultExp) * 0.25) +
                    ((barMetrics.Average(b => b.PriceSMA3) - barMetrics.Average(b => b.AlmaSMA3)).DoubleReduce(15, -15) * 0.1) +
                    ((barMetrics.Average(b => b.PriceSMA2) - barMetrics.Average(b => b.AlmaSMA2)).DoubleReduce(15, -15) * 0.1) +
                    ((barMetrics.Average(b => b.PriceSMA1) - barMetrics.Average(b => b.AlmaSMA1)).DoubleReduce(15, -15) * 0.1);
                    pricePrediction *= Math.Pow(barMetrics[0].ProfitLossPerc.DoubleReduce(profitLossMin, profitLossMin - 20), 2);
                }
                else
                {
                    var profitLossMax = ticker.ProfitLossAvg + (ticker.ProfitLossStDev * 1.5);
                    pricePrediction = (barMetrics[0].PriceSMA3.DoubleReduce(90, 0).Curve4(defaultExp) * 0.15) +
                                          (barMetrics[0].PriceSMA2.DoubleReduce(90, 15).Curve4(defaultExp) * 0.15) +
                                          (barMetrics[0].PriceSMA1.DoubleReduce(90, 30).Curve4(defaultExp) * 0.15) +
                    (barMetrics[0].ProfitLossPerc.DoubleReduce(profitLossMax, ticker.ProfitLossAvg).Curve4(defaultExp) * 0.25) +
                    ((barMetrics.Average(b => b.AlmaSMA3) - barMetrics.Average(b => b.PriceSMA3)).DoubleReduce(15, -15) * 0.1) +
                    ((barMetrics.Average(b => b.AlmaSMA2) - barMetrics.Average(b => b.PriceSMA2)).DoubleReduce(15, -15) * 0.1) +
                    ((barMetrics.Average(b => b.AlmaSMA1) - barMetrics.Average(b => b.PriceSMA1)).DoubleReduce(15, -15) * 0.1);
                }

                var totalPrediction = (pricePrediction * alpha) + (longPricePrediction * (1 - alpha));

                if (buy)
                {
                    totalPrediction *= barMetrics.CalculateVelocity(b => b.SMASMA).DoubleReduce(0, -ticker.AlmaSma3StDev);
                    totalPrediction *= (1 - peRatio.DoubleReduce(_peratioCutoff, _peratioCutoff * 0.5)) * _encouragementMultiplier.DoubleReduce(0, -1);
                    totalPrediction += (1 - totalPrediction) * _encouragementMultiplier.DoubleReduce(1, 0);
                }
                else
                {
                    totalPrediction *= 1 - barMetrics.CalculateVelocity(b => b.SMASMA).DoubleReduce(ticker.AlmaSma3StDev, 0);
                    totalPrediction *= 1 - _encouragementMultiplier.DoubleReduce(1, 0);
                    totalPrediction += (1 - totalPrediction) * (1 - _encouragementMultiplier.DoubleReduce(0, -1));
                }

                return totalPrediction;
            }
            return 0.0;
        }
    }
}
