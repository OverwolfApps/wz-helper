using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace GameHelper.Core.Net
{
    /// <summary>
    /// Uses an ETW kernel network trace to observe cod.exe's UDP packets and thereby learn the
    /// real remote endpoints the connection table hides. Requires an elevated (admin) process.
    /// Falls back gracefully (Available=false) if the session can't be created.
    /// </summary>
    public sealed class EtwUdpPeerSource : IUdpPeerSource
    {
        private const string SessionName = "GameHelper-KernelNet";
        private readonly Dictionary<string, UdpPeer> _peers = new Dictionary<string, UdpPeer>();
        private readonly object _lock = new object();
        private readonly TimeSpan _window = TimeSpan.FromSeconds(6);

        private TraceEventSession _session;
        private Thread _thread;
        private Func<ISet<int>> _pids;
        private Action<string> _log;
        private volatile bool _available;
        private volatile bool _stopping;

        public bool Available => _available;

        public void Start(Func<ISet<int>> pidProvider, Action<string> log)
        {
            _pids = pidProvider;
            _log = log;
            _stopping = false;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "wzh-etw" };
            _thread.Start();
        }

        /// <summary>
        /// Owns the kernel session for the whole run: (re)creates it and pumps events, and if the
        /// pump dies (e.g. HRESULT 0x80071069 when a leftover same-named session from a prior hard-kill
        /// blocks the bind, or the session is stopped out from under us) it cleans up and retries with
        /// backoff instead of silently going dark for the rest of the process.
        /// </summary>
        private void RunLoop()
        {
            int backoffMs = 1000;
            while (!_stopping)
            {
                try
                {
                    // A kernel session with our name may linger after a crash/hard-kill; stop it so the
                    // fresh EnableKernelProvider below can bind (otherwise Process() throws 0x80071069).
                    try
                    {
                        if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
                        {
                            using (var stale = new TraceEventSession(SessionName) { StopOnDispose = true }) { }
                            _log?.Invoke($"[net] ETW: stopped leftover session '{SessionName}'.");
                        }
                    }
                    catch { /* best effort */ }

                    _session = new TraceEventSession(SessionName) { StopOnDispose = true };
                    _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                    _session.Source.Kernel.UdpIpSend += e => Record(e.ProcessID, e.daddr.ToString(), e.dport, e.sport, e.size, true);
                    _session.Source.Kernel.UdpIpRecv += e => Record(e.ProcessID, e.saddr.ToString(), e.sport, e.dport, e.size, false);

                    _available = true;
                    _log?.Invoke("[net] ETW UDP peer source active.");
                    _session.Source.Process();   // blocks until the session stops or errors

                    // Clean exit (Dispose/Stop) — don't loop.
                    if (_stopping) break;
                    _log?.Invoke("[net] ETW loop ended cleanly; restarting.");
                }
                catch (Exception ex)
                {
                    _available = false;
                    if (_stopping) break;
                    _log?.Invoke($"[net] ETW loop ended: {ex.Message}. Retrying in {backoffMs}ms (need admin rights if this persists).");
                }
                finally
                {
                    try { _session?.Dispose(); } catch { }
                    _session = null;
                    _available = false;
                }

                if (_stopping) break;
                Thread.Sleep(backoffMs);
                backoffMs = Math.Min(backoffMs * 2, 15000);   // cap backoff at 15s
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
            _stopping = true;
            try { _session?.Dispose(); } catch { }   // unblocks Source.Process() in the loop thread
            _session = null;
            _available = false;
        }
    }
}
