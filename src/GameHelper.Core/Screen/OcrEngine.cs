using System;
using System.Drawing;

namespace GameHelper.Core.Screen
{
    public interface IOcrEngine : IDisposable
    {
        bool Available { get; }
        /// <summary>OCR a region; return recognized text (trimmed) or null. Set singleLine=false for chat/blocks.</summary>
        string Read(Bitmap region, string whitelist = null, bool singleLine = true);
    }

    /// <summary>Returned when Tesseract is not installed/initialised. Color-based events still work.</summary>
    public sealed class NullOcrEngine : IOcrEngine
    {
        public bool Available => false;
        public string Read(Bitmap region, string whitelist = null, bool singleLine = true) => null;
        public void Dispose() { }
    }
}
