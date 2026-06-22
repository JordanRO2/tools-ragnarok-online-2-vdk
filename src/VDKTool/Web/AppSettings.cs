using System;
using System.IO;
using System.Text.Json;

namespace VDKTool.Web
{
    /// <summary>
    /// User preferences persisted to <c>%APPDATA%\VDKTool\settings.json</c>.
    /// Currently just a default output folder that the GUI uses to pre-fill the
    /// Extract field and seed the Browse dialogs. Kept tiny and best-effort: any
    /// IO error falls back to defaults rather than failing an operation.
    /// </summary>
    internal sealed class Settings
    {
        /// <summary>Folder pre-filled into output fields / used as the picker start dir. Empty = none.</summary>
        public string DefaultOutputFolder { get; set; } = "";
    }

    internal static class AppSettings
    {
        private static readonly object Lock = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VDKTool");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        /// <summary>Reads the settings file, or returns defaults if it is missing/unreadable.</summary>
        public static Settings Load()
        {
            lock (Lock)
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        string json = File.ReadAllText(FilePath);
                        var s = JsonSerializer.Deserialize<Settings>(json, JsonOpts);
                        if (s != null) return Normalize(s);
                    }
                }
                catch { /* ignore — fall back to defaults */ }
                return new Settings();
            }
        }

        /// <summary>Persists the settings (creating the folder if needed) and returns the normalized value.</summary>
        public static Settings Save(Settings s)
        {
            s = Normalize(s ?? new Settings());
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
                }
                catch { /* best-effort persistence */ }
            }
            return s;
        }

        private static Settings Normalize(Settings s)
        {
            s.DefaultOutputFolder = (s.DefaultOutputFolder ?? "").Trim();
            return s;
        }
    }
}
