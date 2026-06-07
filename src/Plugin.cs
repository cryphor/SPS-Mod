using HarmonyLib;
using UnityEngine;

namespace SPSMod
{
    public class Plugin : IPuckPlugin
    {
        public const string MOD_NAME = "SPSMod";
        public const string MOD_VERSION = "1.0.0";
        public const string MOD_GUID = "sps";

        private static readonly Harmony _harmony = new(MOD_GUID);

        public bool OnEnable()
        {
            Plugin.Log($"Enabling v{MOD_VERSION}...");

            try
            {
                _harmony.PatchAll();
                Plugin.Log("Harmony patches applied");

                StatsTracker.Initialize();
                ChatTracker.Initialize();

                OptimizeForClientPerformance();

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
            ChatTracker.Shutdown();
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

        // ── Client FPS Optimizations ─────────────────────────────────────

        /// <summary>
        /// Applies conservative quality settings to improve FPS on lower-end machines.
        /// Skips headless/dedicated server builds (no rendering).
        /// </summary>
        private static void OptimizeForClientPerformance()
        {
            if (Application.isBatchMode)
                return;

            Log("Applying client performance optimizations...");

            QualitySettings.shadowDistance = 30f;
            QualitySettings.shadowResolution = ShadowResolution.Low;
            QualitySettings.softVegetation = false;
            QualitySettings.globalTextureMipmapLimit = 1;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
            QualitySettings.pixelLightCount = 1;
            QualitySettings.skinWeights = SkinWeights.OneBone;

            Log("Performance optimizations applied (shadows, textures, lighting)");
        }
    }
}
