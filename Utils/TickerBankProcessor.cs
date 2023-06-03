﻿using CsvHelper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public interface ITickerBankProcessor
    {
        Task<List<BankTicker>> DownloadTickers(bool alwaysProcessData = false);
        void UpdateBankTicker(BankTicker src, BankTicker dest);
    }

    public class TradingViewTickerBankProcessor : ITickerBankProcessor
    {
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TradingViewTickerBankProcessor> _logger;
        private readonly ITickerHash _tickerHash;
        private readonly ITickerFile _tickerFile;

        public TradingViewTickerBankProcessor(IAppCancellation appCancellation, ILogger<TradingViewTickerBankProcessor> logger, ITickerHash tickerHash, ITickerFile tickerFile)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _tickerHash = tickerHash;
            _tickerFile = tickerFile;
        }
        public async Task<List<BankTicker>> DownloadTickers(bool alwaysProcessData = false)
        {
            var oldHash = await _tickerHash.GetCurrentHash();
            var currentHash = string.Empty;
            using (var md5 = MD5.Create())
            {
                using (var stream = await _tickerFile.OpenRead())
                {
                    currentHash = Convert.ToBase64String(md5.ComputeHash(stream));
                }
            }
            List<BankTicker> tickers = new List<BankTicker>();
            if (oldHash != currentHash || alwaysProcessData)
            {
                using (var sr = new StreamReader(await _tickerFile.OpenRead()))
                {
                    /*
                     * Ticker,Description,Volume,Market Capitalization,Price to Earnings Ratio (TTM),Sector,Current Ratio (MRQ),
                     * Debt to Equity Ratio (MRQ),Dividend Yield Forward,EBITDA (TTM),Enterprise Value/EBITDA (TTM),EPS Diluted (TTM)
                     */
                    int? tickerIndex = null, sectorIndex = null, marketCapIndex = null, peRatioIndex = null, currentRatioIndex = null, debtEquityRatioIndex = null, dividendIndex = null, 
                         ebitdaIndex = null, evebitdaIndex = null, epsIndex = null, priceIndex = null, sharesIndex = null, evIndex = null, currentEPSIndex = null, futureEPSIndex = null, epsQoQIndex = null;
                    using (var csv = new CsvReader(sr, CultureInfo.InvariantCulture))
                    {
                        string[] headers = null;
                        for (var dc = 0; await csv.ReadAsync() && dc < 5000; dc++)
                        {
                            if (dc == 0)
                            {
                                csv.ReadHeader();
                                headers = csv.HeaderRecord;
                                for (var i = 0; i < headers.Length; i++)
                                {
                                    if (headers[i] == "Ticker") { tickerIndex = i; }
                                    if (headers[i] == "Sector") { sectorIndex = i; }
                                    if (headers[i] == "Market Capitalization") { marketCapIndex = i; }
                                    if (headers[i] == "Price to Earnings Ratio (TTM)") { peRatioIndex = i; }
                                    if (headers[i] == "Current Ratio (MRQ)") { currentRatioIndex = i; }
                                    if (headers[i] == "Debt to Equity Ratio (MRQ)") { debtEquityRatioIndex = i; }
                                    if (headers[i] == "Dividend Yield Forward") { dividendIndex = i; }
                                    if (headers[i] == "EBITDA (TTM)") { ebitdaIndex = i; }
                                    if (headers[i] == "Enterprise Value/EBITDA (TTM)") { evebitdaIndex = i; }
                                    if (headers[i] == "EPS Diluted (TTM)") { epsIndex = i; }
                                    if (headers[i] == "Price") { priceIndex = i; }
                                    if (headers[i] == "EPS Diluted (MRQ)") { currentEPSIndex = i; }
                                    if (headers[i] == "EPS Forecast (MRQ)") { futureEPSIndex = i; }
                                    if (headers[i] == "Total Shares Outstanding") { sharesIndex = i; }
                                    if (headers[i] == "Enterprise Value (MRQ)") { evIndex = i; }
                                    if (headers[i] == "EPS Diluted (Quarterly QoQ Growth)") { epsQoQIndex = i; }
                                }
                            }
                            else
                            {
                                var ticker = new BankTicker();
                                if (tickerIndex.HasValue)
                                {
                                    ticker.Symbol = csv[tickerIndex.Value];
                                }
                                else
                                {
                                    _logger.LogError("No ticker symbol found!!");
                                    continue;
                                }
                                if (sectorIndex.HasValue)
                                {
                                    ticker.Sector = csv[sectorIndex.Value];
                                }
                                else
                                {
                                    _logger.LogWarning($"Sector not found for {ticker.Symbol}");
                                }
                                if (peRatioIndex.HasValue && double.TryParse(csv[peRatioIndex.Value], out var peRatio))
                                {
                                    ticker.PERatio = peRatio;
                                }
                                else
                                {
                                    _logger.LogWarning($"PE Ratio not found for {ticker.Symbol}");
                                }
                                if (currentRatioIndex.HasValue && double.TryParse(csv[currentRatioIndex.Value], out var currentRatio))
                                {
                                    ticker.CurrentRatio = currentRatio;
                                }
                                else
                                {
                                    //_logger.LogWarning($"Current Ratio not found for {ticker.Symbol}");
                                }
                                if (debtEquityRatioIndex.HasValue && double.TryParse(csv[debtEquityRatioIndex.Value], out var debtEquityRatio))
                                {
                                    ticker.DebtEquityRatio = debtEquityRatio;
                                }
                                else
                                {
                                    _logger.LogWarning($"Debt Equity Ratio not found for {ticker.Symbol}");
                                }
                                if (dividendIndex.HasValue && double.TryParse(csv[dividendIndex.Value], out var dividend))
                                {
                                    ticker.DividendYield = dividend / 100;
                                }
                                else
                                {
                                    _logger.LogWarning($"Dividend not found for {ticker.Symbol}");
                                }
                                if (epsIndex.HasValue && double.TryParse(csv[epsIndex.Value], out var eps))
                                {
                                    ticker.EPS = eps;
                                    if (eps > 0 && currentEPSIndex.HasValue && double.TryParse(csv[currentEPSIndex.Value], out var currentEPS) &&
                                        futureEPSIndex.HasValue && double.TryParse(csv[futureEPSIndex.Value], out var futureEPS))
                                    {
                                        if (epsQoQIndex.HasValue && double.TryParse(csv[epsQoQIndex.Value], out var epsQoQPerc))
                                        {
                                            if(epsQoQIndex > -100)
                                            {
                                                var pastEPS = currentEPS / Math.Min((epsQoQPerc + 100) / 100, 2);
                                                var finalQ = new[] { pastEPS, currentEPS, futureEPS }.CalculateRegression(4);
                                                ticker.EPS = pastEPS + currentEPS + futureEPS + finalQ;
                                            }
                                            else
                                            {
                                                ticker.EPS = -1;
                                            }
                                        }
                                        else //assume semi-annual earnings reports
                                        {
                                            //_logger.LogInformation($"QoQ eps growth not found. Assuming semi-annual earnings. Ticker {ticker.Symbol}");
                                            ticker.EPS = (1.1 * futureEPS) + (0.9 * currentEPS);
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogError($"Current and/or future eps not found. Using default eps for {ticker.Symbol}");
                                    }
                                    if (sharesIndex.HasValue && double.TryParse(csv[sharesIndex.Value], out var shares))
                                    {
                                        ticker.Shares = shares;
                                        ticker.Earnings = ticker.EPS * shares;
                                    }
                                    else
                                    {
                                        _logger.LogError($"No shares outstanding for {ticker.Symbol} to calculate earnings.");
                                    }
                                }
                                else
                                {
                                    _logger.LogError($"EPS not found for {ticker.Symbol}");
                                }
                                var price = 0.0;
                                if (priceIndex.HasValue && double.TryParse(csv[priceIndex.Value], out price))
                                {
                                    ticker.PERatio = ticker.EPS > 0 ? (price / ticker.EPS) : ticker.PERatio;
                                }
                                else
                                {
                                    _logger.LogWarning($"EPS not found for {ticker.Symbol}");
                                }
                                if (ticker.Earnings > 0)
                                {
                                    double ev = 0;
                                    if (evIndex.HasValue && double.TryParse(csv[evIndex.Value], out ev))
                                    {
                                        ticker.EVEarnings = ev / ticker.Earnings;
                                    }
                                    else if (evebitdaIndex.HasValue && double.TryParse(csv[evebitdaIndex.Value], out var evebitda) &&
                                             ebitdaIndex.HasValue && double.TryParse(csv[ebitdaIndex.Value], out var ebitda))
                                    {
                                        ev = evebitda * ebitda;
                                        ticker.EVEarnings = ev / ticker.Earnings;
                                    }
                                    else
                                    {
                                        _logger.LogWarning($"EV EBITDA ratio unavailable for {ticker.Symbol}. Could not calculate EV.");
                                    }
                                    if (marketCapIndex.HasValue && double.TryParse(csv[marketCapIndex.Value], out var marketCap))
                                    {
                                        ticker.MarketCap = marketCap;
                                    }
                                    else if(ticker.Shares > 0 && price > 0)
                                    {
                                        _logger.LogWarning($"Market cap not found for {ticker.Symbol}. Deriving from shares*price");
                                        ticker.MarketCap = ticker.Shares * price;
                                    }
                                    else
                                    {
                                        _logger.LogError($"Market cap not found for {ticker.Symbol} and unable to derive.");
                                    }
                                    if (ev > 0)
                                    {
                                        ticker.DebtMinusCash = ev - ticker.MarketCap;
                                    }
                                    else
                                    {
                                        ticker.DebtMinusCash = ticker.MarketCap; //make this large to invalidate ticker
                                        _logger.LogWarning($"Unable to calculate DebtMinusCash for {ticker.Symbol}. EV not found.");
                                    }
                                }
                                else if(ticker.Earnings == 0)
                                {
                                    _logger.LogError($"Earnings ratio unavailable for {ticker.Symbol}. Earnings not found.");
                                }
                                tickers.Add(ticker);
                            }
                        }
                    }
                }
                await _tickerHash.WriteNewHash(currentHash);
            }
            return tickers;
        }
        
        public void UpdateBankTicker(BankTicker dest, BankTicker src)
        {
            dest.Sector = src.Sector;
            dest.MarketCap = src.MarketCap;
            dest.CurrentRatio = src.CurrentRatio;
            dest.DebtEquityRatio = src.DebtEquityRatio;
            dest.DividendYield = src.DividendYield;
            dest.Earnings = src.Earnings;
            dest.EPS = src.EPS;
            dest.PERatio = src.PERatio;
            dest.EVEarnings = src.EVEarnings;
            dest.PriceChangeAvg = src.PriceChangeAvg;
            dest.DebtMinusCash = src.DebtMinusCash;
            dest.Shares = src.Shares;
        }
    }
}
