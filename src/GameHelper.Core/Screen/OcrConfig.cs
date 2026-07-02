using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace GameHelper.Core.Screen
{
    /// <summary>Editable data part of one OCR field (the Validate/Clean logic stays in code).</summary>
    public sealed class OcrFieldSpec
    {
        public string Whitelist;
        public int? MinLength;
        public int? MaxLength;
        public int? Establish;
        public int? Overturn;
        public int? Window;
        public string Pattern;      // regex source
        public string[] Reject;

        public static OcrFieldSpec From(OcrField f) => new OcrFieldSpec
        {
            Whitelist = f.Whitelist, MinLength = f.MinLength, MaxLength = f.MaxLength,
            Establish = f.Establish, Overturn = f.Overturn, Window = f.Window,
            Pattern = f.Pattern?.ToString(), Reject = f.Reject
        };

        public void ApplyTo(OcrField f)
        {
            if (Whitelist != null) f.Whitelist = Whitelist;
            if (MinLength.HasValue) f.MinLength = MinLength.Value;
            if (MaxLength.HasValue) f.MaxLength = MaxLength.Value;
            if (Establish.HasValue) f.Establish = Establish.Value;
            if (Overturn.HasValue) f.Overturn = Overturn.Value;
            if (Window.HasValue) f.Window = Window.Value;
            if (Reject != null) f.Reject = Reject;
            if (Pattern != null) f.Pattern = new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    /// <summary>
    /// Externalized OCR rules (%APPDATA%\GameHelper\{game}\ocr.jsonc). Overrides the per-field char
    /// whitelist, length bounds, regex pattern, reject-list and confidence thresholds at startup.
    /// Game-agnostic: the field registry and the perf-strip whitelist are supplied by the caller
    /// (the game's IGameProfile), so this class knows nothing about any specific game's fields.
    /// </summary>
    public sealed class OcrConfig
    {
        public string PerfStripWhitelist;
        public Dictionary<string, OcrFieldSpec> Fields = new Dictionary<string, OcrFieldSpec>();

        /// <summary>Default ocr.jsonc path for a game: %APPDATA%\GameHelper\{game}\ocr.jsonc.</summary>
        public static string DefaultPath(string game) =>
            Path.Combine(Config.HelperConfig.GameDataDir(game), "ocr.jsonc");

        /// <summary>
        /// Load ocr.jsonc and apply overrides onto <paramref name="registry"/>; write a commented
        /// default from the code specs if the file is missing. <paramref name="perfGet"/>/
        /// <paramref name="perfSet"/> read/write the game's perf-strip whitelist.
        /// </summary>
        public static void LoadOrCreate(string path, IReadOnlyDictionary<string, OcrField> registry,
            Func<string> perfGet, Action<string> perfSet, Action<string> log = null)
        {
            if (registry == null) return;
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonConvert.DeserializeObject<OcrConfig>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        if (cfg.PerfStripWhitelist != null) perfSet?.Invoke(cfg.PerfStripWhitelist);
                        if (cfg.Fields != null)
                            foreach (var kv in cfg.Fields)
                                if (registry.TryGetValue(kv.Key, out var f) && kv.Value != null) kv.Value.ApplyTo(f);
                        log?.Invoke($"[ocr] applied overrides from {path}");
                    }
                    return;
                }

                var def = new OcrConfig { PerfStripWhitelist = perfGet?.Invoke() };
                foreach (var kv in registry) def.Fields[kv.Key] = OcrFieldSpec.From(kv.Value);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path,
                    "// Game Helper OCR rules (JSONC). Per-field char whitelist, length bounds, regex\n" +
                    "// pattern, reject words and confidence (Establish/Overturn/Window). Delete to regenerate.\n" +
                    JsonConvert.SerializeObject(def, Formatting.Indented));
                log?.Invoke($"[ocr] wrote default {path}");
            }
            catch (Exception ex) { log?.Invoke($"[ocr] config error ({ex.Message})"); }
        }
    }
}
