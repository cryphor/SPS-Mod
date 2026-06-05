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
        // puck instance ID → steamId of player who last hit it with a stick
        internal static readonly Dictionary<int, string> LastStickerByPuck = new();

        // puck instance ID → steamId of player whose stick lost contact
        internal static readonly Dictionary<int, string> LastLeaverByPuck = new();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Puck), "OnCollisionEnter")]
        private static void OnCollisionEnter(Puck __instance, Collision collision)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            Stick stick = collision.gameObject.GetComponent<Stick>();
            if (stick != null && stick.Player != null && stick.Player.SteamId != null)
            {
                string enteringSteamId = stick.Player.SteamId.Value.ToString();
                int puckId = __instance.GetInstanceID();

                // Notify StatsTracker about stick-on-puck contact
                StatsTracker.OnPuckStickHit(enteringSteamId, puckId);

                // stick-to-stick transfer → previous carrier gets a pass
                if (LastStickerByPuck.TryGetValue(puckId, out string prevSteamId) && prevSteamId != enteringSteamId)
                {
                    StatsTracker.RecordPass(prevSteamId);
                }

                LastStickerByPuck[puckId] = enteringSteamId;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Puck), "OnCollisionExit")]
        private static void OnCollisionExit(Puck __instance, Collision collision)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            Stick stick = collision.gameObject.GetComponent<Stick>();
            if (stick != null && stick.Player != null && stick.Player.SteamId != null)
            {
                string leavingSteamId = stick.Player.SteamId.Value.ToString();
                int puckId = __instance.GetInstanceID();

                LastLeaverByPuck[puckId] = leavingSteamId;
            }
        }
    }
}
