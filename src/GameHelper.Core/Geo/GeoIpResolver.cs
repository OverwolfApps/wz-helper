using System;
using System.Collections.Generic;
using System.Net;
using MaxMind.Db;

namespace GameHelper.Core.Geo
{
    public sealed class GeoInfo
    {
        public string CountryIso;
        public string CountryName;
        public string City;
        public double? Latitude;
        public double? Longitude;
        public long? AsnNumber;
        public string AsnOrg;

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "countryIso", CountryIso },
                { "countryName", CountryName },
                { "city", City },
                { "latitude", Latitude },
                { "longitude", Longitude },
                { "asn", AsnNumber },
                { "asnOrg", AsnOrg }
            };
        }
    }

    /// <summary>
    /// Local, offline GeoIP resolution using MaxMind.Db against GeoLite2 mmdb files.
    /// Prefers City (has lat/lon + country); falls back to Country db; ASN db adds org.
    /// </summary>
    public sealed class GeoIpResolver : IDisposable
    {
        private Reader _city;
        private Reader _country;
        private Reader _asn;
        private readonly object _lock = new object();

        public bool Available => _city != null || _country != null;

        public void Load(string dir, bool autoDownload, Action<string> log = null)
        {
            lock (_lock)
            {
                var cityPath = autoDownload
                    ? MaxMindDownloader.Ensure(dir, MaxMindDownloader.City, log)
                    : System.IO.Path.Combine(dir, MaxMindDownloader.City.FileName);
                var countryPath = autoDownload
                    ? MaxMindDownloader.Ensure(dir, MaxMindDownloader.Country, log)
                    : System.IO.Path.Combine(dir, MaxMindDownloader.Country.FileName);
                var asnPath = autoDownload
                    ? MaxMindDownloader.Ensure(dir, MaxMindDownloader.Asn, log)
                    : System.IO.Path.Combine(dir, MaxMindDownloader.Asn.FileName);

                _city = TryOpen(cityPath, log);
                _country = TryOpen(countryPath, log);
                _asn = TryOpen(asnPath, log);
            }
        }

        private static Reader TryOpen(string path, Action<string> log)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    return new Reader(path, FileAccessMode.Memory);
            }
            catch (Exception ex) { log?.Invoke($"[geoip] open failed {path}: {ex.Message}"); }
            return null;
        }

        public GeoInfo Resolve(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return null;
            if (!IPAddress.TryParse(ip, out var addr)) return null;
            if (IsPrivate(addr)) return new GeoInfo { CountryIso = "LAN", CountryName = "Local network" };

            var info = new GeoInfo();
            lock (_lock)
            {
                try
                {
                    if (_city != null)
                    {
                        var rec = _city.Find<Dictionary<string, object>>(addr);
                        FillFromCity(info, rec);
                    }
                    else if (_country != null)
                    {
                        var rec = _country.Find<Dictionary<string, object>>(addr);
                        FillCountry(info, rec);
                    }

                    if (_asn != null)
                    {
                        var rec = _asn.Find<Dictionary<string, object>>(addr);
                        if (rec != null)
                        {
                            if (rec.TryGetValue("autonomous_system_number", out var n) && n != null)
                                info.AsnNumber = Convert.ToInt64(n);
                            if (rec.TryGetValue("autonomous_system_organization", out var org))
                                info.AsnOrg = org?.ToString();
                        }
                    }
                }
                catch { /* unmapped IP */ }
            }
            return info;
        }

        private static void FillCountry(GeoInfo info, Dictionary<string, object> rec)
        {
            if (rec == null) return;
            if (rec.TryGetValue("country", out var c) && c is Dictionary<string, object> country)
            {
                if (country.TryGetValue("iso_code", out var iso)) info.CountryIso = iso?.ToString();
                if (country.TryGetValue("names", out var namesObj) &&
                    namesObj is Dictionary<string, object> names &&
                    names.TryGetValue("en", out var en))
                    info.CountryName = en?.ToString();
            }
        }

        private static void FillFromCity(GeoInfo info, Dictionary<string, object> rec)
        {
            if (rec == null) return;
            FillCountry(info, rec);
            if (rec.TryGetValue("city", out var cityObj) && cityObj is Dictionary<string, object> city &&
                city.TryGetValue("names", out var cn) && cn is Dictionary<string, object> cnames &&
                cnames.TryGetValue("en", out var cen))
                info.City = cen?.ToString();
            if (rec.TryGetValue("location", out var locObj) && locObj is Dictionary<string, object> loc)
            {
                if (loc.TryGetValue("latitude", out var lat) && lat != null)
                    info.Latitude = Convert.ToDouble(lat);
                if (loc.TryGetValue("longitude", out var lon) && lon != null)
                    info.Longitude = Convert.ToDouble(lon);
            }
        }

        public static bool IsPrivate(IPAddress addr)
        {
            var b = addr.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] == 10) return true;
                if (b[0] == 127) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT
            }
            return false;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _city?.Dispose(); _country?.Dispose(); _asn?.Dispose();
                _city = _country = _asn = null;
            }
        }
    }
}
