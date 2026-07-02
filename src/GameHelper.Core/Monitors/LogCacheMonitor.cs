using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GameHelper.Core.Config;
using GameHelper.Core.Events;

namespace GameHelper.Core.Monitors
{
    /// <summary>
    /// Watches the configured log/cache directories with FileSystemWatcher and emits generic,
    /// game-agnostic events: LOG_FILE_ADDED / LOG_FILE_REMOVED when watched files appear/disappear,
    /// and LOG_LINE_ADDED { path, line } for each newly appended (non-empty) line. When the config
    /// supplies LogLinePatterns, the first regex that matches a line contributes its named capture
    /// groups (timestamp, level, message, ...) as fields on the event, so consumers get pre-parsed
    /// lines. Game profiles decide which directories/filters/patterns apply.
    /// </summary>
    public sealed class LogCacheMonitor : IMonitor
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly ConcurrentDictionary<string, DateTime> _lastFired = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, long> _offsets = new ConcurrentDictionary<string, long>();
        private Regex[] _linePatterns = Array.Empty<Regex>();

        public string Name => "logwatch";

        public LogCacheMonitor(HelperConfig cfg, EventBus bus) { _cfg = cfg; _bus = bus; }

        public void Start()
        {
            _linePatterns = (_cfg.LogLinePatterns ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p =>
                {
                    try { return new Regex(p, RegexOptions.Compiled); }
                    catch (Exception ex) { _bus.Log($"[logwatch] bad LogLinePattern '{p}': {ex.Message}"); return null; }
                })
                .Where(r => r != null).ToArray();

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
                    w.Created += OnCreated;
                    w.Deleted += OnDeleted;
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

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (Directory.Exists(e.FullPath) || !Matches(e.FullPath)) return;
                _bus.Publish(EventNames.LogFileAdded, EventSource.FileWatch, x => x.With("path", e.FullPath));
                _offsets[e.FullPath] = 0;          // tail this new file from the start
                EmitAppendedLines(e.FullPath);
            }
            catch (Exception ex) { _bus.Log($"[logwatch] {ex.Message}"); }
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (!Matches(e.FullPath)) return;
                _bus.Publish(EventNames.LogFileRemoved, EventSource.FileWatch, x => x.With("path", e.FullPath));
                _offsets.TryRemove(e.FullPath, out _);
                _lastFired.TryRemove(e.FullPath, out _);
            }
            catch (Exception ex) { _bus.Log($"[logwatch] {ex.Message}"); }
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                if (Matches(e.OldFullPath))
                {
                    _bus.Publish(EventNames.LogFileRemoved, EventSource.FileWatch, x => x.With("path", e.OldFullPath));
                    _offsets.TryRemove(e.OldFullPath, out _);
                    _lastFired.TryRemove(e.OldFullPath, out _);
                }
                if (!Directory.Exists(e.FullPath) && Matches(e.FullPath))
                {
                    _bus.Publish(EventNames.LogFileAdded, EventSource.FileWatch, x => x.With("path", e.FullPath));
                    _offsets[e.FullPath] = 0;
                    EmitAppendedLines(e.FullPath);
                }
            }
            catch (Exception ex) { _bus.Log($"[logwatch] {ex.Message}"); }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (Directory.Exists(e.FullPath) || !Matches(e.FullPath)) return;
                if (Debounced(e.FullPath)) return;
                EmitAppendedLines(e.FullPath);
            }
            catch (Exception ex) { _bus.Log($"[logwatch] {ex.Message}"); }
        }

        /// <summary>Emit one LOG_LINE_ADDED per newly appended, non-empty line (with parsed groups).</summary>
        private void EmitAppendedLines(string path)
        {
            foreach (var line in TailNewLines(path))
            {
                var evt = new HelperEvent(EventNames.LogLineAdded, EventSource.FileWatch)
                    .With("path", path).With("line", line);
                foreach (var re in _linePatterns)
                {
                    var m = re.Match(line);
                    if (!m.Success) continue;
                    foreach (var g in re.GetGroupNames())
                        if (!int.TryParse(g, out _))          // named groups only (skip numeric indices)
                        {
                            var grp = m.Groups[g];
                            if (grp.Success) evt.With(g, grp.Value);
                        }
                    break;                                    // first matching pattern wins
                }
                _bus.Publish(evt);
            }
        }

        /// <summary>Reads text lines appended since we last saw this file. Empty for non-text/binary churn.</summary>
        private IEnumerable<string> TailNewLines(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".log" && ext != ".txt" && ext != ".ndjson") yield break;

            var lines = new List<string>();
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
                            if (!string.IsNullOrWhiteSpace(l)) lines.Add(l);
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
