using System;

namespace GameHelper.Core.Monitors
{
    public interface IMonitor : IDisposable
    {
        string Name { get; }
        void Start();
        void Stop();
    }
}
