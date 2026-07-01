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
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": if (i + 1 < args.Length) configPath = args[++i]; break;
                    case "--quiet": quietLog = true; break;
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

            core.Bus.OnEvent += evt => Console.Out.WriteLine(evt.ToJson());
            if (!quietLog) core.Bus.OnLog += msg => Console.Error.WriteLine("[log] " + msg);

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => stop.Set();

            Console.Error.WriteLine("Warzone Helper (standalone). Press Ctrl+C to stop.");
            core.Start();
            stop.Wait();
            core.Stop();
            return 0;
        }

        private static void PrintHelp()
        {
            Console.Error.WriteLine(
@"Warzone Helper - standalone monitor
Usage:
  WarzoneHelper.Console.exe [--config <path>] [--quiet]
  WarzoneHelper.Console.exe --write-default-config [path]

Outputs one JSON event per line on stdout. Run elevated to enable UDP
game-server detection (ETW). Options are also documented in README.md.");
        }
    }
}
