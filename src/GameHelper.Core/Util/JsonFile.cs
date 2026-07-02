using System.IO;
using Newtonsoft.Json;

namespace GameHelper.Core.Util
{
    /// <summary>
    /// Tiny JSON file helpers for persistent caches (player rosters, etc.): tolerant load (returns
    /// null on missing/corrupt) and a minified, directory-creating save. Game-agnostic.
    /// </summary>
    public static class JsonFile
    {
        /// <summary>Deserialize <paramref name="path"/> to T, or null if missing/unreadable/invalid.</summary>
        public static T Load<T>(string path) where T : class
        {
            try { if (File.Exists(path)) return JsonConvert.DeserializeObject<T>(File.ReadAllText(path)); }
            catch { /* corrupt/locked -> treat as absent */ }
            return null;
        }

        /// <summary>Write <paramref name="obj"/> as minified JSON, creating the directory if needed.</summary>
        public static void SaveMinified(string path, object obj)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.None));
        }
    }
}
