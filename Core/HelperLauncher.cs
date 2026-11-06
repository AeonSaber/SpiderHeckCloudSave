using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Logger = Silk.Logger;

namespace CloudSave
{
    public class HelperResult
    {
        public string Stamp = "";
        public string Action = "";
        public bool Ok;
        public string Log = "";
        public string Backup = "";
        public string Error = "";
        public readonly List<KeyValuePair<string, string>> Roots = new List<KeyValuePair<string, string>>();
        public readonly List<string> Warnings = new List<string>();
    }

    public class BackupInfo
    {
        public string Stamp = "";
        public string Path = "";
        public string Action = "";
        public string Summary = "";
    }

    // Folders are never moved while the game runs: Silk keeps the mod DLLs open and the game keeps
    // its save folder open, so Windows refuses (0.1.0 failed with "Access denied"). Instead, at exit
    // the mod writes a plan and starts cloudsave-helper.ps1 (embedded in this DLL), which waits for
    // the game to close and then does the work.
    public static class HelperLauncher
    {
        private const string ScriptResource = "CloudSave.Helper.ps1";
        public const int KeepBackups = 5;

        public static string BackupsDir => Path.Combine(CloudSaveConfig.WorkDir, "Backups");
        private static string RunsDir => Path.Combine(CloudSaveConfig.WorkDir, "Runs");
        private static string ResultPath => Path.Combine(CloudSaveConfig.WorkDir, "last-run.txt");
        private static string LockPath => Path.Combine(CloudSaveConfig.WorkDir, "helper.lock");

        public static string LogPathFor(string stamp) => Path.Combine(RunsDir, stamp + ".log");

        // Starts the helper. Returns the run stamp, or null if it could not be started.
        public static string Launch(string action, RootInfo[] roots, string whenBothExist, string restoreFrom)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.CreateDirectory(RunsDir);
                Directory.CreateDirectory(BackupsDir);

                string script = Path.Combine(CloudSaveConfig.WorkDir, "cloudsave-helper.ps1");
                File.WriteAllText(script, ReadScript(), new UTF8Encoding(true)); // BOM: Windows PowerShell reads it as UTF-8

