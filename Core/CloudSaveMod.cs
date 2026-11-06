using System;
using System.Reflection;
using HarmonyLib;
using Silk;
using Logger = Silk.Logger;

namespace CloudSave
{
    [SilkMod(ModName, new string[] { "AeonS" }, "0.3.0", "0.7.0", ModName, 1)]
    public class CloudSaveMod : SilkMod
    {
        // Silk shows this name in its mod list and passes it to Announcer.ModsPopup as the menu title.
        public const string ModName = "SpiderHeckCloudSave";
        private const string HarmonyId = "com.AeonS.SpiderHeckCloudSave";

        public static CloudSaveMod Instance { get; private set; }

        private static string _version;
        public static string Version
        {
            get
            {
                if (_version == null)
                {
                    var version = Assembly.GetExecutingAssembly().GetName().Version;
                    _version = $"{version.Major}.{version.Minor}.{version.Build}";
                }
                return _version;
            }
        }

        public override void Initialize()
        {
            Instance = this;
            Logger.LogInfo($"Initializing {ModName} v{Version}...");

            try
            {
                new Harmony(HarmonyId).PatchAll();
                Logger.LogInfo("Harmony patches applied.");

                CloudSaveController.StartupCheck();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize: {ex}");
            }
        }

        public override void Unload()
        {
            Logger.LogInfo($"Unloading {ModName}...");

            try
            {
                new Harmony(HarmonyId).UnpatchSelf();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unload error: {ex.Message}");
            }

            Instance = null;
        }
    }
}
