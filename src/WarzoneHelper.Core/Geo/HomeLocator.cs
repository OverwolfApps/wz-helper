using System;
using System.Net;

namespace WarzoneHelper.Core.Geo
{
    /// <summary>
    /// Resolves the local player's approximate location (lat/lon) once, either from configured
    /// coordinates or by looking up the machine's public IP against the local GeoLite2 City db.
    /// Used to judge how far away a game server is (VPN/proxy heuristic).
    /// </summary>
    public sealed class HomeLocator
    {
        public double? Latitude { get; private set; }
        public double? Longitude { get; private set; }
        public bool Known => Latitude.HasValue && Longitude.HasValue;

        public void Resolve(double? cfgLat, double? cfgLon, bool auto, string publicIpUrl,
            GeoIpResolver geo, Action<string> log)
        {
            if (cfgLat.HasValue && cfgLon.HasValue)
            {
                Latitude = cfgLat; Longitude = cfgLon;
                log?.Invoke($"[home] using configured location {cfgLat},{cfgLon}");
                return;
            }
            if (!auto || geo == null) return;

            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                string ip;
                using (var wc = new WebClient())
                    ip = wc.DownloadString(publicIpUrl).Trim();

                var info = geo.Resolve(ip);
                if (info?.Latitude != null && info.Longitude != null)
                {
                    Latitude = info.Latitude; Longitude = info.Longitude;
                    log?.Invoke($"[home] resolved from public IP {ip} -> {info.City}, {info.CountryIso} ({info.Latitude},{info.Longitude})");
                }
                else
                {
                    log?.Invoke("[home] public IP not mappable to coordinates; distance VPN check disabled.");
                }
            }
            catch (Exception ex) { log?.Invoke($"[home] resolve failed: {ex.Message}"); }
        }

        /// <summary>Great-circle distance in km between two points (haversine).</summary>
        public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371.0;
            double dLat = ToRad(lat2 - lat1), dLon = ToRad(lon2 - lon1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static double ToRad(double d) => d * Math.PI / 180.0;
    }
}
