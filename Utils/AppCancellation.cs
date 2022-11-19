using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public interface IAppCancellation : IDisposable
    {
        void AddCancellationToken(CancellationToken cancellationToken);
        bool IsCancellationRequested { get; }
        CancellationToken Token { get; }
        void Cancel();
        Task Shutdown();
    }
    public class AppCancellation : IAppCancellation
    {
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly IHostApplicationLifetime _lifetime;

        public AppCancellation(IHostApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
        }
        public void AddCancellationToken(CancellationToken cancellationToken)
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
        }
        //
        // Summary:
        //     Gets whether cancellation has been requested for this System.Threading.CancellationTokenSource.
        //
        // Returns:
        //     true if cancellation has been requested for this System.Threading.CancellationTokenSource;
        //     otherwise, false.
        public bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;
        public CancellationToken Token => _cancellationTokenSource.Token;
        public void Cancel()
        {
            if (!IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
        }

        public async Task Shutdown()
        {
            Cancel();
            await Task.Delay(300);
            _lifetime.StopApplication();
        }
    }
}
