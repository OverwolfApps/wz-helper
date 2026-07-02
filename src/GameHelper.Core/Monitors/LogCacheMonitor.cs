using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GameHelper.Core.Config;
using GameHelper.Core.Events;

namespace GameHelper.Core.Monitors
{
    /// <summary>
    /// Watches Warzone log/cache directories with FileSystemWatcher and emits
    /// LOG_FILE_CHANGED / CACHE_CHANGED (debounced). For .log/.txt files it also tails
    /// newly appended lines so consumers can react to log content, not just file mtime.
    /// </summary>
    public sealed class LogCacheMonitor : IMonitor
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly ConcurrentDictionary<string, DateTime> _lastFired = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, long> _offsets = new ConcurrentDictionary<string, long>();

        public string Name => "logwatch";

        public LogCacheMonitor(HelperConfig cfg, EventBus bus) { _cfg = cfg; _bus = bus; }

        public void Start()
        {
            foreach (var path in _cfg.ExpandedWatchPaths())
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        _bus.Log($"[logwatch] skip (missing): {path}");
                        continue;
                    }
                    var w = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                                       NotifyFilters.Size | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true
                    };
                    w.Changed += OnChanged;
                    w.Created += OnChanged;
                    w.Renamed += OnRenamed;
                    _watchers.Add(w);
                    _bus.Log($"[logwatch] watching {path}");
                }
                catch (Exception ex) { _bus.Log($"[logwatch] failed {path}: {ex.Message}"); }
            }
        }

        private bool Matches(string file)
        {
            var filters = _cfg.WatchFilters;
            if (filters == null || filters.Length == 0) return true;
            var name = Path.GetFileName(file);
            foreach (var f in filters)
            {
                var ext = f.TrimStart('*');
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private bool Debounced(string file)
        {
            var now = DateTime.UtcNow;
            if (_lastFired.TryGetValue(file, out var last) &&
                (now - last).TotalMilliseconds < _cfg.WatchDebounceMs)
                return true;
            _lastFired[file] = now;
            return false;
        }

        private void OnRenamed(object sender, RenamedEventArgs e) => OnChanged(sender, e);

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (Directory.Exists(e.FullPath)) return;
                if (!Matches(e.FullPath)) return;
                if (Debounced(e.FullPath)) return;

                bool isCache = e.FullPath.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0;
                var appended = new List<string>(TailNewLines(e.FullPath));

                _bus.Publish(new HelperEvent(isCache ? EventNames.CacheChanged : EventNames.LogFileChanged,
                        EventSource.FileWatch)
                    .With("path", e.FullPath)
                    .With("changeType", e.ChangeType.ToString())
                    .With("appendedLineCount", appended.Count));

                // Emit one event per appended line so consumers can pattern-match log content.
                foreach (var line in appended)
                {
                    _bus.Publish(EventNames.LogFileChanged, EventSource.FileWatch, x => x
                        .With("path", e.FullPath).With("line", line));
                }
            }
            catch (Exception ex) { _bus.Log($"[logwatch] {ex.Message}"); }
        }

        /// <summary>Reads bytes appended since we last saw this file. Empty for non-text/binary churn.</summary>
        private IEnumerable<string> TailNewLines(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".log" && ext != ".txt" && ext != ".ndjson") yield break;

            List<string> lines = new List<string>();
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long prev = _offsets.TryGetValue(file, out var o) ? o : fs.Length;
                    if (fs.Length < prev) prev = 0; // truncated/rotated
                    fs.Seek(prev, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs))
                    {
                        string l;
                        while ((l = sr.ReadLine()) != null)
                            if (l.Length > 0) lines.Add(l);
                    }
                    _offsets[file] = fs.Length;
                }
            }
            catch { yield break; }

            foreach (var l in lines) yield return l;
        }

        public void Stop()
        {
            foreach (var w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();
        }

        public void Dispose() => Stop();
    }
}
