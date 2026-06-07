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
        /// Applies aggressive quality settings for 30%+ FPS gain.
        /// Skips headless/dedicated server builds (no rendering).
        /// </summary>
        private static void OptimizeForClientPerformance()
        {
            if (Application.isBatchMode)
                return;

            Log("Applying AGGRESSIVE client performance optimizations...");

            // ── Shadows (biggest GPU impact) ──
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.shadowDistance = 0f;

            // ── Textures ──
            QualitySettings.globalTextureMipmapLimit = 2;    // quarter resolution
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
            QualitySettings.streamingMipmapsActive = false;  // no background streaming

            // ── Lighting ──
            QualitySettings.pixelLightCount = 0;
            QualitySettings.realtimeReflectionProbes = false;

            // ── Geometry ──
            QualitySettings.skinWeights = SkinWeights.OneBone;
            QualitySettings.lodBias = 0.3f;
            QualitySettings.maximumLODLevel = 1;

            // ── Particles ──
            QualitySettings.softParticles = false;
            QualitySettings.particleRaycastBudget = 4;

            // ── Misc ──
            QualitySettings.softVegetation = false;
            QualitySettings.billboardsFaceCameraPosition = false;
            QualitySettings.asyncUploadTimeSlice = 1;
            QualitySettings.asyncUploadBufferSize = 4;

            Log("Aggressive optimizations applied (shadows OFF, quarter-res textures, no reflections, low LOD)");
        }
    }
}
