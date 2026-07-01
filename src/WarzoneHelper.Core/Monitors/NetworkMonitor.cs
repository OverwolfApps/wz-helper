using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WarzoneHelper.Core.Config;
using WarzoneHelper.Core.Events;
using WarzoneHelper.Core.Geo;
using WarzoneHelper.Core.Net;

namespace WarzoneHelper.Core.Monitors
{
    /// <summary>
    /// Polls cod.exe's sockets and classifies them:
    ///  - Sustained UDP peers on CoD game ports  => GAME_SERVER_CONNECTED/DISCONNECTED
    ///  - TCP peers (Demonware/Akamai/AWS/etc.)   => SERVICE_CONNECTED/DISCONNECTED
    /// Game-server candidates are confirmed only after they persist for several polls, and
    /// enriched with local MaxMind geo + an ICMP ping. UDP remotes come from the ETW source
    /// (the connection table cannot supply them).
    /// </summary>
    public sealed class NetworkMonitor : IMonitor
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly GeoIpResolver _geo;
        private readonly ProcessTracker _proc;
        private readonly IUdpPeerSource _udp;
        private readonly HomeLocator _home;
        private readonly MatchState _match;

        // ETW aggregates UDP peers over a rolling ~6s window; use that to turn window bytes into a rate.
        private const double UdpWindowSeconds = 6.0;

        private Timer _timer;

        private sealed class Tracked
        {
            public string Ip;
            public int Port;
            public string Proto;
            public int SeenPolls;
            public int MissedPolls;
            public bool Announced;
            public bool IsGameServer;
            public long BytesSent;
            public long BytesRecv;
            public readonly DateTime FirstSeenUtc = DateTime.UtcNow;
        }

        private readonly Dictionary<string, Tracked> _tracked = new Dictionary<string, Tracked>();

        public string Name => "network";

        public NetworkMonitor(HelperConfig cfg, EventBus bus, GeoIpResolver geo,
            ProcessTracker proc, IUdpPeerSource udp, HomeLocator home, MatchState match)
        {
            _cfg = cfg; _bus = bus; _geo = geo; _proc = proc; _udp = udp; _home = home; _match = match;
        }

        public void Start()
        {
            _udp?.Start(() => _proc.CurrentPids(), _bus.Log);
            _timer = new Timer(_ => Poll(), null, 1000, Math.Max(250, _cfg.NetworkPollMs));
        }

        private bool IsGamePort(int port)
        {
            if (_cfg.GameUdpPorts != null && _cfg.GameUdpPorts.Contains(port)) return true;
            var starts = _cfg.GameUdpPortRangeStart ?? new int[0];
            var ends = _cfg.GameUdpPortRangeEnd ?? new int[0];
            for (int i = 0; i < Math.Min(starts.Length, ends.Length); i++)
                if (port >= starts[i] && port <= ends[i]) return true;
            return false;
        }

        private void Poll()
        {
            try
            {
                var pids = _proc.CurrentPids();
                if (pids == null || pids.Count == 0) { SweepAll(); return; }

                var current = new HashSet<string>();

                // --- UDP peers (potential game servers) ---
                foreach (var peer in _udp?.Snapshot() ?? Array.Empty<UdpPeer>())
                {
                    if (string.IsNullOrEmpty(peer.RemoteAddress)) continue;
                    bool gamePort = IsGamePort(peer.RemotePort);
                    // Game traffic is bidirectional and sustained; ignore one-shot stray packets.
                    var key = $"UDP:{peer.RemoteAddress}:{peer.RemotePort}";
                    current.Add(key);
                    Touch(key, peer.RemoteAddress, peer.RemotePort, "UDP", isGameCandidate: gamePort,
                        sent: peer.BytesSent, recv: peer.BytesRecv);
                }

                // --- TCP peers (backend services) ---
                foreach (var c in ConnectionTable.GetConnections(pids))
                {
                    if (c.Protocol != ConnProtocol.Tcp) continue;
                    if (string.IsNullOrEmpty(c.RemoteAddress) || c.RemoteAddress == "0.0.0.0") continue;
                    if (c.RemotePort == 0) continue;
                    if (c.State != "ESTABLISHED") continue;
                    var key = $"TCP:{c.RemoteAddress}:{c.RemotePort}";
                    current.Add(key);
                    Touch(key, c.RemoteAddress, c.RemotePort, "TCP", isGameCandidate: false, sent: 0, recv: 0);
                }

                Sweep(current);
            }
            catch (Exception ex) { _bus.Log($"[network] poll error: {ex.Message}"); }
        }

