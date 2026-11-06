using System;
using System.Diagnostics;
using System.IO;
using CloudSave.Junction;
using Logger = Silk.Logger;

namespace CloudSave
{
    public struct RootInfo
    {
        public string Key;
        public string Name;
        public string Local;
        public string Cloud;
    }

    public static class CloudSaveController
    {
        public static RootInfo[] Roots(string cloudRoot)
        {
            bool hasRoot = !string.IsNullOrEmpty(cloudRoot);
            return new[]
            {
                new RootInfo { Key = "SaveData", Name = "Save data", Local = CloudSaveConfig.SaveDataLocal, Cloud = hasRoot ? CloudSaveConfig.SaveDataCloud(cloudRoot) : null },
                new RootInfo { Key = "SilkConfig", Name = "Silk settings", Local = CloudSaveConfig.SilkConfigLocal, Cloud = hasRoot ? CloudSaveConfig.SilkConfigCloud(cloudRoot) : null },
                new RootInfo { Key = "SilkMods", Name = "Silk mods", Local = CloudSaveConfig.SilkModsLocal, Cloud = hasRoot ? CloudSaveConfig.SilkModsCloud(cloudRoot) : null },
            };
        }

        public static bool IsLinked(RootInfo root) => JunctionHelper.IsJunction(root.Local);

        public static int LinkedCount()
        {
            int count = 0;
            foreach (var root in Roots(null))
            {
                if (IsLinked(root))
                {
                    count++;
                }
            }
            return count;
        }

        // True when <root>\SpiderHeck already holds files (for example from another PC).
        public static bool CloudHasData(string cloudRoot)
        {
            try
            {
                string folder = Path.Combine(cloudRoot, "SpiderHeck");
                if (!Directory.Exists(folder))
                {
                    return false;
                }

                foreach (string unused in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Runs at mod load: reports what the helper did after the last exit.
        public static void StartupCheck()
        {
            if (HelperLauncher.IsHelperRunning())
            {
                Logger.LogError("[CloudSave] The helper from the last exit is still moving folders. Close the game and wait a minute before playing.");
                return;
            }

            string lastRun = CloudSaveConfig.LastRun;
            if (string.IsNullOrEmpty(lastRun) || lastRun == CloudSaveConfig.LastRunReported)
            {
                return;
            }

            var result = HelperLauncher.ReadLastResult();
            if (result == null || result.Stamp != lastRun)
            {
                Logger.LogWarning($"[CloudSave] The folder change started at the last exit ({lastRun}) left no result. Its log: {HelperLauncher.LogPathFor(lastRun)}");
                CloudSaveConfig.Set("LastRunReported", lastRun);
                return;
            }

            if (result.Ok)
            {
                Logger.LogInfo($"[CloudSave] Last exit: {result.Action} finished.");
            }
            else
            {
                Logger.LogError($"[CloudSave] Last exit: {result.Action} failed. {result.Error} Log: {result.Log}");
            }

            foreach (var root in result.Roots)
            {
                Logger.LogInfo($"[CloudSave]   {root.Key}: {root.Value}");
            }

            foreach (string warning in result.Warnings)
            {
                Logger.LogWarning($"[CloudSave]   {warning}");
            }

            if (!string.IsNullOrEmpty(result.Backup))
            {
                Logger.LogInfo($"[CloudSave] Backup of the folders as they were before that change: {result.Backup}");
            }

            if (result.Ok && result.Action == "Disable")
            {
                CloudSaveConfig.Set("Provider", "");
                CloudSaveConfig.Set("CloudRoot", "");
            }

            CloudSaveConfig.Set("LastRunReported", lastRun);
        }

        // Runs from the GameSaveWrapper.OnApplicationQuit postfix, after the game's final save.
        public static void OnGameExit()
        {
            string action = CloudSaveConfig.Pending;
            if (string.IsNullOrEmpty(action))
            {
                return;
            }

            string restoreFrom = "";
            if (action == "Restore")
            {
                string name = CloudSaveConfig.RestoreFrom;
                restoreFrom = string.IsNullOrEmpty(name) ? "" : Path.Combine(HelperLauncher.BackupsDir, name);
                if (restoreFrom.Length == 0 || !Directory.Exists(restoreFrom))
                {
                    Logger.LogWarning($"[CloudSave] The backup to restore ({name}) no longer exists, so nothing was done.");
                    CloudSaveConfig.Set("Pending", "");
                    CloudSaveConfig.Set("RestoreFrom", "");
                    return;
                }
            }

            bool needsTarget = action == "Enable" || action == "Switch";
            string targetRoot = action == "Switch" ? CloudSaveConfig.NewCloudRoot : CloudSaveConfig.CloudRoot;
            if (needsTarget && string.IsNullOrEmpty(targetRoot))
            {
                Logger.LogWarning("[CloudSave] No cloud folder is chosen, so there is nothing to do.");
                CloudSaveConfig.Set("Pending", "");
                return;
            }

            string stamp = HelperLauncher.Launch(action, Roots(needsTarget ? targetRoot : null), CloudSaveConfig.WhenBothExist, restoreFrom);
            if (stamp == null)
            {
                return; // the change stays pending for the next exit
            }

            CloudSaveConfig.Set("LastRun", stamp);
            CloudSaveConfig.Set("Pending", "");
            if (action == "Switch")
            {
                CloudSaveConfig.Set("Provider", CloudSaveConfig.NewProvider);
                CloudSaveConfig.Set("CloudRoot", CloudSaveConfig.NewCloudRoot);
            }
            CloudSaveConfig.Set("NewProvider", "");
            CloudSaveConfig.Set("NewCloudRoot", "");
            CloudSaveConfig.Set("WhenBothExist", "");
            CloudSaveConfig.Set("RestoreFrom", "");

            Logger.LogInfo($"[CloudSave] {action} handed to the helper, which runs once the game has closed. It backs up the folders to {Path.Combine(HelperLauncher.BackupsDir, stamp)} first. Log: {HelperLauncher.LogPathFor(stamp)}");
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[CloudSave] Could not open {path}: {ex.Message}");
            }
        }
    }
}
