using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace WarzoneHelper.Core.Net
{
    /// <summary>
    /// Uses an ETW kernel network trace to observe cod.exe's UDP packets and thereby learn the
    /// real remote endpoints the connection table hides. Requires an elevated (admin) process.
    /// Falls back gracefully (Available=false) if the session can't be created.
    /// </summary>
    public sealed class EtwUdpPeerSource : IUdpPeerSource
    {
        private const string SessionName = "WarzoneHelper-KernelNet";
        private readonly Dictionary<string, UdpPeer> _peers = new Dictionary<string, UdpPeer>();
        private readonly object _lock = new object();
        private readonly TimeSpan _window = TimeSpan.FromSeconds(6);

        private TraceEventSession _session;
        private Thread _thread;
        private Func<ISet<int>> _pids;
        private Action<string> _log;
        private volatile bool _available;

        public bool Available => _available;

        public void Start(Func<ISet<int>> pidProvider, Action<string> log)
        {
            _pids = pidProvider;
            _log = log;
            try
            {
                _session = new TraceEventSession(SessionName)
                {
                    StopOnDispose = true
                };
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                _session.Source.Kernel.UdpIpSend += e => Record(e.ProcessID, e.daddr.ToString(), e.dport, e.sport, e.size, true);
                _session.Source.Kernel.UdpIpRecv += e => Record(e.ProcessID, e.saddr.ToString(), e.sport, e.dport, e.size, false);

                _thread = new Thread(() =>
                {
                    try { _session.Source.Process(); }
                    catch (Exception ex) { _log?.Invoke($"[net] ETW loop ended: {ex.Message}"); }
                })
                { IsBackground = true, Name = "wzh-etw" };
                _thread.Start();
                _available = true;
                _log?.Invoke("[net] ETW UDP peer source active.");
            }
            catch (Exception ex)
            {
                _available = false;
                _log?.Invoke($"[net] ETW UDP source unavailable ({ex.Message}). Need admin rights.");
                Dispose();
            }
        }

        private void Record(int pid, string remote, int remotePort, int localPort, int size, bool sent)
        {
            var pids = _pids?.Invoke();
            if (pids == null || !pids.Contains(pid)) return;
            var key = $"UDP:{remote}:{remotePort}";
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!_peers.TryGetValue(key, out var p))
                {
                    p = new UdpPeer { RemoteAddress = remote, RemotePort = remotePort, LocalPort = localPort, FirstSeen = now };
                    _peers[key] = p;
                }
                p.LastSeen = now;
                p.LocalPort = localPort;
                if (sent) p.BytesSent += size; else p.BytesRecv += size;
            }
        }

        public IReadOnlyCollection<UdpPeer> Snapshot()
        {
            var cutoff = DateTime.UtcNow - _window;
            lock (_lock)
            {
                foreach (var stale in _peers.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
                    _peers.Remove(stale);
                return _peers.Values.Select(Clone).ToList();
            }
        }

        private static UdpPeer Clone(UdpPeer p) => new UdpPeer
        {
            RemoteAddress = p.RemoteAddress, RemotePort = p.RemotePort, LocalPort = p.LocalPort,
            BytesSent = p.BytesSent, BytesRecv = p.BytesRecv, FirstSeen = p.FirstSeen, LastSeen = p.LastSeen
        };

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            _available = false;
        }
    }
}
