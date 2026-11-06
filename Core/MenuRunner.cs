using System;
using System.Collections;
using UnityEngine;
using Logger = Silk.Logger;

namespace CloudSave
{
    // Runs menu rebuilds on the next frame. Rebuilding the menu from inside a button's own click
    // destroys that button mid-click, which throws a NullReferenceException (0.1.0 did this).
    public class MenuRunner : MonoBehaviour
    {
        private static MenuRunner _instance;

        public static void RunNextFrame(Action action)
        {
            if (_instance == null)
            {
                var holder = new GameObject("SpiderHeckCloudSave.MenuRunner");
                DontDestroyOnLoad(holder);
                _instance = holder.AddComponent<MenuRunner>();
            }

            _instance.StartCoroutine(Run(action));
        }

        private static IEnumerator Run(Action action)
        {
            yield return null;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[CloudSave] Menu update failed: {ex.Message}");
            }
        }
    }
}
