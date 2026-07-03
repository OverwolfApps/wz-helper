using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using GameHelper.Core.Net;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// Optional online display-name validator using Activision's own endpoint
    /// (profile.callofduty.com/cod/checkUsername) — the authoritative format check the site uses
    /// (display names aren't unique, so it validates shape, not availability; e.g. "bist gut
    /// genuuug" returns "valid"). The endpoint is Akamai bot-protected, so we bootstrap a session
    /// cookie with a GET, then POST the form the site does. Results are cached; a failed/blocked
    /// call returns null (unknown) so it never false-rejects. Off by default — see
    /// WarzoneConfig.VerifyUsernamesOnline (external calls, rate-limit/anti-bot risk).
    /// </summary>
    public sealed class CodUsernameVerifier
    {
        private const string Origin = "https://profile.callofduty.com";
        private const string CheckUrl = Origin + "/cod/checkUsername";
        private const string BootstrapUrl = Origin + "/cod/login";

        private readonly HttpClient _http;
        private readonly Action<string> _log;
        private readonly ConcurrentDictionary<string, bool> _cache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private int _bootstrapped;

        public CodUsernameVerifier(Action<string> log = null)
        {
            _log = log;
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", HttpDefaults.ChromeUserAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        }

        /// <summary>true = Activision accepts the name's format, false = it rejects it,
        /// null = unknown (network/anti-bot error — never treat as invalid).</summary>
        public bool? Check(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            if (_cache.TryGetValue(username, out var cached)) return cached;
            try
            {
                Bootstrap();
                var req = new HttpRequestMessage(HttpMethod.Post, CheckUrl)
                {
                    Content = new StringContent("username=" + Uri.EscapeDataString(username),
                        Encoding.UTF8, "application/x-www-form-urlencoded")
                };
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                req.Headers.TryAddWithoutValidation("Origin", Origin);
                req.Headers.TryAddWithoutValidation("Referer", Origin + "/cod/profile");

                using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                {
                    var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var status = JObject.Parse(text).Value<string>("status");
                    if (string.IsNullOrEmpty(status)) return null;   // unexpected shape -> unknown
                    bool valid = status.Equals("valid", StringComparison.OrdinalIgnoreCase);
                    _cache[username] = valid;
                    return valid;
                }
            }
            catch (Exception ex) { _log?.Invoke($"[username] check failed for '{username}': {ex.Message}"); return null; }
        }

        private void Bootstrap()
        {
            if (Interlocked.CompareExchange(ref _bootstrapped, 1, 0) != 0) return;
            try { using (_http.GetAsync(BootstrapUrl).GetAwaiter().GetResult()) { } }
            catch { /* cookie may still work / retried implicitly next call keeps flag set */ }
        }
    }
}
