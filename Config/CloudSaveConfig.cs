using System;
using System.Collections.Generic;
using System.IO;
using Logger = Silk.Logger;

namespace CloudSave
{
    // CloudSave's own settings. They live in %LOCALAPPDATA%\SpiderHeckCloudSave\settings.txt, not in
    // Silk\Config: CloudSave moves Silk\Config into the cloud folder, and these settings must stay
    // per PC (a pending "turn off" on one PC must never reach another PC through the sync).
    public static class CloudSaveConfig
    {
        public static string WorkDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpiderHeckCloudSave");

        private static string SettingsPath => Path.Combine(WorkDir, "settings.txt");

        private static Dictionary<string, string> _values;

        // The cloud folder in use (or chosen), e.g. C:\Users\me\OneDrive. Data goes in <CloudRoot>\SpiderHeck.
        public static string Provider => Get("Provider");
        public static string CloudRoot => Get("CloudRoot");
        public static string CustomPath => Get("CustomPath");

        // What happens when the game exits: "", "Enable", "Disable" or "Switch".
        public static string Pending
        {
            get
            {
                string value = Get("Pending");
                return value == "Undo" ? "Disable" : value; // "Undo" was 0.1.0's name for Disable
            }
        }

        // The folder a "Switch" moves to.
        public static string NewProvider => Get("NewProvider");
        public static string NewCloudRoot => Get("NewCloudRoot");

        // When the cloud folder already holds SpiderHeck data: "Cloud" keeps it (this PC's copy goes
        // to the backup), "Local" replaces it with this PC's copy (the cloud copy is renamed and backed up).
        public static string WhenBothExist => Get("WhenBothExist") == "Local" ? "Local" : "Cloud";

        // The helper run started at the last exit, and the last run already reported in the log.
        public static string LastRun => Get("LastRun");
        public static string LastRunReported => Get("LastRunReported");

        public static bool IsConfigured => !string.IsNullOrEmpty(CloudRoot);

        public static string Get(string key)
        {
            Load();
            return _values.TryGetValue(key, out var value) ? value : "";
        }

        public static void Set(string key, string value)
        {
            Load();
            _values[key] = (value ?? "").Replace("\r", "").Replace("\n", "");
            Save();
        }

        private static void Load()
        {
            if (_values != null)
            {
                return;
            }

            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int split = line.IndexOf('=');
                    if (split > 0)
                    {
                        _values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[CloudSave] Could not read {SettingsPath}: {ex.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(WorkDir);
                var lines = new List<string>();
                foreach (var pair in _values)
                {
                    lines.Add(pair.Key + "=" + pair.Value);
                }
                lines.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(SettingsPath, lines);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[CloudSave] Could not save {SettingsPath}: {ex.Message}");
            }
        }

        // The game folder (the parent of SpiderHeckApp_Data), and the three folders CloudSave moves.
        private static string GameDir => Path.GetDirectoryName(Path.GetFullPath(UnityEngine.Application.dataPath));

        public static string SaveDataLocal => Path.GetFullPath(UnityEngine.Application.persistentDataPath);
        public static string SilkConfigLocal => Path.Combine(GameDir, "Silk", "Config");
        public static string SilkModsLocal => Path.Combine(GameDir, "Silk", "Mods");

        public static string SaveDataCloud(string root) => Path.Combine(root, "SpiderHeck", "SaveData");
        public static string SilkConfigCloud(string root) => Path.Combine(root, "SpiderHeck", "Silk", "Config");
        public static string SilkModsCloud(string root) => Path.Combine(root, "SpiderHeck", "Silk", "Mods");
    }
}
