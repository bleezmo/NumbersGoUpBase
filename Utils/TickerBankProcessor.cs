using CsvHelper;
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
                    int? tickerIndex = null, sectorIndex = null, marketCapIndex = null, peRatioIndex = null, currentRatioIndex = null, debtEquityRatioIndex = null,
                         dividendIndex = null, ebitdaIndex = null, evebitdaIndex = null, epsIndex = null, incomeIndex = null, priceIndex = null, currentEPSIndex = null, futureEPSIndex = null;
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
                                    if (headers[i].StartsWith("Market Capitalization")) { marketCapIndex = i; }
                                    if (headers[i].StartsWith("Price to Earnings Ratio")) { peRatioIndex = i; }
                                    if (headers[i].StartsWith("Current Ratio")) { currentRatioIndex = i; }
                                    if (headers[i].StartsWith("Debt to Equity Ratio")) { debtEquityRatioIndex = i; }
                                    if (headers[i].StartsWith("Dividend")) { dividendIndex = i; }
                                    if (headers[i].StartsWith("EBITDA")) { ebitdaIndex = i; }
                                    if (headers[i].StartsWith("Enterprise Value/EBITDA")) { evebitdaIndex = i; }
                                    if (headers[i] == "EPS Diluted (TTM)") { epsIndex = i; }
                                    if (headers[i].StartsWith("Net Income")) { incomeIndex = i; }
                                    if (headers[i].StartsWith("Price")) { priceIndex = i; }
                                    if (headers[i].StartsWith("Price")) { priceIndex = i; }
                                    if (headers[i] == "EPS Diluted (MRQ)") { currentEPSIndex = i; }
                                    if (headers[i] == "EPS Forecast (FQ)") { futureEPSIndex = i; }
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
                                if (marketCapIndex.HasValue && double.TryParse(csv[marketCapIndex.Value], out var marketCap))
                                {
                                    ticker.MarketCap = marketCap;
                                }
                                else
                                {
                                    _logger.LogWarning($"Market cap not found for {ticker.Symbol}");
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
                                if (incomeIndex.HasValue && double.TryParse(csv[incomeIndex.Value], out var income))
                                {
                                    ticker.Earnings = income;
                                }
                                else
                                {
                                    _logger.LogWarning($"Income not found for {ticker.Symbol}");
                                }
                                if (evebitdaIndex.HasValue && double.TryParse(csv[evebitdaIndex.Value], out var evebitda))
                                {
                                    ticker.EVEBITDA = evebitda;
                                }
                                else
                                {
                                    //_logger.LogWarning($"EV EBITDA ratio not found for {ticker.Symbol}");
                                }
                                if (epsIndex.HasValue && double.TryParse(csv[epsIndex.Value], out var eps))
                                {
                                    if(currentEPSIndex.HasValue && double.TryParse(csv[currentEPSIndex.Value], out var currentEPS) &&
                                        futureEPSIndex.HasValue && double.TryParse(csv[futureEPSIndex.Value], out var futureEPS))
                                    {
                                        var avg = (futureEPS + currentEPS) / 2;
                                        var changePerc = (avg - currentEPS) / currentEPS;
                                        ticker.EPS = (changePerc * eps) + eps;
                                    }
                                    else
                                    {
                                        ticker.EPS = eps;
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning($"EPS not found for {ticker.Symbol}");
                                }
                                if (priceIndex.HasValue && double.TryParse(csv[priceIndex.Value], out var price))
                                {
                                    ticker.PERatio = ticker.EPS > 0 ? price / ticker.EPS : ticker.PERatio;
                                }
                                else
                                {
                                    _logger.LogWarning($"EPS not found for {ticker.Symbol}");
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
            dest.MarketCap = src.MarketCap;
            dest.CurrentRatio = src.CurrentRatio;
            dest.DebtEquityRatio = src.DebtEquityRatio;
            dest.DividendYield = src.DividendYield;
            dest.Earnings = src.Earnings;
            dest.EPS = src.EPS;
            dest.PERatio = src.PERatio;
            dest.EVEBITDA = src.EVEBITDA;
            dest.PriceChangeAvg = src.PriceChangeAvg;
        }
    }
}
