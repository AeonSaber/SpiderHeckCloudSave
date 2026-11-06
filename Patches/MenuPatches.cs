using System;
using System.IO;
using HarmonyLib;
using Logger = Silk.Logger;
using UnityEngine;
using UnityEngine.UI;

namespace CloudSave.Patches
{
    // Silk opens a mod's settings page with Announcer.ModsPopup(<mod name>); this fills in CloudSave's.
    [HarmonyPatch(typeof(Announcer), nameof(Announcer.ModsPopup))]
    public static class MenuPatches
    {
        private static string _message;

        public static void Postfix(string title, ref ModsMenuPopup __result)
        {
            if (title == CloudSaveMod.ModName && __result != null)
            {
                Build(__result);
            }
        }

        private static void Build(ModsMenuPopup menu)
        {
            menu.CreateParagraph("<size=200%>\n<b><color=#FFD700>CloudSave</color></b></size>");
            menu.CreateParagraph("<size=95%><color=#AAAAAA>Keeps your save data, Silk settings and Silk mods in a cloud-synced folder (OneDrive, Google Drive or any folder you choose), so they follow you to your other PCs.</color></size>");

            if (!string.IsNullOrEmpty(_message))
            {
                menu.CreateParagraph($"<size=110%><color=#FFAA00><noparse>{_message}</noparse></color></size>");
                _message = null;
            }

            if (HelperLauncher.IsHelperRunning())
            {
                menu.CreateParagraph("<size=105%><color=#FF5555>The change from the last exit is still running. Close the game and wait a minute before playing.</color></size>");
            }

            AddLastRun(menu);
            Spacer(menu);

            bool configured = CloudSaveConfig.IsConfigured;
            var roots = CloudSaveController.Roots(configured ? CloudSaveConfig.CloudRoot : null);
            int linked = CloudSaveController.LinkedCount();
            string pending = CloudSaveConfig.Pending;

            // Status
            string status;
            if (linked == roots.Length)
            {
                status = "<color=#00FF00>on</color>";
            }
            else if (linked > 0)
            {
                status = "<color=#FFAA00>partly on</color>";
            }
            else
            {
                status = "<color=#AAAAAA>off</color>";
            }
            menu.CreateParagraph($"<size=115%><b>Status:</b> {status}</size>");

            if (configured)
            {
                string provider = string.IsNullOrEmpty(CloudSaveConfig.Provider) ? "" : $" ({CloudSaveConfig.Provider})";
                menu.CreateParagraph($"<size=100%><b>Cloud folder:</b> <noparse>{Path.Combine(CloudSaveConfig.CloudRoot, "SpiderHeck")}</noparse>{provider}</size>");
            }

            if (linked > 0 || configured)
            {
                foreach (var root in roots)
                {
                    string where = CloudSaveController.IsLinked(root)
                        ? "<color=#00FF00>in the cloud folder</color>"
                        : "<color=#AAAAAA>on this PC</color>";
                    menu.CreateParagraph($"<size=95%>{root.Name}: {where}</size>");
                }
            }

            // What happens at exit
            if (!string.IsNullOrEmpty(pending))
            {
                Spacer(menu);
                menu.CreateParagraph($"<size=105%><color=#FFAA00><b>When the game closes:</b> <noparse>{DescribePending(pending)}</noparse></color></size>");

                string target = pending == "Switch" ? CloudSaveConfig.NewCloudRoot : CloudSaveConfig.CloudRoot;
                if ((pending == "Enable" || pending == "Switch") && !string.IsNullOrEmpty(target) && CloudSaveController.CloudHasData(target))
                {
                    AddConflictChoice(menu);
                }

                AddButton(menu, "Cancel this change", () =>
                {
                    CloudSaveConfig.Set("Pending", "");
                    CloudSaveConfig.Set("NewProvider", "");
                    CloudSaveConfig.Set("NewCloudRoot", "");
                    _message = "Change cancelled.";
                    RefreshLater(menu);
                });
            }

            // Choosing a folder
            Spacer(menu);
            menu.CreateParagraph(linked > 0
                ? "<size=120%><color=#AAAAAA>Move to another cloud folder:</color></size>"
                : "<size=120%><color=#AAAAAA>Choose a cloud folder:</color></size>");

            string oneDrive = CloudLocator.DetectOneDrive();
            AddButton(menu, oneDrive != null ? "Use OneDrive" : "Use OneDrive (not found on this PC)", () => ChooseDetected(menu, "OneDrive", oneDrive));

            string googleDrive = CloudLocator.DetectGoogleDrive();
            AddButton(menu, googleDrive != null ? "Use Google Drive" : "Use Google Drive (not found on this PC)", () => ChooseDetected(menu, "GoogleDrive", googleDrive));

            AddButton(menu, "Use a custom folder...", () => ShowCustomInput(menu));

            if (linked > 0 || configured)
            {
                Spacer(menu);
                if (configured && linked > 0 && linked < roots.Length && string.IsNullOrEmpty(pending))
                {
                    AddButton(menu, "Finish moving the remaining folders", () =>
                    {
                        CloudSaveConfig.Set("Pending", "Enable");
                        _message = "The remaining folders will move to the cloud folder when the game closes.";
                        RefreshLater(menu);
                    });
                }

                AddButton(menu, "Turn off cloud save", () =>
                {
                    CloudSaveConfig.Set("Pending", "Disable");
                    CloudSaveConfig.Set("NewProvider", "");
                    CloudSaveConfig.Set("NewCloudRoot", "");
                    _message = "Your folders will be copied back to this PC when the game closes. The cloud copy is kept.";
                    RefreshLater(menu);
                });

                if (configured)
                {
                    AddButton(menu, "Open cloud folder", () => CloudSaveController.OpenFolder(Path.Combine(CloudSaveConfig.CloudRoot, "SpiderHeck")));
                }
            }

            AddButton(menu, "Open backups folder", () => CloudSaveController.OpenFolder(HelperLauncher.BackupsDir));

            Spacer(menu);
            menu.CreateParagraph("<size=90%><color=#AAAAAA>Changes happen after the game closes: a small helper waits for SpiderHeck to exit, makes a full backup of each folder, then copies, checks and links it. Cloud copies are never deleted. Your sync app should keep the SpiderHeck folder available offline.</color></size>");
            menu.CreateParagraph($"<size=85%><color=#666666>Backups (last {HelperLauncher.KeepBackups} changes): <noparse>{HelperLauncher.BackupsDir}</noparse></color></size>");
        }

