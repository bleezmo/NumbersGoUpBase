﻿using Microsoft.EntityFrameworkCore;
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
        private readonly ILogger<PredicterService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;
        private readonly double _peratioCutoff;
        private readonly TickerService _tickerService;

        public double EncouragementMultiplier { get; }

        public PredicterService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, TickerService tickerService, 
                                IStocksContextFactory contextFactory, IConfiguration configuration)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            EncouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration["EncouragementMultiplier"], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
            _peratioCutoff = tickerService.PERatioCutoff;
            _tickerService = tickerService;
        }
        public async Task<Prediction> Predict(string symbol)
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
                        return null;
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
                    return null;
                }
                var lastMarketDay = await _brokerService.GetLastMarketDay();
                if (barMetrics[0].BarDay.CompareTo(lastMarketDay.Date.AddDays(-1)) < 0)
                {
                    _logger.LogError($"BarMetrics data for {symbol} isn't up to date! Returning default prediction.");
                    return null;
                }
                return new Prediction
                {
                    BuyMultiplier = Predict(ticker, barMetrics, true),
                    SellMultiplier = Predict(ticker, barMetrics, false),
                    Day = barMetrics[0].BarDay
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return null;
        }
        public async Task<Prediction> Predict(Ticker ticker, DateTime day)
        {
            var symbol = ticker.Symbol;
            BarMetric[] barMetrics;
            var cutoff = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    barMetrics = await stocksContext.BarMetrics.Where(p => p.Symbol == symbol && p.BarDayMilliseconds <= cutoff)
                                        .OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                if (barMetrics.Any())
                {
                    return new Prediction
                    {
                        BuyMultiplier = Predict(ticker, barMetrics, true),
                        SellMultiplier = Predict(ticker, barMetrics, false),
                        Day = barMetrics[0].BarDay
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return null;
        }
        private double Predict(Ticker ticker, BarMetric[] barMetrics, bool buy)
        {
            if (barMetrics.Length == FEATURE_HISTORY_DAY)
            {
                double pricePrediction;

                if (buy)
                {
                    var bullPricePrediction = ((
                                        (barMetrics[0].AlmaSMA1.ZeroReduce(ticker.AlmaSma1Avg + ticker.AlmaSma1StDev, ticker.AlmaSma1Avg - ticker.AlmaSma1StDev) * 0.2) +
                                        (barMetrics[0].AlmaSMA2.ZeroReduce(ticker.AlmaSma2Avg + ticker.AlmaSma2StDev, ticker.AlmaSma2Avg - ticker.AlmaSma2StDev) * 0.2) +
                                        (barMetrics[0].AlmaSMA3.ZeroReduce(ticker.AlmaSma3Avg + ticker.AlmaSma3StDev, ticker.AlmaSma3Avg - ticker.AlmaSma3StDev) * 0.2)
                                      ) * barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(0, -ticker.AlmaVelStDev)) +
                                      ((1 - barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - ticker.SMASMAStDev)) * 0.15) +
                                      (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(ticker.SMAVelStDev, -ticker.SMAVelStDev) * 0.1) +
                                      (barMetrics[0].WeekTrend.DoubleReduce(0, -2) * barMetrics.CalculateAvgVelocity(b => b.WeekTrend).DoubleReduce(0.1, -0.2) * 0.15);

                    var bearPricePrediction = ((
                                        ((1 - barMetrics[0].AlmaSMA1.DoubleReduce(ticker.AlmaSma1Avg, ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 1.5))) * 0.2) +
                                        ((1 - barMetrics[0].AlmaSMA2.DoubleReduce(ticker.AlmaSma2Avg, ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 1.5))) * 0.2) +
                                        ((1 - barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg, ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 1.5))) * 0.2)
                                      ) * barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(0.5 * ticker.AlmaVelStDev, -ticker.AlmaVelStDev)) +
                                      ((1 - barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - (ticker.SMASMAStDev * 1.5))) * 0.15) +
                                      (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(0.5 * ticker.SMAVelStDev, -0.5 * ticker.SMAVelStDev) * 0.1) +
                                      (barMetrics[0].WeekTrend.DoubleReduce(1, -1) * barMetrics.CalculateAvgVelocity(b => b.WeekTrend).DoubleReduce(0.2, -0.1) * 0.15);

                    var coeff = barMetrics.Average(b => b.SMASMA).DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - ticker.SMASMAStDev) * barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(0.5 * ticker.SMAVelStDev, -ticker.SMAVelStDev);
                    pricePrediction = (coeff * bullPricePrediction) + ((1 - coeff) * bearPricePrediction);
                }
                else
                {
                    var bullPricePrediction = ((
                                                  ((1 - barMetrics[0].AlmaSMA1.ZeroReduce(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 2), ticker.AlmaSma1Avg - ticker.AlmaSma1StDev)) * barMetrics[0].AlmaSMA1.DoubleReduce(ticker.AlmaSma1Avg - ticker.AlmaSma1StDev, ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 2)) * 0.2) +
                                                  ((1 - barMetrics[0].AlmaSMA2.ZeroReduce(ticker.AlmaSma2Avg + (ticker.AlmaSma2StDev * 2), ticker.AlmaSma2Avg - ticker.AlmaSma2StDev)) * barMetrics[0].AlmaSMA2.DoubleReduce(ticker.AlmaSma2Avg - ticker.AlmaSma2StDev, ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 2)) * 0.2) +
                                                  ((1 - barMetrics[0].AlmaSMA3.ZeroReduce(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 2), ticker.AlmaSma3Avg - ticker.AlmaSma3StDev)) * barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg - ticker.AlmaSma3StDev, ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 2)) * 0.2)
                                              ) * (1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(ticker.AlmaVelStDev, 0))) +
                                            (barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg + (ticker.SMASMAStDev * 1.5), ticker.SMASMAAvg) * 0.15) +
                                            (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(0.5 * ticker.SMAVelStDev, -0.5 * ticker.SMAVelStDev) * 0.1) +
                                            ((1 - barMetrics[0].WeekTrend.DoubleReduce(0, -2)) * (1 - barMetrics.CalculateAvgVelocity(b => b.WeekTrend).DoubleReduce(0.1, -0.2)) * 0.15);

                    var bearPricePrediction = ((
                                                  (barMetrics[0].AlmaSMA1.DoubleReduce(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 1.5), ticker.AlmaSma1Avg) * 0.2) +
                                                  (barMetrics[0].AlmaSMA2.DoubleReduce(ticker.AlmaSma2Avg + (ticker.AlmaSma2StDev * 1.5), ticker.AlmaSma2Avg) * 0.2) +
                                                  (barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 1.5), ticker.AlmaSma3Avg) * 0.2)
                                              ) * (1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(ticker.AlmaVelStDev, 0))) +
                                            (barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg + ticker.SMASMAStDev, ticker.SMASMAAvg) * 0.15) +
                                            (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(ticker.SMAVelStDev, -ticker.SMAVelStDev) * 0.1) +
                                            ((1 - barMetrics[0].WeekTrend.DoubleReduce(1, -1)) * (1 - barMetrics.CalculateAvgVelocity(b => b.WeekTrend).DoubleReduce(0.2, -0.1)) * 0.15);

                    var coeff = barMetrics.Average(b => b.SMASMA).DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - ticker.SMASMAStDev) * barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(0.5 * ticker.SMAVelStDev, -ticker.SMAVelStDev);
                    pricePrediction = (coeff * bullPricePrediction) + ((1 - coeff) * bearPricePrediction);
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
    }
    public class Prediction
    {
        public double SellMultiplier { get; set; }
        public double BuyMultiplier { get; set; }
        public DateTime Day { get; set; }
    }
}