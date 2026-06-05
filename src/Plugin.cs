using HarmonyLib;
using UnityEngine;

namespace SPSMod
{
    public class Plugin : IPuckPlugin
    {
        public const string MOD_NAME = "SPSMod";
        public const string MOD_VERSION = "1.0.0";
        public const string MOD_GUID = "pw.stellaric.sps.stats";

        private static readonly Harmony _harmony = new(MOD_GUID);

        public bool OnEnable()
        {
            Plugin.Log($"Enabling v{MOD_VERSION}...");

            try
            {
                _harmony.PatchAll();
                Plugin.Log("Harmony patches applied");

                StatsTracker.Initialize();

                Plugin.Log("Enabled!");
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.LogError($"Failed to enable: {ex.Message}");
                return false;
            }
        }

        public bool OnDisable()
        {
            Plugin.Log("Disabling...");

            StatsTracker.Shutdown();
            _harmony.UnpatchSelf();

            Plugin.Log("Disabled!");
            return true;
        }

        // ── Logging ──────────────────────────────────────────────────────

        public static void Log(string msg) =>
            Debug.Log($"[{MOD_NAME}] {msg}");

        public static void LogError(string msg) =>
            Debug.LogError($"[{MOD_NAME}] {msg}");

        public static void LogWarning(string msg) =>
            Debug.LogWarning($"[{MOD_NAME}] {msg}");
    }
}