        private static void AddLastRun(ModsMenuPopup menu)
        {
            var last = HelperLauncher.ReadLastResult();
            if (last == null)
            {
                return;
            }

            string outcome = last.Ok ? "<color=#00FF00>done</color>" : "<color=#FF5555>failed</color>";
            menu.CreateParagraph($"<size=100%><b>Last change:</b> {DescribeAction(last.Action)}, {FormatStamp(last.Stamp)}: {outcome}</size>");

            if (last.Ok)
            {
                return;
            }

            if (!string.IsNullOrEmpty(last.Error))
            {
                menu.CreateParagraph($"<size=90%><color=#FF8888><noparse>{last.Error}</noparse></color></size>");
            }

            foreach (var root in last.Roots)
            {
                if (!root.Value.StartsWith("ok"))
                {
                    menu.CreateParagraph($"<size=90%><color=#FF8888><noparse>{root.Key}: {root.Value}</noparse></color></size>");
                }
            }

            menu.CreateParagraph($"<size=85%><color=#888888>Log: <noparse>{last.Log}</noparse></color></size>");
        }

        private static void AddConflictChoice(ModsMenuPopup menu)
        {
            bool keepCloud = CloudSaveConfig.WhenBothExist == "Cloud";
            menu.CreateParagraph("<size=95%>That cloud folder already has SpiderHeck data, maybe from another PC. Which copy should be used?</size>");

            AddButton(menu, (keepCloud ? "[x] " : "[ ] ") + "The cloud copy (this PC's copy goes to the backup)", () =>
            {
                CloudSaveConfig.Set("WhenBothExist", "Cloud");
                RefreshLater(menu);
            });

            AddButton(menu, (keepCloud ? "[ ] " : "[x] ") + "This PC's copy (the cloud copy is renamed and backed up)", () =>
            {
                CloudSaveConfig.Set("WhenBothExist", "Local");
                RefreshLater(menu);
            });
        }

