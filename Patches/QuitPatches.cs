using System;
using HarmonyLib;
using Logger = Silk.Logger;

namespace CloudSave.Patches
{
    // GameSaveWrapper.OnApplicationQuit runs the game's final save on exit. The postfix hands any
    // pending change to the helper, which waits for the game to close before touching folders.
    [HarmonyPatch(typeof(GameSaveWrapper), nameof(GameSaveWrapper.OnApplicationQuit))]
    public static class QuitPatches
    {
        public static void Postfix()
        {
            try
            {
                CloudSaveController.OnGameExit();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[CloudSave] Error while handing the change to the helper: {ex}");
            }
        }
    }
}
