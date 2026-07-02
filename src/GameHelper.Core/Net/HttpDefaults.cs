namespace GameHelper.Core.Net
{
    /// <summary>Shared HTTP defaults for outbound API calls.</summary>
    public static class HttpDefaults
    {
        /// <summary>A recent, widely-used desktop Chrome User-Agent — servers that reject unknown
        /// UAs (or serve degraded responses) treat this as an ordinary browser.</summary>
        public const string ChromeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
    }
}