                var game = Process.GetCurrentProcess();
                string planPath = Path.Combine(RunsDir, stamp + ".plan.json");
                File.WriteAllText(planPath, BuildPlan(action, stamp, game.Id, game.ProcessName, whenBothExist, restoreFrom, roots), new UTF8Encoding(false));

                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powershell))
                {
                    powershell = "powershell.exe";
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = powershell,
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -PlanPath \"{planPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = CloudSaveConfig.WorkDir
                });

                return stamp;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[CloudSave] Could not start the helper, so nothing was changed: {ex.Message}");
                return null;
            }
        }

        public static bool IsHelperRunning()
        {
            try
            {
                if (!File.Exists(LockPath))
                {
                    return false;
                }

                if (!int.TryParse(File.ReadAllText(LockPath).Trim(), out int pid))
                {
                    return false;
                }

                var process = Process.GetProcessById(pid);
                return !process.HasExited && process.ProcessName.StartsWith("powershell", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false; // no such process
            }
        }

        public static HelperResult ReadLastResult()
        {
            try
            {
                if (!File.Exists(ResultPath))
                {
                    return null;
                }

                var result = new HelperResult();
                foreach (string line in File.ReadAllLines(ResultPath))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim().TrimStart('﻿');
                    string value = line.Substring(split + 1);
                    switch (key)
                    {
                        case "Stamp": result.Stamp = value; break;
                        case "Action": result.Action = value; break;
                        case "Ok": result.Ok = value.Trim() == "true"; break;
                        case "Log": result.Log = value; break;
                        case "Backup": result.Backup = value; break;
                        case "Error": result.Error = value; break;
                        case "Warning": result.Warnings.Add(value); break;
                        default:
                            if (key.StartsWith("Root:"))
                            {
                                result.Roots.Add(new KeyValuePair<string, string>(key.Substring(5), value));
                            }
                            break;
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[CloudSave] Could not read {ResultPath}: {ex.Message}");
                return null;
            }
        }

        // The backups kept under Backups, newest first, with a one-line summary of each.
        public static List<BackupInfo> ListBackups()
        {
            var backups = new List<BackupInfo>();
            try
            {
                if (!Directory.Exists(BackupsDir))
                {
                    return backups;
                }

                var folders = new List<string>(Directory.GetDirectories(BackupsDir));
                folders.Sort(StringComparer.Ordinal);
                folders.Reverse();
                foreach (string folder in folders)
                {
                    string stamp = Path.GetFileName(folder);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(stamp, @"^\d{8}-\d{6}$"))
                    {
                        continue;
                    }

                    var info = new BackupInfo { Stamp = stamp, Path = folder };
                    var parts = new List<string>();
                    string infoFile = Path.Combine(folder, "backup-info.txt");
                    if (File.Exists(infoFile))
                    {
                        foreach (string line in File.ReadAllLines(infoFile))
                        {
                            string text = line.TrimStart('﻿');
                            if (text.StartsWith("Action="))
                            {
                                info.Action = text.Substring(7);
                            }
                            else if (text.StartsWith("Folder:"))
                            {
                                // Folder:<name>=<root name>|<what>|<from>|<files>|<bytes>
                                string[] fields = text.Substring(text.IndexOf('=') + 1).Split('|');
                                if (fields.Length >= 4)
                                {
                                    parts.Add($"{fields[0]} ({fields[3]} files, {fields[1]})");
                                }
                            }
                        }
                    }
                    else
                    {
                        // 0.2.0 backups have no backup-info.txt; describe their folders.
                        foreach (string sub in Directory.GetDirectories(folder))
                        {
                            parts.Add($"{Path.GetFileName(sub)} ({Directory.GetFiles(sub, "*", SearchOption.AllDirectories).Length} files)");
                        }
                    }

                    info.Summary = parts.Count > 0 ? string.Join(", ", parts) : "(empty)";
                    backups.Add(info);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[CloudSave] Could not list the backups: {ex.Message}");
            }
            return backups;
        }

        private static string ReadScript()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("the helper script is missing from the DLL");
                }
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string BuildPlan(string action, string stamp, int gamePid, string gameName, string whenBothExist, string restoreFrom, RootInfo[] roots)
        {
            var json = new StringBuilder();
            json.Append('{');
            Field(json, "Action", action);
            json.Append($"\"GamePid\":{gamePid},");
            Field(json, "GameName", gameName);
            json.Append("\"WaitSeconds\":300,");
            Field(json, "Stamp", stamp);
            Field(json, "WorkDir", CloudSaveConfig.WorkDir);
            Field(json, "BackupsRoot", BackupsDir);
            Field(json, "BackupDir", Path.Combine(BackupsDir, stamp));
            Field(json, "LogPath", LogPathFor(stamp));
            Field(json, "ResultPath", ResultPath);
            Field(json, "WhenBothExist", whenBothExist);
            Field(json, "RestoreFrom", restoreFrom ?? "");
            json.Append($"\"KeepBackups\":{KeepBackups},");
            json.Append("\"Roots\":[");
            for (int i = 0; i < roots.Length; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }
                json.Append('{');
                Field(json, "Key", roots[i].Key);
                Field(json, "Name", roots[i].Name);
                Field(json, "Local", roots[i].Local);
                Field(json, "Cloud", roots[i].Cloud ?? "", last: true);
                json.Append('}');
            }
            json.Append("]}");
            return json.ToString();
        }

        private static void Field(StringBuilder json, string name, string value, bool last = false)
        {
            json.Append('"').Append(name).Append("\":\"").Append(Escape(value ?? "")).Append('"');
            if (!last)
            {
                json.Append(',');
            }
        }

        private static string Escape(string value)
        {
            var escaped = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    default:
                        if (c < ' ')
                        {
                            escaped.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            escaped.Append(c);
                        }
                        break;
                }
            }
            return escaped.ToString();
        }
    }
}
