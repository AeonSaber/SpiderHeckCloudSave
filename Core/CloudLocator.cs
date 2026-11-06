using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CloudSave
{
    // Finds the folder each sync app uses, from the app's own records rather than guessed paths.
    public static class CloudLocator
    {
        public static string DetectOneDrive()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("OneDriveConsumer"),
                ReadRegistry(@"Software\Microsoft\OneDrive\Accounts\Personal", "UserFolder"),
                Environment.GetEnvironmentVariable("OneDrive"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"),
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        public static string DetectGoogleDrive()
        {
            // Google Drive for desktop, mirror mode: the folder the user chose is recorded in the app's
            // settings database (table roots, row with is_my_drive = 1).
            string mirrored = DriveFs.FindMyDriveFolder();
            if (mirrored != null)
            {
                return mirrored;
            }

            // Stream mode: a virtual drive labelled "Google Drive" on whatever letter the app chose.
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady && drive.VolumeLabel == "Google Drive")
                        {
                            string myDrive = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                            if (Directory.Exists(myDrive))
                            {
                                return myDrive;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // A drive that is not ready or not readable; skip it.
                    }
                }
            }
            catch (Exception)
            {
                // Drive enumeration failed; fall through to the older app.
            }

            // The retired Backup and Sync app.
            string legacy = ReadRegistry(@"Software\Google\Drive\Preference", "Drive");
            if (!string.IsNullOrEmpty(legacy) && Directory.Exists(legacy))
            {
                return legacy;
            }

            string profileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive");
            return Directory.Exists(profileFolder) ? profileFolder : null;
        }

        internal static string ReadRegistry(string keyPath, string valueName)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    return key?.GetValue(valueName) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // Reads Google Drive for desktop's root_preference_sqlite.db with Windows' own SQLite
    // (winsqlite3.dll, part of Windows 10 and 11), so the mod ships no extra DLLs.
    internal static class DriveFs
    {
        private const int SqliteOk = 0;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;
        private const int SqliteOpenReadOnly = 0x1;

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.StdCall)]
        private static extern int SqliteOpen(byte[] fileNameUtf8, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare16_v2", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        private static extern int SqlitePrepare(IntPtr db, string sql, int byteCount, out IntPtr statement, IntPtr tail);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.StdCall)]
        private static extern int SqliteStep(IntPtr statement);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text16", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr SqliteColumnText(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.StdCall)]
        private static extern int SqliteFinalize(IntPtr statement);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.StdCall)]
        private static extern int SqliteClose(IntPtr db);

        private const string Query = "SELECT last_seen_absolute_path, account_token FROM roots WHERE is_my_drive = 1";

        public static string FindMyDriveFolder()
        {
            string dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "DriveFS", "root_preference_sqlite.db");
            if (!File.Exists(dbPath))
            {
                return null;
            }

            var rows = ReadRows(dbPath) ?? ReadRowsFromCopy(dbPath);
            if (rows == null)
            {
                return null;
            }

            // Prefer the account the app is signed in with now.
            string currentAccount = CloudLocator.ReadRegistry(@"Software\Google\DriveFS", "CurrentAccountToken");
            string first = null;
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Key) || !Directory.Exists(row.Key))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(currentAccount) && row.Value == currentAccount)
                {
                    return row.Key;
                }

                if (first == null)
                {
                    first = row.Key;
                }
            }

            return first;
        }

        // The database is open in the Drive app (WAL mode); reading it read-only normally works.
        private static List<KeyValuePair<string, string>> ReadRows(string dbPath)
        {
            IntPtr db = IntPtr.Zero;
            IntPtr statement = IntPtr.Zero;
            try
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(dbPath + "\0");
                if (SqliteOpen(name, out db, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk)
                {
                    return null;
                }

                if (SqlitePrepare(db, Query, -1, out statement, IntPtr.Zero) != SqliteOk)
                {
                    return null;
                }

                var rows = new List<KeyValuePair<string, string>>();
                int result;
                while ((result = SqliteStep(statement)) == SqliteRow)
                {
                    rows.Add(new KeyValuePair<string, string>(Text(statement, 0), Text(statement, 1)));
                }

                return result == SqliteDone ? rows : null;
            }
            catch (Exception)
            {
                return null; // no winsqlite3.dll, or the call failed
            }
            finally
            {
                if (statement != IntPtr.Zero)
                {
                    SqliteFinalize(statement);
                }
                if (db != IntPtr.Zero)
                {
                    SqliteClose(db);
                }
            }
        }

        // Fallback: read a copy of the database (and its WAL files) from a new temp folder.
        private static List<KeyValuePair<string, string>> ReadRowsFromCopy(string dbPath)
        {
            string folder = Path.Combine(Path.GetTempPath(), "SpiderHeckCloudSave-drivefs-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                string copy = Path.Combine(folder, Path.GetFileName(dbPath));
                File.Copy(dbPath, copy);
                foreach (string suffix in new[] { "-wal", "-shm" })
                {
                    if (File.Exists(dbPath + suffix))
                    {
                        File.Copy(dbPath + suffix, copy + suffix);
                    }
                }
                return ReadRows(copy);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception)
                {
                    // A temp folder left behind is harmless.
                }
            }
        }

        private static string Text(IntPtr statement, int column)
        {
            IntPtr text = SqliteColumnText(statement, column);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text);
        }
    }
}