        private void Touch(string key, string ip, int port, string proto, bool isGameCandidate, long sent, long recv)
        {
            if (!_tracked.TryGetValue(key, out var t))
            {
                t = new Tracked { Ip = ip, Port = port, Proto = proto, IsGameServer = isGameCandidate };
                _tracked[key] = t;
            }
            t.MissedPolls = 0;
            t.SeenPolls++;
            t.BytesSent = sent;
            t.BytesRecv = recv;

            if (t.Announced) return;

            if (proto == "UDP" && isGameCandidate)
            {
                // Promote to game server only once it persists, has enough throughput, and (optionally)
                // we believe we're in a match. These filters exist because lobby/matchmaking traffic
                // also uses game ports — they need tuning against real in-match captures.
                bool persisted = t.SeenPolls >= _cfg.GameServerConfirmPolls;
                bool throughputOk = _cfg.GameServerMinBytesPerSec <= 0 ||
                                    (t.BytesSent + t.BytesRecv) / UdpWindowSeconds >= _cfg.GameServerMinBytesPerSec;
                bool matchOk = !_cfg.GameServerRequireInMatch || (_match != null && _match.InMatch);

                if (persisted && throughputOk && matchOk)
                {
                    t.IsGameServer = true;
                    Announce(t, connected: true);
                }
            }
            else if (proto == "TCP")
            {
                Announce(t, connected: true);
            }
        }

        private void Announce(Tracked t, bool connected)
        {
            t.Announced = connected;
            var geo = _geo?.Resolve(t.Ip);
            long ping = -1;
            if (connected && _cfg.ResolvePing && (t.IsGameServer || t.Proto == "UDP"))
                ping = Pinger.PingMs(t.Ip, _cfg.PingTimeoutMs);

            string name = connected
                ? (t.IsGameServer ? EventNames.GameServerConnected : EventNames.ServiceConnected)
                : (t.IsGameServer ? EventNames.GameServerDisconnected : EventNames.ServiceDisconnected);

            // Distance from home + VPN/proxy heuristic (only meaningful for game servers).
            double? distanceKm = null;
            if (geo?.Latitude != null && geo.Longitude != null && _home != null && _home.Known)
                distanceKm = Math.Round(HomeLocator.DistanceKm(
                    _home.Latitude.Value, _home.Longitude.Value, geo.Latitude.Value, geo.Longitude.Value), 0);

            bool highPing = ping >= 0 && ping >= _cfg.VpnPingThresholdMs;
            bool tooFar = distanceKm.HasValue && distanceKm.Value >= _cfg.VpnDistanceKmThreshold;
            bool isLikelyVpn = t.IsGameServer && (highPing || tooFar);

            long totalBytes = t.BytesSent + t.BytesRecv;
            var evt = new HelperEvent(name, EventSource.Network)
                .With("ip", t.Ip)
                .With("port", t.Port)
                .With("protocol", t.Proto)
                .With("isGameServer", t.IsGameServer)
                .With("inMatch", _match != null && _match.InMatch)
                .With("pingMs", ping)
                .With("distanceKm", distanceKm)
                .With("isLikelyVPN", isLikelyVpn)
                .With("vpnReason", isLikelyVpn ? (highPing && tooFar ? "ping+distance" : highPing ? "ping" : "distance") : null)
                .With("bytes", totalBytes)
                .With("bytesSent", t.BytesSent)
                .With("bytesRecv", t.BytesRecv)
                .With("bytesPerSec", (long)(totalBytes / UdpWindowSeconds))
                .With("secondsTracked", Math.Round((DateTime.UtcNow - t.FirstSeenUtc).TotalSeconds, 1));

            if (geo != null)
                foreach (var kv in geo.ToDict()) evt.With(kv.Key, kv.Value);

            _bus.Publish(evt);
        }

        private void Sweep(HashSet<string> current)
        {
            var drop = new List<string>();
            foreach (var kv in _tracked)
            {
                if (current.Contains(kv.Key)) continue;
                kv.Value.MissedPolls++;
                if (kv.Value.MissedPolls >= _cfg.GameServerDropPolls)
                {
                    if (kv.Value.Announced) Announce(kv.Value, connected: false);
                    drop.Add(kv.Key);
                }
            }
            foreach (var k in drop) _tracked.Remove(k);
        }

        private void SweepAll()
        {
            foreach (var kv in _tracked.ToList())
                if (kv.Value.Announced) Announce(kv.Value, connected: false);
            _tracked.Clear();
        }

        public void Stop() { _timer?.Dispose(); _timer = null; _udp?.Dispose(); }
        public void Dispose() => Stop();
    }
}
