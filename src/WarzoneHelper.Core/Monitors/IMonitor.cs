using System;

namespace WarzoneHelper.Core.Monitors
{
    public interface IMonitor : IDisposable
    {
        string Name { get; }
        void Start();
        void Stop();
    }
}
