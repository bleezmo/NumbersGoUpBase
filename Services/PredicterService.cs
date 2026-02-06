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
        public const int FEATURE_HISTORY_DAY = 7;
        private readonly ILogger<PredicterService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;

        public double EncouragementMultiplier { get; }

        public PredicterService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, 
                                IStocksContextFactory contextFactory, IConfiguration configuration)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            EncouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration["EncouragementMultiplier"], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
        }
        public async Task<Prediction> Predict(Ticker ticker)
        {
            var symbol = ticker.Symbol;
            BarMetric[] barMetrics;
            try
            {
                if (!IsValidTicker(ticker))
                {
                    if (DateTime.Now.DayOfWeek != TickerService.RUN_LOAD)
                    {
                        _logger.LogWarning($"Ticker {ticker.Symbol} has invalid metric averages");
                    }
                    return null;
                }
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    barMetrics = await stocksContext.BarMetrics.Where(p => p.Symbol == symbol).OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY)
                                                     .Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                if (barMetrics.Length != FEATURE_HISTORY_DAY)
                {
                    _logger.LogError($"BarMetrics for {symbol} did not return the required history (retrieved {barMetrics.Length} results). returning default prediction");
                    if (barMetrics.Length == 0)
                    {
                        _logger.LogError($"BarMetrics for {symbol} did not return any history. Assume ticker is no longer valid.");
                        return null;
                    }
                    return null;
                }
                DateTime checkDay = (await _brokerService.GetLastMarketDay()).Date.AddDays(-1);
                if (barMetrics[0].BarDay.CompareTo(checkDay) < 0)
                {
                    _logger.LogError($"BarMetrics data for {symbol} isn't up to date! Returning default prediction.");
                    return null;
                }
                return new Prediction
                {
                    BuyMultiplier = Math.Round(Predict(ticker, barMetrics, true), 3, MidpointRounding.AwayFromZero),
                    SellMultiplier = Math.Round(Predict(ticker, barMetrics, false), 3, MidpointRounding.AwayFromZero),
                    RecentBarMetric = barMetrics[0]
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return null;
        }
        public double Predict(Ticker ticker, BarMetric[] barMetrics, bool buy)
        {
            if (barMetrics.Length == FEATURE_HISTORY_DAY)
            {
                double pricePrediction;

                if (buy)
                {
                    pricePrediction = barMetrics[0].SMASMA.ZeroReduce(ticker.SMASMAStDev, -ticker.SMASMAStDev) *
                                        (1 - barMetrics[0].SMA2SMA.DoubleReduce(0, -ticker.SMA2SMAStDev)) *
                                        (1 - barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg, ticker.AlmaSma3Avg - ticker.AlmaSma3StDev));
                }
                else
                {
                    pricePrediction = barMetrics[0].SMASMA.ZeroReduce(ticker.SMASMAStDev, -ticker.SMASMAStDev) *
                                     barMetrics[0].SMA2SMA.DoubleReduce(ticker.SMA2SMAStDev, 0) *
                                     barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg + ticker.AlmaSma3StDev, ticker.AlmaSma3Avg) *
                                     (1 - barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 3), ticker.AlmaSma3Avg + ticker.AlmaSma3StDev));

                }

                if (buy)
                {
                    pricePrediction *= EncouragementMultiplier.DoubleReduce(0, -1);
                    pricePrediction += (1 - pricePrediction) * EncouragementMultiplier.DoubleReduce(1, 0);
                }
                else
                {
                    pricePrediction *= 1 - EncouragementMultiplier.DoubleReduce(1, 0);
                    pricePrediction += (1 - pricePrediction) * (1 - EncouragementMultiplier.DoubleReduce(0, -1));
                }

                return pricePrediction;
            }
            return 0.0;
        }
        private bool IsValidTicker(Ticker ticker) =>
            ticker.AlmaSma1StDev > 0 && ticker.AlmaSma2StDev > 0 && ticker.AlmaSma3StDev > 0 && ticker.AlmaVelStDev > 0 &&
            ticker.SMASMAStDev > 0 && ticker.SMAVelStDev > 0;

    }
    public class Prediction
    {
        public double SellMultiplier { get; set; }
        public double BuyMultiplier { get; set; }
        public BarMetric RecentBarMetric { get; set; }
    }
}