        private static void ChooseDetected(ModsMenuPopup menu, string provider, string folder)
        {
            if (folder == null)
            {
                _message = provider == "OneDrive"
                    ? "No OneDrive folder was found on this PC. Use a custom folder instead."
                    : "No Google Drive folder was found. Open Google Drive for desktop and sign in, or use a custom folder.";
                RefreshLater(menu);
                return;
            }

            ChooseFolder(menu, provider, folder);
        }

        private static void ShowCustomInput(ModsMenuPopup menu)
        {
            string initial = CloudSaveConfig.CustomPath;
            if (string.IsNullOrEmpty(initial))
            {
                initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var input = CreateInputField(menu, initial);
            if (input == null)
            {
                return;
            }

            input.onEndEdit.AddListener(value => OnCustomPathCommitted(menu, value));
        }

        private static void OnCustomPathCommitted(ModsMenuPopup menu, string raw)
        {
            string path = (raw ?? "").Trim().Trim('"');
            if (path.Length == 0)
            {
                _message = "No folder path entered.";
                RefreshLater(menu);
                return;
            }

            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                Directory.CreateDirectory(path);
            }
            catch (Exception)
            {
                _message = "That is not a valid folder path.";
                RefreshLater(menu);
                return;
            }

            CloudSaveConfig.Set("CustomPath", path);
            ChooseFolder(menu, "Custom", path);
        }

        private static void ChooseFolder(ModsMenuPopup menu, string provider, string folder)
        {
            folder = Path.GetFullPath(folder).TrimEnd('\\');
            int linked = CloudSaveController.LinkedCount();
            bool sameFolder = CloudSaveConfig.IsConfigured
                && string.Equals(Path.GetFullPath(CloudSaveConfig.CloudRoot).TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase);

            if (sameFolder && linked > 0)
            {
                _message = "Cloud save already uses that folder.";
                CloudSaveConfig.Set("NewProvider", "");
                CloudSaveConfig.Set("NewCloudRoot", "");
                if (CloudSaveConfig.Pending == "Switch")
                {
                    CloudSaveConfig.Set("Pending", "");
                }
            }
            else if (linked > 0)
            {
                CloudSaveConfig.Set("NewProvider", provider);
                CloudSaveConfig.Set("NewCloudRoot", folder);
                CloudSaveConfig.Set("Pending", "Switch");
                _message = $"Your folders will move to {Path.Combine(folder, "SpiderHeck")} when the game closes. The old cloud copy is kept.";
            }
            else
            {
                CloudSaveConfig.Set("Provider", provider);
                CloudSaveConfig.Set("CloudRoot", folder);
                CloudSaveConfig.Set("Pending", "Enable");
                _message = $"Your folders will move to {Path.Combine(folder, "SpiderHeck")} when the game closes.";
            }

            CloudSaveConfig.Set("WhenBothExist", "");
            Logger.LogInfo($"[CloudSave] Cloud folder chosen: {provider} at {folder}");
            RefreshLater(menu);
        }

        private static string DescribePending(string pending)
        {
            switch (pending)
            {
                case "Enable": return "move the folders to the cloud folder";
                case "Disable": return "copy the folders back to this PC and turn cloud save off";
                case "Switch": return $"move the folders to {Path.Combine(CloudSaveConfig.NewCloudRoot, "SpiderHeck")}";
                default: return pending;
            }
        }

