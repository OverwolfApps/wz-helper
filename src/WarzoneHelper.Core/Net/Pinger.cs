using System.Net.NetworkInformation;

namespace WarzoneHelper.Core.Net
{
    public static class Pinger
    {
        /// <summary>Returns round-trip time in ms, or -1 on failure/timeout.</summary>
        public static long PingMs(string host, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) return -1;
            try
            {
                using (var p = new Ping())
                {
                    var reply = p.Send(host, timeoutMs);
                    if (reply != null && reply.Status == IPStatus.Success)
                        return reply.RoundtripTime;
                }
            }
            catch { }
            return -1;
        }
    }
}
