using System;
using System.Drawing;
using Tesseract;

namespace GameHelper.Core.Screen
{
    /// <summary>
    /// Tesseract-backed OCR. Needs the eng.traineddata file in the configured tessdata dir
    /// (auto-downloaded on first use). If init fails, Available=false and the analyzer
    /// silently skips text-based detection (lobby ID).
    /// </summary>
    public sealed class TesseractOcrEngine : IOcrEngine
    {
        private TesseractEngine _engine;
        private readonly object _lock = new object();
        public bool Available { get; private set; }

        public TesseractOcrEngine(string tessDataDir, Action<string> log = null)
        {
            try
            {
                TessdataDownloader.EnsureEng(tessDataDir, log);
                _engine = new TesseractEngine(tessDataDir, "eng", EngineMode.LstmOnly);
                Available = true;
                log?.Invoke("[ocr] Tesseract ready.");
            }
            catch (Exception ex)
            {
                Available = false;
                log?.Invoke($"[ocr] Tesseract unavailable: {ex.Message}");
            }
        }

        public string Read(Bitmap region, string whitelist = null, bool singleLine = true)
        {
            if (!Available || region == null) return null;
            lock (_lock)
            {
                try
                {
                    if (!string.IsNullOrEmpty(whitelist))
                        _engine.SetVariable("tessedit_char_whitelist", whitelist);
                    else
                        _engine.SetVariable("tessedit_char_whitelist", string.Empty);

                    var mode = singleLine ? PageSegMode.SingleLine : PageSegMode.SingleBlock;
                    using (var pix = PixConverter.ToPix(region))
                    using (var page = _engine.Process(pix, mode))
                    {
                        var text = page.GetText();
                        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    }
                }
                catch { return null; }
            }
        }

        public void Dispose() { lock (_lock) { _engine?.Dispose(); _engine = null; } }
    }

    internal static class TessdataDownloader
    {
        private const string EngUrl =
            "https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata";

        public static void EnsureEng(string dir, Action<string> log)
        {
            System.IO.Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, "eng.traineddata");
            if (System.IO.File.Exists(target) && new System.IO.FileInfo(target).Length > 0) return;

            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls11;
            log?.Invoke("[ocr] downloading eng.traineddata ...");
            using (var wc = new System.Net.WebClient())
                wc.DownloadFile(EngUrl, target);
        }
    }
}
