using System;
using System.IO;
using System.Threading;
using WarzoneHelper.Core.Config;

namespace WarzoneHelper.Core
{
    /// <summary>
    /// Standalone entry logic, kept in Core so it can be driven either by the accompanying
    /// console EXE or by a generic managed DLL runner (e.g. dllrun) that invokes Run().
    /// Emits one JSON line per event to stdout; logs to stderr.
    /// </summary>
    public static class ConsoleRunner
    {
        public static int Run(string[] args)
        {
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
                        new HelperConfig().Save(p);
                        Console.Error.WriteLine($"Wrote default config to {p}");
                        return 0;
                    case "-h":
                    case "--help":
                        PrintHelp();
                        return 0;
                }
            }

            var cfg = string.IsNullOrEmpty(configPath) ? new HelperConfig() : HelperConfig.Load(configPath);
            var core = new HelperCore(cfg);

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

            core.Bus.OnEvent += evt =>
            {
                var json = evt.ToJson();
                Console.Out.WriteLine(json);
                events?.WriteLine(json);
            };
            core.Bus.OnLog += msg =>
            {
                if (!quietLog) Console.Error.WriteLine("[log] " + msg);
                diag?.WriteLine($"{DateTime.UtcNow:o} {msg}");
            };

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => stop.Set();

            Console.Error.WriteLine("Warzone Helper (standalone). Press Ctrl+C to stop.");
            core.Start();
            stop.Wait();
            core.Stop();
            events?.Dispose();
            diag?.Dispose();
            return 0;
        }

        private static void PrintHelp()
        {
            Console.Error.WriteLine(
@"Warzone Helper - standalone monitor
Usage:
  WarzoneHelper.Console.exe [--config <path>] [--quiet]
                            [--logfile <path> | --no-logfile]
  WarzoneHelper.Console.exe --write-default-config [path]

Outputs one JSON event per line on stdout, and (by default) appends events to
%LOCALAPPDATA%\WarzoneHelper\logs\events.ndjson with a sibling .log for diagnostics.
  --logfile <path>   override the event log path
  --no-logfile       disable file logging entirely
  --quiet            suppress [log] lines on stderr (still written to the .log file)

Run elevated to enable UDP game-server detection (ETW). See README.md.");
        }
    }
}
