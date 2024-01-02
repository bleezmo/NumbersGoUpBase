using NumbersGoUp.Models;

namespace NumbersGoUp.Utils
{
    public interface ITickerFile
    {
        Task<DateTimeOffset?> GetLastModified();
        public Task<Stream> OpenRead();
    }
    public interface ITickerHash
    {
        public Task<string> GetCurrentHash();
        public Task WriteNewHash(string hash);
    }
    public interface ITickerPickFile
    {
        public Task<Stream> OpenRead();
    }
    public interface ITickerProcessor
    {
        Task<List<Ticker>> DownloadTickers(bool alwaysProcessData = false);
    }
}
