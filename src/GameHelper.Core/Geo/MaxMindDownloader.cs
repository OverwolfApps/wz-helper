using System;
using System.IO;
using System.Net;

namespace GameHelper.Core.Geo
{
    /// <summary>
    /// Auto-downloads GeoLite2 databases from community mirrors when missing.
    /// Mirrors the approach used by the universal-lookup project (P3TERX GitHub copies),
    /// since official MaxMind downloads now require a license key.
    /// </summary>
    public static class MaxMindDownloader
    {
        public sealed class DbSpec
        {
            public string FileName;
            public string PrimaryUrl;
            public string AltUrl;
        }

        public static readonly DbSpec Country = new DbSpec
        {
            FileName = "GeoLite2-Country.mmdb",
            PrimaryUrl = "https://raw.githubusercontent.com/P3TERX/GeoLite.mmdb/download/GeoLite2-Country.mmdb",
            AltUrl = "https://git.io/GeoLite2-Country.mmdb"
        };

        public static readonly DbSpec City = new DbSpec
        {
            FileName = "GeoLite2-City.mmdb",
            PrimaryUrl = "https://raw.githubusercontent.com/P3TERX/GeoLite.mmdb/download/GeoLite2-City.mmdb",
            AltUrl = "https://git.io/GeoLite2-City.mmdb"
        };

        public static readonly DbSpec Asn = new DbSpec
        {
            FileName = "GeoLite2-ASN.mmdb",
            PrimaryUrl = "https://raw.githubusercontent.com/P3TERX/GeoLite.mmdb/download/GeoLite2-ASN.mmdb",
            AltUrl = "https://git.io/GeoLite2-ASN.mmdb"
        };

        /// <summary>Ensures a db exists at dir/FileName, downloading if needed. Returns the path or null.</summary>
        public static string Ensure(string dir, DbSpec spec, Action<string> log = null)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, spec.FileName);
                if (File.Exists(target) && new FileInfo(target).Length > 0)
                    return target;

                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                foreach (var url in new[] { spec.PrimaryUrl, spec.AltUrl })
                {
                    try
                    {
                        log?.Invoke($"[geoip] downloading {spec.FileName} from {url}");
                        using (var client = new WebClient())
                        {
                            var tmp = target + ".tmp";
                            client.DownloadFile(url, tmp);
                            if (new FileInfo(tmp).Length > 0)
                            {
                                if (File.Exists(target)) File.Delete(target);
                                File.Move(tmp, target);
                                log?.Invoke($"[geoip] saved {target}");
                                return target;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[geoip] download failed ({url}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[geoip] ensure error: {ex.Message}");
            }
            return null;
        }
    }
}
