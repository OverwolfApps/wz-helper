using System;
using System.IO;
using System.Text;

namespace GameHelper.Core
{
    /// <summary>
    /// Minimal append-only text sink with size-based rotation. Thread-safe, auto-flushing, so a
    /// long-running background host (e.g. the elevated scheduled task) keeps a durable log even
    /// with no console attached. On rotation, "file.ndjson" -> "file.1.ndjson" (one backup).
    /// </summary>
    public sealed class RollingFileSink : IDisposable
    {
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly object _lock = new object();
        private StreamWriter _writer;

        public RollingFileSink(string path, int maxMb)
        {
            _path = path;
            _maxBytes = maxMb > 0 ? (long)maxMb * 1024 * 1024 : 0;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Open();
        }

        private void Open()
        {
            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void WriteLine(string line)
        {
            lock (_lock)
            {
                try
                {
                    _writer.WriteLine(line);
                    if (_maxBytes > 0 && _writer.BaseStream.Length >= _maxBytes) Rotate();
                }
                catch { /* never let logging kill the pipeline */ }
            }
        }

        private void Rotate()
        {
            try
            {
                _writer.Dispose();
                var backup = Path.Combine(
                    Path.GetDirectoryName(_path) ?? ".",
                    Path.GetFileNameWithoutExtension(_path) + ".1" + Path.GetExtension(_path));
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(_path, backup);
            }
            catch { }
            finally { Open(); }
        }

        public void Dispose()
        {
            lock (_lock) { try { _writer?.Dispose(); } catch { } _writer = null; }
        }
    }
}
