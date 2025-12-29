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
        private readonly SemaphoreSlim _semTradier = new SemaphoreSlim(10, 10);
        private readonly SemaphoreSlim _semTradierTrade = new SemaphoreSlim(10, 10);
        private readonly SemaphoreSlim _semSchwab = new SemaphoreSlim(1, 1);

        public RateLimiter(IAppCancellation appCancellation, IHostEnvironment environment)
        {
            _appCancellation = appCancellation;
            _environment = environment;
        }
        public async Task LimitTradierRate() => await LimitRate(_environment.IsProduction() ? 5000 : 10000, _semTradier);
        public async Task LimitTradierTradeRate() => await LimitRate(10000, _semTradierTrade);
        public async Task LimitSchwabRate() => await LimitRate(500, _semSchwab);
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
            _semTradier.Dispose();
            _semTradierTrade.Dispose();
            _semSchwab.Dispose();
        }
    }
}
