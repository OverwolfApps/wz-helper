using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace WarzoneHelper.Core.Screen
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
    /// Externalized OCR rules (%APPDATA%\WarzoneHelper\ocr.jsonc). Overrides the per-field char
    /// whitelist, length bounds, regex pattern, reject-list and confidence thresholds at startup.
    /// </summary>
    public sealed class OcrConfig
    {
        public string PerfStripWhitelist;
        public Dictionary<string, OcrFieldSpec> Fields = new Dictionary<string, OcrFieldSpec>();

        /// <summary>All named fields, keyed by their spec name.</summary>
        private static Dictionary<string, OcrField> Registry() => new Dictionary<string, OcrField>
        {
            { OcrFields.LobbyId.Name, OcrFields.LobbyId },
            { OcrFields.PlayerName.Name, OcrFields.PlayerName },
            { OcrFields.Level.Name, OcrFields.Level },
            { OcrFields.SpectateId.Name, OcrFields.SpectateId },
            { OcrFields.PartyCode.Name, OcrFields.PartyCode },
            { OcrFields.ChatChannel.Name, OcrFields.ChatChannel },
            { OcrFields.Fps.Name, OcrFields.Fps },
            { OcrFields.Latency.Name, OcrFields.Latency },
            { OcrFields.GameLatency.Name, OcrFields.GameLatency },
            { OcrFields.PacketLoss.Name, OcrFields.PacketLoss },
            { OcrFields.GpuTemp.Name, OcrFields.GpuTemp },
            { OcrFields.VramPct.Name, OcrFields.VramPct },
            { OcrFields.Clock.Name, OcrFields.Clock },
        };

        public static string DefaultPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarzoneHelper", "ocr.jsonc");

        /// <summary>Load ocr.jsonc and apply overrides; write a commented default from the code specs if missing.</summary>
        public static void LoadOrCreate(string path, Action<string> log = null)
        {
            var reg = Registry();
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonConvert.DeserializeObject<OcrConfig>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        if (cfg.PerfStripWhitelist != null) OcrFields.PerfStripWhitelist = cfg.PerfStripWhitelist;
                        if (cfg.Fields != null)
                            foreach (var kv in cfg.Fields)
                                if (reg.TryGetValue(kv.Key, out var f) && kv.Value != null) kv.Value.ApplyTo(f);
                        log?.Invoke($"[ocr] applied overrides from {path}");
                    }
                    return;
                }

                var def = new OcrConfig { PerfStripWhitelist = OcrFields.PerfStripWhitelist };
                foreach (var kv in reg) def.Fields[kv.Key] = OcrFieldSpec.From(kv.Value);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path,
                    "// Warzone Helper OCR rules (JSONC). Per-field char whitelist, length bounds, regex\n" +
                    "// pattern, reject words and confidence (Establish/Overturn/Window). Delete to regenerate.\n" +
                    JsonConvert.SerializeObject(def, Formatting.Indented));
                log?.Invoke($"[ocr] wrote default {path}");
            }
            catch (Exception ex) { log?.Invoke($"[ocr] config error ({ex.Message})"); }
        }
    }
}
