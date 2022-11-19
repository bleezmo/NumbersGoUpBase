using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public interface IRuntimeSettings
    {
        public string Path { get; }
        public bool ForceDataCollection { get; set; }
    }
}
