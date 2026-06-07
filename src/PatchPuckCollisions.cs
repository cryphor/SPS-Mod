using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace SPSMod
{
    /// <summary>
    /// Patches Puck collision methods to detect shots and passes.
    /// OnCollisionEnter/Exit are private — Harmony patches via string name.
    /// </summary>
    [HarmonyPatch]
    public static class PatchPuckCollisions
    {
        // Minimum puck Speed to count as a shot attempt (maxSpeed = 30f)
        internal const float SHOT_SPEED_THRESHOLD = 3.0f;

        // Cached layer index for Stick layer — used for cheap early-out before GetComponent
        private static readonly int StickLayer = -1;

        // puck instance ID → steamId of player who last hit it with a stick
        internal static readonly Dictionary<int, string> LastStickerByPuck = new();

        // puck instance ID → steamId of player whose stick lost contact
        internal static readonly Dictionary<int, string> LastLeaverByPuck = new();

        // Static cctor — resolve layer index once
        static PatchPuckCollisions()
        {
            StickLayer = LayerMask.NameToLayer("Stick");
        }

        /// <summary>
        /// Try to get a Stick component from the collision.
        /// Uses layer check as cheap fast-path before GetComponent heap traversal.
        /// Returns null if the collision doesn't involve a stick.
        /// </summary>
        private static Stick GetStickFromCollision(Collision collision)
        {
            // Layer check: integer comparison, no alloc.
            // Most collisions (walls, ice, bodies) are NOT on the Stick layer,
            // so we skip GetComponent entirely for the common case.
            if (StickLayer >= 0 && collision.gameObject.layer != StickLayer)
                return null;

            return collision.gameObject.GetComponent<Stick>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Puck), "OnCollisionEnter")]
        private static void OnCollisionEnter(Puck __instance, Collision collision)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            Stick stick = GetStickFromCollision(collision);
            if (stick != null && stick.Player != null && stick.Player.SteamId != null)
            {
                string enteringSteamId = stick.Player.SteamId.Value.ToString();
                int puckId = __instance.GetInstanceID();

                // Check for blocked shot BEFORE pass logic
                if (StatsTracker.CheckShotBlocked(enteringSteamId, puckId))
                    return; // blocked — don't record pass, don't update sticker

                // Notify StatsTracker about stick-on-puck contact
                StatsTracker.OnPuckStickHit(enteringSteamId, puckId);

                // stick-to-stick transfer → previous carrier gets a pass
                if (LastStickerByPuck.TryGetValue(puckId, out string prevSteamId) && prevSteamId != enteringSteamId)
                {
                    StatsTracker.RecordPass(prevSteamId);
                }

                LastStickerByPuck[puckId] = enteringSteamId;
                LastLeaverByPuck.Remove(puckId); // stale leaver from destroyed/reset puck
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Puck), "OnCollisionExit")]
        private static void OnCollisionExit(Puck __instance, Collision collision)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            Stick stick = GetStickFromCollision(collision);
            if (stick != null && stick.Player != null && stick.Player.SteamId != null)
            {
                string leavingSteamId = stick.Player.SteamId.Value.ToString();
                int puckId = __instance.GetInstanceID();

                LastLeaverByPuck[puckId] = leavingSteamId;
                LastStickerByPuck.Remove(puckId); // stale sticker from destroyed/reset puck

                // Shot detection: ShotSpeed is already set by the game's OnCollisionExit
                // before this HarmonyPostfix runs. A high ShotSpeed = shot attempt.
                if (__instance.ShotSpeed > SHOT_SPEED_THRESHOLD)
                {
                    string team = stick.Player.Team == PlayerTeam.Blue ? "blue"
                        : stick.Player.Team == PlayerTeam.Red ? "red" : "";
                    string name = stick.Player.Username.Value.ToString();
                    StatsTracker.OnShotAttempt(leavingSteamId, puckId, __instance.ShotSpeed, name, team);
                }
            }
        }

        /// <summary>
        /// Clear all per-puck tracking state.
        /// Called from StatsTracker.FinalizeMatch to prevent unbounded growth.
        /// </summary>
        internal static void ClearDictionaries()
        {
            LastStickerByPuck.Clear();
            LastLeaverByPuck.Clear();
        }
    }
}
