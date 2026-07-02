using System;
using System.Collections.Generic;

namespace GameHelper.Core.Net
{
    /// <summary>
    /// Observed UDP peer of the game process, aggregated over a short window.
    /// The connection table cannot supply UDP remote endpoints, so this comes from
    /// packet-level visibility (ETW) instead.
    /// </summary>
    public sealed class UdpPeer
    {
        public string RemoteAddress;
        public int RemotePort;
        public int LocalPort;
        public long BytesSent;
        public long BytesRecv;
        public DateTime FirstSeen;
        public DateTime LastSeen;

        public string Key => $"UDP:{RemoteAddress}:{RemotePort}";
    }

    /// <summary>
    /// Supplies the set of UDP peers currently exchanging packets with the tracked PIDs.
    /// Implementations: ETW kernel network trace (real IPs, needs admin), or a no-op fallback.
    /// </summary>
    public interface IUdpPeerSource : IDisposable
    {
        bool Available { get; }
        void Start(Func<ISet<int>> pidProvider, Action<string> log);
        /// <summary>Snapshot of peers seen since the last call (rolling window maintained internally).</summary>
        IReadOnlyCollection<UdpPeer> Snapshot();
    }

    /// <summary>Used when ETW is unavailable (no admin / provider missing). Reports nothing.</summary>
    public sealed class NullUdpPeerSource : IUdpPeerSource
    {
        public bool Available => false;
        public void Start(Func<ISet<int>> pidProvider, Action<string> log)
        {
            log?.Invoke("[net] UDP peer source unavailable; UDP game-server IP detection disabled. " +
                        "Run elevated with the ETW source to enable it.");
        }
        public IReadOnlyCollection<UdpPeer> Snapshot() => Array.Empty<UdpPeer>();
        public void Dispose() { }
    }
}
