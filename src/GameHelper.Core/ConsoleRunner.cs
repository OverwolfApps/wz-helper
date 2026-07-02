using System;
using System.IO;
using System.Linq;
using System.Threading;
using GameHelper.Core.Config;

namespace GameHelper.Core
{
    /// <summary>
    /// Standalone entry logic, kept in Core so it can be driven either by the accompanying
    /// console EXE or by a generic managed DLL runner (e.g. dllrun) that invokes Run().
    /// Emits one JSON line per event to stdout; logs to stderr.
    /// </summary>
    public static class ConsoleRunner
    {
        public static int Run(IGameProfile profile, string[] args)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string configPath = null;
            bool quietLog = false;
            string logFileOverride = null;
            bool disableLogFile = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": if (i + 1 < args.Length) configPath = args[++i]; break;
                    case "--quiet": quietLog = true; break;
                    case "--logfile": if (i + 1 < args.Length) logFileOverride = args[++i]; break;
                    case "--no-logfile": disableLogFile = true; break;
                    case "--write-default-config":
                        var p = i + 1 < args.Length ? args[++i] : "config.json";
                        profile.CreateDefaultConfig().Save(p);
                        Console.Error.WriteLine($"Wrote default config to {p}");
                        return 0;
                    case "--geo":
                        var targets = new System.Collections.Generic.List<string>();
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) targets.Add(args[++i]);
                        return RunGeo(profile, targets, configPath);
                    case "--tail":
                        string tailArg = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : null;
                        return RunTail(profile, tailArg, configPath);
                    case "-h":
                    case "--help":
                        PrintHelp();
                        return 0;
                }
            }

            // Default to %APPDATA%\GameHelper\settings.jsonc, auto-creating it with commented
            // defaults so all tunables (regions, thresholds, ...) are editable without recompiling.
            if (string.IsNullOrEmpty(configPath)) configPath = HelperConfig.DefaultConfigPath(profile.Name);
            var cfg = HelperConfig.LoadOrCreate(HelperConfig.Expand(configPath), profile.CreateDefaultConfig, Console.Error.WriteLine);
            var core = new HelperCore(cfg, profile);

            // Resolve the durable event log path: CLI override > config > (disabled).
            RollingFileSink events = null, diag = null;
            if (!disableLogFile)
            {
                var eventPath = HelperConfig.ExpandTokens(logFileOverride ?? cfg.EventLogFile);
                if (!string.IsNullOrEmpty(eventPath))
                {
                    try
                    {
                        events = new RollingFileSink(eventPath, cfg.LogRotateMB);
                        var diagPath = HelperConfig.ExpandTokens(cfg.DiagLogFile)
                            ?? Path.Combine(Path.GetDirectoryName(eventPath) ?? ".",
                                Path.GetFileNameWithoutExtension(eventPath) + ".log");
                        diag = new RollingFileSink(diagPath, cfg.LogRotateMB);
                        Console.Error.WriteLine($"[log] writing events -> {eventPath}");
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[log] file logging disabled: {ex.Message}"); }
                }
            }

            // Declare the WS server up front, but wire the event/log handlers BEFORE starting it so
            // its "[ws] listening on ..." line reaches the log file (and not just stderr, which a
            // headless scheduled task discards).
            GameHelper.Core.Net.EventWebSocketServer ws = null;

            core.Bus.OnEvent += evt =>
            {
                var json = evt.ToJson();
                Console.Out.WriteLine(json);
                events?.WriteLine(json);
                ws?.Broadcast(json);
            };
            core.Bus.OnLog += msg =>
            {
                if (!quietLog) Console.Error.WriteLine("[log] " + msg);
                diag?.WriteLine($"{DateTime.UtcNow:o} {msg}");
            };

            if (cfg.EnableWebSocket)
            {
                bool loggedFrame = false;
                ws = new GameHelper.Core.Net.EventWebSocketServer(
                    cfg.WebSocketHost, cfg.WebSocketPort, core.Bus.Log,
                    (name, data) => core.ReportGepEvent(name, data),
                    frameBytes =>
                    {
                        if (!loggedFrame) { loggedFrame = true; core.Bus.Log($"[frame] receiving game frames from Overwolf ({frameBytes.Length} bytes)"); }
                        core.PushFrame(frameBytes);
                    });
                ws.Start();
            }

            var stop = new ManualResetEventSlim(false);
            var stopped = new ManualResetEventSlim(false);

            // Ctrl+C / Ctrl+Break.
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            // Console window close [X], log-off, shutdown — CancelKeyPress does NOT catch these, so
            // we install a native console control handler and block it until the graceful stop (and
            // its HELPER_STOPPED event + flush) completes, within the short window the OS allows.
            // NOTE: a hard kill (taskkill /F, Stop-ScheduledTask) terminates the process outright —
            // no in-process code runs, so no HELPER_STOPPED there. The Overwolf app detects that via
            // the WebSocket onclose instead.
            _ctrlHandler = sig =>
            {
                stop.Set();
                stopped.Wait(4000);
                return true;
            };
            SetConsoleCtrlHandler(_ctrlHandler, true);
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { stop.Set(); stopped.Wait(4000); };

            Console.Error.WriteLine("Game Helper (standalone). Press Ctrl+C to stop.");
            core.Start();
            stop.Wait();
            core.Stop();               // publishes HELPER_STOPPED
            ws?.Dispose();
            events?.Dispose();
            diag?.Dispose();
            stopped.Set();
            return 0;
        }

        // Keep a reference so the delegate isn't garbage-collected while registered.
        private static ConsoleCtrlDelegate _ctrlHandler;
        private delegate bool ConsoleCtrlDelegate(uint ctrlType);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        /// <summary>
        /// Follow the agent's event log (tail -f). With no argument, follows the newest
        /// events_*.ndjson in the configured log directory and auto-switches when the agent restarts
        /// (new {unixtime} file) or rotates the file. Pass a file or directory to override.
        /// </summary>
        private static int RunTail(IGameProfile profile, string arg, string configPath)
        {
            var cfg = string.IsNullOrEmpty(configPath) ? profile.CreateDefaultConfig() : HelperConfig.Load(configPath, profile.CreateDefaultConfig);

            string dir, pattern;
            if (!string.IsNullOrEmpty(arg) && File.Exists(arg))
            {
                dir = Path.GetDirectoryName(Path.GetFullPath(arg));
                pattern = Path.GetFileName(arg);
            }
            else if (!string.IsNullOrEmpty(arg) && Directory.Exists(arg))
            {
                dir = arg; pattern = "events_*.ndjson";
            }
            else
            {
                var tmpl = HelperConfig.Expand(cfg.EventLogFile); // env vars only, keep tokens
                dir = Path.GetDirectoryName(tmpl);
                pattern = Path.GetFileName(tmpl).Replace("{unixtime}", "*").Replace("{pid}", "*");
            }

            Console.Error.WriteLine($"[tail] following {Path.Combine(dir ?? ".", pattern)} — Ctrl+C to stop.");

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };

            string current = null;
            long pos = 0;
            var lastScan = DateTime.MinValue;

            while (!stop.IsSet)
            {
                try
                {
                    // Every ~1.5s, check whether a newer matching file has appeared.
                    if ((DateTime.UtcNow - lastScan).TotalMilliseconds > 1500)
                    {
                        lastScan = DateTime.UtcNow;
                        var newest = Directory.Exists(dir)
                            ? new DirectoryInfo(dir).GetFiles(pattern)
                                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                            : null;
                        if (newest != null && !string.Equals(newest.FullName, current, StringComparison.OrdinalIgnoreCase))
                        {
                            current = newest.FullName;
                            pos = 0; // start of the new file
                            Console.Error.WriteLine($"[tail] --> {current}");
                        }
                    }

                    if (current == null || !File.Exists(current)) { stop.Wait(500); continue; }

                    using (var fs = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length < pos) pos = 0; // truncated / rotated
                        fs.Seek(pos, SeekOrigin.Begin);
                        using (var sr = new StreamReader(fs))
                        {
                            string line;
                            while ((line = sr.ReadLine()) != null)
                                if (line.Length > 0) Console.Out.WriteLine(line);
                            pos = fs.Position;
                        }
                    }
                }
                catch { /* transient IO; retry next tick */ }
                stop.Wait(400);
            }
            return 0;
        }

        /// <summary>Resolve a list of IPs/hostnames to GeoLite2 geo + ASN and print them, then exit.</summary>
        private static int RunGeo(IGameProfile profile, System.Collections.Generic.List<string> targets, string configPath)
        {
            var cfg = string.IsNullOrEmpty(configPath) ? profile.CreateDefaultConfig() : HelperConfig.Load(configPath, profile.CreateDefaultConfig);
            using (var geo = new GameHelper.Core.Geo.GeoIpResolver())
            {
                geo.Load(cfg.ExpandedGeoDbDir(), cfg.AutoDownloadGeoDb, m => Console.Error.WriteLine("[geo] " + m));
                foreach (var t in targets)
                {
                    var ip = t;
                    if (!System.Net.IPAddress.TryParse(t, out _))
                    {
                        try
                        {
                            var addrs = System.Net.Dns.GetHostAddresses(t);
                            foreach (var a in addrs)
                                if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { ip = a.ToString(); break; }
                        }
                        catch { Console.WriteLine($"{t,-42} (DNS resolve failed)"); continue; }
                    }
                    var info = geo.Resolve(ip);
                    if (info == null) { Console.WriteLine($"{t,-42} {ip,-16} (no data)"); continue; }
                    var loc = string.Join(", ", new[] { info.City, info.CountryName ?? info.CountryIso }
                        .Where(s => !string.IsNullOrEmpty(s)));
                    Console.WriteLine($"{t,-42} {ip,-16} {loc}  | ASN {info.AsnNumber} {info.AsnOrg}");
                }
            }
            return 0;
        }

        private static void PrintHelp()
        {
            Console.Error.WriteLine(
@"Game Helper - standalone monitor
Usage:
  GameHelper.Console.exe [--config <path>] [--quiet]
                            [--logfile <path> | --no-logfile]
  GameHelper.Console.exe --tail [<file|dir>]        follow the event log (tail -f)
  GameHelper.Console.exe --geo <ip|host> ...        geolocate endpoints and exit
  GameHelper.Console.exe --write-default-config [path]

Outputs one JSON event per line on stdout, and (by default) appends events to
%LOCALAPPDATA%\GameHelper\logs\events.ndjson with a sibling .log for diagnostics.
  --logfile <path>   override the event log path
  --no-logfile       disable file logging entirely
  --quiet            suppress [log] lines on stderr (still written to the .log file)

Run elevated to enable UDP game-server detection (ETW). See README.md.");
        }
    }
}
