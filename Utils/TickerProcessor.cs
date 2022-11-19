using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public interface ITickerFile
    {
        public Task<Stream> OpenRead();
    }
    public interface ITickerHash
    {
        public Task<string> GetCurrentHash();
        public Task WriteNewHash(string hash);
    }
    public interface ITickerProcessor
    {
        Task<List<Ticker>> DownloadTickers(bool alwaysProcessData = false);
    }
    public class TickerProcessIShares : ITickerProcessor
    {
        private const string URI = "https://www.ishares.com/us/products/264623/ishares-core-dividend-growth-etf/1467271812596.ajax?fileType=csv&fileName=DGRO_holdings&dataType=fund";
        private const string PREFIX = "DGRO";

        private readonly IAppCancellation _appCancellation;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TickerProcessIShares> _logger;
        private readonly string _externalFilesPath;

        public int DayReloadTimeMonths => 2;

        public TickerProcessIShares(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory httpClientFactory, IAppCancellation appCancellation, ILogger<TickerProcessIShares> logger)
        {
            _appCancellation = appCancellation;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _externalFilesPath = environment.EnvironmentName == "Testing" ? configuration.GetValue<string>("NUMBERS_GO_UP_EXTERNAL") : $"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}ExternalFiles";
        }
        public async Task<List<Ticker>> DownloadTickers(bool alwaysProcessData = false)
        {
            bool fileDownloaded = false;
            var directoryInfo = Directory.CreateDirectory(_externalFilesPath);
            var tickerFile = directoryInfo.GetFiles($"{PREFIX}*").FirstOrDefault();
            if (tickerFile != null)
            {
                //split file name up to get date component and parse it as DateTime
                var parsedDate = string.Join('-', tickerFile.Name.Split('_').Skip(1)).Split('.')[0];
                var fileDate = DateTime.Parse(parsedDate);
                if (DateTime.Now.Subtract(fileDate.AddMonths(DayReloadTimeMonths)).TotalDays > 0)
                {
                    tickerFile.Delete();
                    tickerFile = null;
                }
            }
            if (tickerFile == null)
            {
                using(var client = _httpClientFactory.CreateClient())
                {
                    byte[] fileBytes = await client.GetByteArrayAsync(URI, _appCancellation.Token);
                    await File.WriteAllBytesAsync($"{directoryInfo.FullName}{Path.DirectorySeparatorChar}{PREFIX}_{DateTime.Now.ToString("yyyy_MM_dd")}.csv", fileBytes, _appCancellation.Token);
                    tickerFile = new FileInfo($"{directoryInfo.FullName}{Path.DirectorySeparatorChar}{PREFIX}_{DateTime.Now.ToString("yyyy_MM_dd")}.csv");
                    fileDownloaded = true;
                }
            }
            List<Ticker> tickers = new List<Ticker>();
            if (fileDownloaded || alwaysProcessData)
            {
                using (var sr = new StreamReader(tickerFile.OpenRead()))
                {
                    var headerLookup = new Dictionary<string, int>();
                    while (sr.Peek() >= 0)
                    {

                        var line = await sr.ReadLineAsync();
                        if (line.StartsWith("Ticker,"))
                        {
                            var headers = line.Split(',');
                            for (var i = 0; i < headers.Length; i++)
                            {
                                headerLookup.Add(headers[i], i);
                            }
                            break;
                        }
                    }
                    while (CheckLine(await sr.ReadLineAsync(), out var fields))
                    {
                        if (fields[headerLookup["Asset Class"]] == "Equity")
                        {
                            tickers.Add(new Ticker
                            {
                                Symbol = fields[headerLookup["Ticker"]],
                                Sector = fields[headerLookup["Sector"]]
                            });
                        }
                    }
                }
            }
            return tickers;
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
    public class TickerProcessCustom : ITickerProcessor
    {
        private const string CUSTOM_TICKERS_NAME = "ticker_picks.csv";

        private readonly IAppCancellation _appCancellation;
        private readonly HttpClient _httpClient;
        private readonly ILogger<TickerProcessCustom> _logger;
        private readonly string _externalFilesPath;
        private readonly ITickerHash _tickerHash;
        private readonly ITickerFile _tickerFile;

        public TickerProcessCustom(IConfiguration configuration, IHostEnvironment environment, HttpClient client, IAppCancellation appCancellation, ILogger<TickerProcessCustom> logger, 
                                    ITickerHash tickerHash, ITickerFile tickerFile)
        {
            _appCancellation = appCancellation;
            _httpClient = client;
            _logger = logger;
            var path = configuration["NUMBERS_GO_UP_EXTERNAL"];
            _externalFilesPath = $"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}{path}";
            _tickerHash = tickerHash;
            _tickerFile = tickerFile;
        }
        public async Task<List<Ticker>> DownloadTickers(bool alwaysProcessData = false)
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
            List<Ticker> tickers = new List<Ticker>();
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
                    while (CheckLine(await sr.ReadLineAsync(), out var fields))
                    {
                        tickers.Add(new Ticker
                        {
                            Symbol = fields[headerLookup["Ticker"]],
                            Sector = fields[headerLookup["Sector"]]
                        });
                    }
                }
                await _tickerHash.WriteNewHash(currentHash);
            }
            var distinctTickers = new List<Ticker>();
            foreach(var ticker in tickers)
            {
                if(!distinctTickers.Any(t => t.Symbol == ticker.Symbol))
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
}
