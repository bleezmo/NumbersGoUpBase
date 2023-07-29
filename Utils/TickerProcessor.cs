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
        Task<DateTimeOffset?> GetLastModified();
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
}
