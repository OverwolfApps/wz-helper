using System;
using System.IO;
using System.Runtime.InteropServices;
using GameHelper.Core;
using GameHelper.Core.Config;
using WarzoneHelper.Game;

namespace overwolf.plugins.gamehelper
{
    /// <summary>
    /// Overwolf plugin surface for Game Helper. Declared in manifest.json as an extra-object:
    ///
    ///   "extra-objects": {
    ///     "game-helper": {
    ///       "file": "plugins/GameHelper.Plugin.dll",
    ///       "class": "overwolf.plugins.gamehelper.GameHelperPlugin"
    ///     }
    ///   }
    ///
    /// All work happens in GameHelper.Core driven by a game profile (Warzone here); this is a thin
    /// bridge. Events are surfaced to JS as (eventName, jsonPayload). Every public method is
    /// async-callback style per OW best practice.
    /// </summary>
    [ComVisible(true)]
    public class GameHelperPlugin
    {
        private HelperCore _core;
        private readonly IGameProfile _profile = new WarzoneProfile();
        private readonly object _lock = new object();

        /// <summary>Fires for every helper event: (string eventName, string jsonPayload).</summary>
        public event Action<object, object> onEvent;

        /// <summary>Diagnostic log line: (string message, object _).</summary>
        public event Action<object, object> onLog;

        // Overwolf instantiates with either an empty ctor or (int hostWindowHandle).
        public GameHelperPlugin() { }
        public GameHelperPlugin(int hostWindowHandle) { }

        /// <summary>Start monitoring. configPath may be null to use defaults / config.json beside the DLL.</summary>
        public void start(string configPath, Action<object> callback)
        {
            try
            {
                lock (_lock)
                {
                    if (_core != null) { Ok(callback, "already-running"); return; }

                    var cfg = LoadConfig(configPath);
                    _core = new HelperCore(cfg, _profile);
                    _core.Bus.OnEvent += evt =>
                    {
                        try { onEvent?.Invoke(evt.Name, evt.ToJson()); } catch { }
                    };
                    _core.Bus.OnLog += msg =>
                    {
                        try { onLog?.Invoke(msg, null); } catch { }
                    };
                    _core.Start();
                }
                Ok(callback, "started");
            }
            catch (Exception ex) { Fail(callback, ex.Message); }
        }

        public void stop(Action<object> callback)
        {
            try
            {
                lock (_lock) { _core?.Stop(); _core = null; }
                Ok(callback, "stopped");
            }
            catch (Exception ex) { Fail(callback, ex.Message); }
        }

        /// <summary>Push a frame captured by Overwolf (base64 PNG/JPG from getScreenshotUrl).</summary>
        public void pushFrame(string base64Image, Action<object> callback)
        {
            try
            {
                var bytes = Convert.FromBase64String(StripDataUri(base64Image));
                lock (_lock) { _core?.PushFrame(bytes); }
                Ok(callback, "ok");
            }
            catch (Exception ex) { Fail(callback, ex.Message); }
        }

        /// <summary>Relay an Overwolf GEP event as a low-confidence hint.</summary>
        public void reportGepEvent(string name, string data, Action<object> callback)
        {
            try { lock (_lock) { _core?.ReportGepEvent(name, data); } Ok(callback, "ok"); }
            catch (Exception ex) { Fail(callback, ex.Message); }
        }

        public void getDefaultConfig(Action<object> callback)
        {
            try { Ok(callback, Newtonsoft.Json.JsonConvert.SerializeObject(_profile.CreateDefaultConfig())); }
            catch (Exception ex) { Fail(callback, ex.Message); }
        }

        private HelperConfig LoadConfig(string configPath)
        {
            if (string.IsNullOrEmpty(configPath))
            {
                var beside = Path.Combine(
                    Path.GetDirectoryName(typeof(GameHelperPlugin).Assembly.Location) ?? ".",
                    "config.json");
                return HelperConfig.Load(beside, _profile.CreateDefaultConfig);
            }
            return HelperConfig.Load(configPath, _profile.CreateDefaultConfig);
        }

        private static string StripDataUri(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var idx = s.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? s.Substring(idx + 7) : s;
        }

        private static void Ok(Action<object> cb, string msg)
        {
            try { cb?.Invoke("{\"status\":\"success\",\"message\":\"" + msg + "\"}"); } catch { }
        }
        private static void Fail(Action<object> cb, string msg)
        {
            try { cb?.Invoke("{\"status\":\"error\",\"error\":\"" + msg.Replace("\"", "'") + "\"}"); } catch { }
        }
    }
}
