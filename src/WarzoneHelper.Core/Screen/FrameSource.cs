using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WarzoneHelper.Core.Screen
{
    /// <summary>A captured game frame plus the source geometry it came from.</summary>
    public sealed class Frame : IDisposable
    {
        public Bitmap Bitmap;
        public int Width => Bitmap?.Width ?? 0;
        public int Height => Bitmap?.Height ?? 0;
        public void Dispose() => Bitmap?.Dispose();
    }

    public interface IFrameSource : IDisposable
    {
        /// <summary>Grab a frame, or null if none is available this tick.</summary>
        Frame Capture();
    }

    /// <summary>
    /// GDI capture of the game window by PID. Works for windowed / borderless-fullscreen.
    /// Note: true exclusive-fullscreen returns black under GDI — in the Overwolf app we instead
    /// push frames from overwolf.media.getScreenshotUrl (see PushedFrameSource).
    /// </summary>
    public sealed class GdiWindowFrameSource : IFrameSource
    {
        private readonly Func<IEnumerable<int>> _pids;

        public GdiWindowFrameSource(Func<IEnumerable<int>> pids) { _pids = pids; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        public Frame Capture()
        {
            var hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return null;
            if (!GetWindowRect(hwnd, out var r)) return null;

            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return null;

            try
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                return new Frame { Bitmap = bmp };
            }
            catch { return null; }
        }

        private IntPtr FindGameWindow()
        {
            var pids = new HashSet<int>(_pids?.Invoke() ?? Array.Empty<int>());
            if (pids.Count == 0) return IntPtr.Zero;

            // Only capture when the game window is actually topmost (foreground). We deliberately do
            // NOT fall back to the game's MainWindowHandle: CopyFromScreen grabs whatever is at those
            // screen coordinates, so an unfocused game would make us OCR the desktop / other apps and
            // emit garbage chat/HUD events.
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || !IsWindowVisible(fg)) return IntPtr.Zero;
            GetWindowThreadProcessId(fg, out var fpid);
            return pids.Contains((int)fpid) ? fg : IntPtr.Zero;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Frame source fed externally (e.g. Overwolf JS decoding an in-memory screenshot and
    /// pushing raw pixels/PNG bytes into the plugin). Thread-safe single-slot buffer.
    /// </summary>
    public sealed class PushedFrameSource : IFrameSource
    {
        private readonly object _lock = new object();
        private Bitmap _pending;

        public void Push(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0) return;
            try
            {
                using (var ms = new System.IO.MemoryStream(imageBytes))
                {
                    var bmp = new Bitmap(ms);
                    lock (_lock) { _pending?.Dispose(); _pending = new Bitmap(bmp); }
                }
            }
            catch { }
        }

        public Frame Capture()
        {
            lock (_lock)
            {
                if (_pending == null) return null;
                var f = new Frame { Bitmap = _pending };
                _pending = null;
                return f;
            }
        }

        public void Dispose() { lock (_lock) { _pending?.Dispose(); _pending = null; } }
    }
}
