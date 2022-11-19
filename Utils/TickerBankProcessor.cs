using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using System;
using System.Collections.Generic;
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
    }
    public class TickerBankProcessor : ITickerBankProcessor
    {
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerBankProcessor> _logger;
        private readonly ITickerHash _tickerHash;
        private readonly ITickerFile _tickerFile;

        private static readonly string[] _sectorBlacklist = new[]
        {
            "Cash and/or Derivatives"
        };

        public TickerBankProcessor(IAppCancellation appCancellation, ILogger<TickerBankProcessor> logger, ITickerHash tickerHash, ITickerFile tickerFile)
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
                    var headerLookup = new Dictionary<string, int>();
                    while (sr.Peek() >= 0)
                    {

                        var line = await sr.ReadLineAsync();
                        if (line.StartsWith("Ticker"))
                        {
                            var headers = line.Split(',');
                            for (var i = 0; i < headers.Length; i++)
                            {
                                headerLookup.Add(headers[i], i);
                            }
                            break;
                        }
                    }
                    while (tickers.Count < 1200 && CheckLine(await sr.ReadLineAsync(), out var fields))
                    {
                        var symbol = fields[headerLookup["Ticker"]];
                        var sector = fields[headerLookup["Sector"]];
                        if (!string.IsNullOrWhiteSpace(symbol) && !_sectorBlacklist.Any(s => s == sector))
                        {
                            tickers.Add(new BankTicker
                            {
                                Symbol = symbol,
                                Sector = sector
                            });
                        }
                    }
                }
                await _tickerHash.WriteNewHash(currentHash);
            }
            var distinctTickers = new List<BankTicker>();
            foreach (var ticker in tickers)
            {
                if (!distinctTickers.Any(t => t.Symbol == ticker.Symbol))
                {
                    distinctTickers.Add(ticker);
                }
            }
            return distinctTickers;
        }
        private static bool CheckLine(string line, out string[] fields)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                fields = line.Split(',').Select(field => field.Trim('\"')).ToArray();
                return true;
            }
            fields = null;
            return false;
        }
    }
    public class LocalTickerBankFile : LocalTickerFile
    {
        public LocalTickerBankFile(IConfiguration configuration, IRuntimeSettings runtimeSettings) : base(configuration, runtimeSettings)
        {
            CustomTickersFile = "ticker_bank.csv";
        }
    }
    public class LocalTickerBankHash : LocalTickerHash
    {
        public LocalTickerBankHash(IRuntimeSettings runtimeSettings, IConfiguration configuration, IAppCancellation appCancellation): base(runtimeSettings,configuration,appCancellation)
        {
            CustomTickersHashFile = "ticker_bank_hash";
        }
    }
}
