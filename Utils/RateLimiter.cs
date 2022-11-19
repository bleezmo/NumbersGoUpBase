using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public class RateLimiter : IDisposable
    {
        private readonly IAppCancellation _appCancellation;
        private readonly IHostEnvironment _environment;
        private readonly SemaphoreSlim _semAlpacaData = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semAlpacaTrader = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semPolygon = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semFinnhub = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semAlphavantage = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semFMP = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semTradier = new SemaphoreSlim(10, 10);
        private readonly SemaphoreSlim _semTradierTrade = new SemaphoreSlim(10, 10);

        public RateLimiter(IAppCancellation appCancellation, IHostEnvironment environment)
        {
            _appCancellation = appCancellation;
            _environment = environment;
        }
        public async Task LimitAlpacaDataRate() => await LimitRate(350, _semAlpacaData);
        public async Task LimitAlpacaTraderRate() => await LimitRate(350, _semAlpacaTrader);
        public async Task LimitTradierRate() => await LimitRate(_environment.IsProduction() ? 5000 : 10000, _semTradier);
        public async Task LimitTradierTradeRate() => await LimitRate(10000, _semTradierTrade);
        public async Task LimitPolygonRate() => await LimitRate(12100, _semPolygon);
        public async Task LimitFinnhubRate() => await LimitRate(1100, _semFinnhub);
        public async Task LimitAlphavantageRate() => await LimitRate(12100, _semAlphavantage);
        public async Task LimitFMPRate() => await LimitRate(220, _semFMP);
        private async Task LimitRate(int limit, SemaphoreSlim sem)
        {
            await sem.WaitAsync(_appCancellation.Token);
            _ = Task.Run(async () => {
                try
                {
                    await Task.Delay(limit, _appCancellation.Token);
                }
                finally
                {
                    sem.Release();
                }
            }).ConfigureAwait(false);
        }
        public void Dispose()
        {
            _semAlpacaData.Dispose();
            _semAlpacaTrader.Dispose();
            _semPolygon.Dispose();
            _semFinnhub.Dispose();
            _semAlphavantage.Dispose();
            _semTradier.Dispose();
            _semTradierTrade.Dispose();
        }
    }
}
