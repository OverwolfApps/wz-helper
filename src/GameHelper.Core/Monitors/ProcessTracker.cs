using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GameHelper.Core.Config;
using GameHelper.Core.Events;

namespace GameHelper.Core.Monitors
{
    /// <summary>
    /// Polls for the game process by name, exposing its current PIDs and raising
    /// GAME_PROCESS_STARTED / GAME_PROCESS_STOPPED. Other monitors query CurrentPids().
    /// </summary>
    public sealed class ProcessTracker : IMonitor
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly HashSet<string> _names;
        private Timer _timer;
        private volatile HashSet<int> _pids = new HashSet<int>();
        private bool _wasRunning;

        public string Name => "process";
        public event Action<bool> RunningChanged;

        public ProcessTracker(HelperConfig cfg, EventBus bus)
        {
            _cfg = cfg;
            _bus = bus;
            _names = new HashSet<string>(
                (cfg.GameProcessNames ?? new string[0])
                    .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n),
                StringComparer.OrdinalIgnoreCase);
        }

        public ISet<int> CurrentPids() => _pids;
        public bool IsRunning => _pids.Count > 0;

        public void Start()
        {
            _timer = new Timer(_ => Poll(), null, 0, 2000);
        }

        private void Poll()
        {
            try
            {
                var found = new HashSet<int>();
                foreach (var name in _names)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        found.Add(p.Id);
                        p.Dispose();
                    }
                }
                _pids = found;

                bool running = found.Count > 0;
                if (running != _wasRunning)
                {
                    _wasRunning = running;
                    (running ? CoreEvents.GameProcessStarted : CoreEvents.GameProcessStopped)
                        .Emit(_bus, e => e.With("pids", found.ToArray()));
                    try { RunningChanged?.Invoke(running); } catch { }
                }
            }
            catch (Exception ex) { _bus.Log($"[process] poll error: {ex.Message}"); }
        }

        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() => Stop();
    }
}
