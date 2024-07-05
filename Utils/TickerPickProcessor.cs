using CsvHelper;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace NumbersGoUp.Utils
{
    public interface ITickerPickProcessor
    {
        Task<IEnumerable<TickerPick>> GetTickers();
    }

    public class TickerPickProcessor : ITickerPickProcessor
    {
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerPickProcessor> _logger;
        private readonly ITickerPickFile _tickerFile;
        private IEnumerable<TickerPick> _tickers = null;

        public TickerPickProcessor(IAppCancellation appCancellation, ILogger<TickerPickProcessor> logger, ITickerPickFile tickerFile)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _tickerFile = tickerFile;
        }
        public async Task<IEnumerable<TickerPick>> GetTickers()
        {
            if(_tickers == null)
            {
                _tickers = await LoadTickers();
            }
            return _tickers;
        }
        public async Task<IEnumerable<TickerPick>> LoadTickers()
        {
            List<TickerPick> tickers = new List<TickerPick>();

            using (var sr = new StreamReader(await _tickerFile.OpenRead()))
            {
                int? tickerIndex = null, scoreIndex = null;
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
                                if (headers[i] == "Score") { scoreIndex = i; }
                            }
                        }
                        else
                        {
                            var ticker = new TickerPick();
                            if (tickerIndex.HasValue)
                            {
                                ticker.Symbol = csv[tickerIndex.Value];
                            }
                            else
                            {
                                _logger.LogError("No ticker pick symbol found!!");
                                continue;
                            }

                            if (scoreIndex.HasValue && double.TryParse(csv[scoreIndex.Value], out var score))
                            {
                                ticker.Score = score;
                            }
                            else
                            {
                                _logger.LogError($"Score not found for ticker pick {ticker.Symbol}");
                            }
                            if(tickers.Any(t => t.Symbol == ticker.Symbol))
                            {
                                _logger.LogError($"Duplicate ticker pick entries for {ticker.Symbol}");
                            }
                            tickers.Add(ticker);
                        }
                    }
                }
            }
            var (max, min) = tickers.MaxMin(t => t.Score);
            foreach(var ticker in tickers)
            {
                ticker.Score = ticker.Score.DoubleReduce(max, min / 3, 100, 0);
            }
            return tickers;
        }
    }

    public class TickerPick
    {
        public string Symbol { get; set; }
        public double Score { get; set; }
    }
}
