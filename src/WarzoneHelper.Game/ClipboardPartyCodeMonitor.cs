using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using GameHelper.Core.Events;
using GameHelper.Core.Monitors;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// Polls the Windows clipboard for a copied party code (5 alphanumeric chars that are ALL upper
    /// or ALL lower case — case-uniform, which distinguishes a real code like "LLJGJ" from arbitrary
    /// clipboard text) and emits PARTY_CODE_CHANGED. Catches codes the user copies from the menu even
    /// when the on-screen OCR can't read them. Uses the raw Win32 clipboard API (no STA needed).
    /// </summary>
    public sealed class ClipboardPartyCodeMonitor : IMonitor
    {
        private readonly EventBus _bus;
        private Timer _timer;
        private string _last;
        private static readonly Regex Code = new Regex(@"^[A-Za-z0-9]{5}$", RegexOptions.Compiled);

        public string Name => "clipboard";

        public ClipboardPartyCodeMonitor(EventBus bus) { _bus = bus; }

        public void Start() { _timer = new Timer(_ => Poll(), null, 2000, 1000); }
        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() => Stop();

        private void Poll()
        {
            string t;
            try { t = ReadClipboardText()?.Trim(); } catch { return; }
            if (string.IsNullOrEmpty(t) || !Code.IsMatch(t)) return;
            if (!t.Any(char.IsLetter)) return;                       // require letters (skip plain 5-digit numbers)
            bool caseUniform = t == t.ToUpperInvariant() || t == t.ToLowerInvariant();
            if (!caseUniform) return;                                // party codes are case-uniform
            var code = t.ToUpperInvariant();
            if (code == _last) return;
            _last = code;
            WarzoneEvents.PartyCodeChanged.Emit(_bus, e => e.With("code", code).With("source", "clipboard"));
            _bus.Log($"[clipboard] party code from clipboard: {code}");
        }

        // --- raw Win32 clipboard read (works from any thread; no WinForms/STA dependency) ---
        private const uint CF_UNICODETEXT = 13;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

        private static string ReadClipboardText()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                var h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                var p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }
    }
}
