using System;
using HarmonyLib;
using StacklandsCoop.Net;
using StacklandsCoop.Session;
using StacklandsCoop.World;
using UnityEngine;

namespace StacklandsCoop.Patches
{
    /// <summary>
    /// Client drag wiring: the local drag is kept for feel, but pickup and drop are
    /// forwarded to the host, which performs the only real stack operation.
    /// </summary>
    [HarmonyPatch(typeof(Draggable), "StartDragging")]
    internal static class Patch_StartDragging
    {
        private static bool Prefix(Draggable __instance)
        {
            if (!GhostMode.Enabled)
            {
                return true;
            }

            try
            {
                var card = __instance as GameCard;
                if (card != null)
                {
                    CommandSubmit.PickUp(card);
                }
                else
                {
                    var pack = __instance as Boosterpack;
                    if (pack != null)
                    {
                        CommandSubmit.PackPickUp(pack);
                    }
                }
            }
            catch (Exception ex)
            {
                CoopLog.Error("上报拾取失败: " + ex);
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(WorldManager), "DropCard")]
    internal static class Patch_DropCard
    {
        private static bool Prefix(WorldManager __instance)
        {
            if (!GhostMode.Enabled)
            {
                return true;
            }

            GameCard card = __instance.DraggingCard;
            if (card == null)
            {
                // A booster pack is draggable too and never becomes WorldManager.DraggingCard,
                // so it has to be forwarded here or the host never sees it move.
                var pack = __instance.DraggingDraggable as Boosterpack;
                if (pack == null)
                {
                    return true;
                }

                try
                {
                    CommandSubmit.PackDrop(pack, pack.transform.position);
                    pack.StopDragging();
                    __instance.SetDraggingDraggableToNull();
                }
                catch (Exception ex)
                {
                    CoopLog.Error("上报卡包丢弃失败: " + ex);
                }

                return false;
            }

            try
            {
                CommandSubmit.Drop(card, card.transform.position);
            }
            catch (Exception ex)
            {
                CoopLog.Error("上报丢弃失败: " + ex);
            }

            // Predicted stack: the host is the authority, but waiting for a round trip makes
            // a drop feel dead. Stack locally now; the next snapshot keeps or undoes it.
            try
            {
                __instance.NearbyCardTarget = null;
                card.StackUpdate = true;
                __instance.CheckIfCanAddOnStack(card);
            }
            catch (Exception ex)
            {
                CoopLog.Warn("本地堆叠预测失败: " + ex.Message);
            }

            // The host decides the final stack. Locally we only end the drag; the card
            // keeps its position until the next snapshot reconciles it.
            try
            {
                var dragging = __instance.DraggingDraggable;
                if (dragging != null)
                {
                    dragging.StopDragging();
                }

                __instance.SetDraggingDraggableToNull();
            }
            catch (Exception ex)
            {
                CoopLog.Error("结束本地拖拽失败: " + ex);
            }

            return false;
        }
    }
}
