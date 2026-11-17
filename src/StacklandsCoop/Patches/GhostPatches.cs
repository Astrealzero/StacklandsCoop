using HarmonyLib;
using StacklandsCoop.Net;
using UnityEngine;

namespace StacklandsCoop.Patches
{
    /// <summary>
    /// Client "ghost" guards. On a client the world clock is pinned and any game-side
    /// settlement (month end, harvest payout, saving, clicks) is skipped: the host owns
    /// the simulation and the snapshot layer paints the result.
    /// </summary>
    internal static class GhostLog
    {
        public static void Once(string name)
        {
            CoopLog.Info("ghost patch active: " + name);
        }
    }

    [HarmonyPatch(typeof(WorldManager), "get_TimeScale")]
    internal static class Patch_TimeScale
    {
        private static bool Prefix(ref float __result)
        {
            if (!GhostMode.Enabled)
            {
                return true;
            }

            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldManager), "get_PhysicsTimeScale")]
    internal static class Patch_PhysicsTimeScale
    {
        private static bool Prefix(ref float __result)
        {
            if (!GhostMode.Enabled)
            {
                return true;
            }

            __result = 1f;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldManager), "EndOfMonth")]
    internal static class Patch_EndOfMonth
    {
        private static bool Prefix()
        {
            if (!GhostMode.Enabled)
            {
                return true;
            }

            CoopLog.Info("ghost: 跳过月末结算");
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), new[] { typeof(SaveGame) })]
    internal static class Patch_SaveGame
    {
        private static bool _logged;

        private static bool Prefix()
        {
            if (GhostMode.Enabled)
            {
                // Proves the client never persists the host's world; the host owns the save.
                if (!_logged)
                {
                    _logged = true;
                    CoopLog.Info("save suppressed (client)");
                }

                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), new[] { typeof(bool) })]
    internal static class Patch_SaveBool
    {
        private static bool Prefix()
        {
            return !GhostMode.Enabled;
        }
    }

    [HarmonyPatch(typeof(Harvestable), nameof(Harvestable.CompleteHarvest))]
    internal static class Patch_CompleteHarvest
    {
        private static bool Prefix()
        {
            return !GhostMode.Enabled;
        }
    }

    /// <summary>
    /// A client's world is a mirror: while it rebuilds, it can briefly hold zero cards,
    /// which the game would read as "all villagers are dead" and flip to GameOver.
    /// Only the host may decide the run is lost.
    /// </summary>
    [HarmonyPatch(typeof(WorldManager), "CheckAllVillagersDead")]
    internal static class Patch_CheckAllVillagersDead
    {
        private static bool Prefix(ref bool __result)
        {
            if (!GhostMode.Enabled)
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.Clicked))]
    internal static class Patch_CardClicked
    {
        private static bool Prefix(GameCard __instance)
        {
            if (!GhostMode.Enabled)
            {
                return true;
            }

            CommandSubmit.ClickCard(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(Boosterpack), nameof(Boosterpack.Clicked))]
    internal static class Patch_BoosterClicked
    {
        private static bool Prefix(Boosterpack __instance)
        {
            CoopLog.Info("booster clicked patch: ghost=" + GhostMode.Enabled + " pack=" + (__instance == null ? "null" : __instance.BoosterId));
            if (!GhostMode.Enabled)
            {
                return true;
            }

            CommandSubmit.ClickPack(__instance);
            return false;
        }
    }
}