        private static string DescribeAction(string action)
        {
            switch (action)
            {
                case "Enable": return "turn on";
                case "Disable": return "turn off";
                case "Switch": return "move to another folder";
                default: return action;
            }
        }

        private static string FormatStamp(string stamp)
        {
            return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var time)
                ? time.ToString("yyyy-MM-dd HH:mm")
                : stamp;
        }

        private static void AddButton(ModsMenuPopup menu, string label, Action onClick)
        {
            int before = GetChildCount(menu);
            menu.CreateButton(label, () => onClick());
            ResizeLastCreatedButton(menu, before);
        }

        private static void Spacer(ModsMenuPopup menu)
        {
            menu.CreateParagraph("<size=100%>\n </size>");
        }

        // Builds a one-line text input the same way the game's menus are laid out. The object starts
        // inactive so TMP_InputField's Awake (which would create its own text component) runs only
        // after the pieces are wired up.
        private static TMPro.TMP_InputField CreateInputField(ModsMenuPopup menu, string initial)
        {
            Transform parent = menu.objectParent;
            if (parent == null)
            {
                return null;
            }

            TMPro.TMP_FontAsset font = null;
            foreach (var text in parent.GetComponentsInChildren<TMPro.TMP_Text>())
            {
                if (text != null && text.font != null)
                {
                    font = text.font;
                    break;
                }
            }
            if (font == null)
            {
                font = Resources.GetBuiltinResource<TMPro.TMP_FontAsset>("LegacyRuntime.ttf");
            }

            var holder = new GameObject("CloudSaveInput", typeof(RectTransform));
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);

            var rect = holder.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(420f, 40f);
            var layout = holder.AddComponent<LayoutElement>();
            layout.minHeight = 40f;
            layout.preferredHeight = 40f;
            var background = holder.AddComponent<Image>();
            background.color = new Color(0.12f, 0.12f, 0.12f, 1f);

            var textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(holder.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var tmp = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            tmp.fontSize = 20f;
            tmp.font = font;
            tmp.color = Color.white;
            tmp.text = initial;

            var input = holder.AddComponent<TMPro.TMP_InputField>();
            input.textComponent = tmp;
            input.fontAsset = font;
            input.lineType = TMPro.TMP_InputField.LineType.SingleLine;
            input.text = initial;
            holder.SetActive(true);

            return input;
        }

        private static int GetChildCount(ModsMenuPopup menu)
        {
            return menu.objectParent != null ? menu.objectParent.childCount : 0;
        }

        private static void RefreshLater(ModsMenuPopup menu)
        {
            MenuRunner.RunNextFrame(() => RefreshMenu(menu));
        }

        private static void RefreshMenu(ModsMenuPopup menu)
        {
            if (menu == null || menu.objectParent == null)
            {
                return;
            }

            for (int i = menu.objectParent.childCount - 1; i >= 0; i--)
            {
                var child = menu.objectParent.GetChild(i);
                if (child != null)
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }

            Build(menu);
        }

        private static void ResizeLastCreatedButton(ModsMenuPopup menu, int childCountBefore)
        {
            if (menu == null || menu.objectParent == null)
            {
                return;
            }

            if (menu.objectParent.childCount <= childCountBefore)
            {
                return;
            }

            var buttonTransform = menu.objectParent.GetChild(menu.objectParent.childCount - 1);
            if (buttonTransform == null)
            {
                return;
            }

            var button = buttonTransform.GetComponent<Button>();
            if (button == null)
            {
                return;
            }

            var rect = button.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.sizeDelta = new Vector2(rect.sizeDelta.x * 1.1f, rect.sizeDelta.y * 1.45f);
            }

            var label = button.GetComponentInChildren<TMPro.TMP_Text>();
            if (label != null)
            {
                label.fontSize *= 1.2f;
            }
        }
    }
}
