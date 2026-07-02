using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace GameHelper.Core.Net
{
    public enum ConnProtocol { Tcp, Udp }

    public sealed class Connection
    {
        public ConnProtocol Protocol;
        public string LocalAddress;
        public int LocalPort;
        public string RemoteAddress;   // null/0.0.0.0 for UDP endpoints with no fixed peer
        public int RemotePort;
        public int Pid;
        public string State;           // TCP only

        public string Key => $"{Protocol}:{RemoteAddress}:{RemotePort}:{LocalPort}";
    }

    /// <summary>
    /// Enumerates per-process TCP/UDP endpoints via the IP Helper API
    /// (GetExtendedTcpTable / GetExtendedUdpTable), so we can map cod.exe's PID to its sockets.
    /// </summary>
    public static class ConnectionTable
    {
        // AF_INET
        private const int AF_INET = 2;

        // TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int UDP_TABLE_OWNER_PID = 1;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
            bool sort, int ipVersion, int tblClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
            bool sort, int ipVersion, int tblClass, int reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;   // network byte order, low word
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW_OWNER_PID
        {
            public uint localAddr;
            public uint localPort;
            public uint owningPid;
        }

        private static readonly string[] TcpStates =
        {
            "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED",
            "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK",
            "TIME_WAIT", "DELETE_TCB"
        };

        public static List<Connection> GetConnections(ISet<int> pids)
        {
            var result = new List<Connection>();
            result.AddRange(GetTcp(pids));
            result.AddRange(GetUdp(pids));
            return result;
        }

        private static IEnumerable<Connection> GetTcp(ISet<int> pids)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                    yield break;

                int count = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    var r = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(row, typeof(MIB_TCPROW_OWNER_PID));
                    row += rowSize;
                    if (pids != null && !pids.Contains((int)r.owningPid)) continue;
                    yield return new Connection
                    {
                        Protocol = ConnProtocol.Tcp,
                        LocalAddress = ToIp(r.localAddr),
                        LocalPort = ToPort(r.localPort),
                        RemoteAddress = ToIp(r.remoteAddr),
                        RemotePort = ToPort(r.remotePort),
                        Pid = (int)r.owningPid,
                        State = r.state < TcpStates.Length ? TcpStates[r.state] : r.state.ToString()
                    };
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static IEnumerable<Connection> GetUdp(ISet<int> pids)
        {
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0)
                    yield break;

                int count = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                int rowSize = Marshal.SizeOf(typeof(MIB_UDPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    var r = (MIB_UDPROW_OWNER_PID)Marshal.PtrToStructure(row, typeof(MIB_UDPROW_OWNER_PID));
                    row += rowSize;
                    if (pids != null && !pids.Contains((int)r.owningPid)) continue;
                    yield return new Connection
                    {
                        Protocol = ConnProtocol.Udp,
                        LocalAddress = ToIp(r.localAddr),
                        LocalPort = ToPort(r.localPort),
                        RemoteAddress = null,   // UDP owner table has no remote peer
                        RemotePort = 0,
                        Pid = (int)r.owningPid,
                        State = null
                    };
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static string ToIp(uint addr) => new IPAddress(BitConverter.GetBytes(addr)).ToString();

        private static int ToPort(uint port)
        {
            // Ports are in network byte order in the low 16 bits.
            return ((int)(port & 0xFF) << 8) | (int)((port >> 8) & 0xFF);
        }
    }
}
