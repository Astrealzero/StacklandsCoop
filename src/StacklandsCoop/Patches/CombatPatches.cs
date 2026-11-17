using HarmonyLib;
using StacklandsCoop.Net;
using StacklandsCoop.Protocol;
using StacklandsCoop.Session;
using StacklandsCoop.World;
using UnityEngine;

namespace StacklandsCoop.Patches
{
    /// <summary>
    /// Combat feedback: the host applies all damage, clients only replay the
    /// floating damage number so fights on a client screen are readable.
    /// </summary>
    internal static class CombatPatches
    {
        [HarmonyPatch(typeof(Combatable), "Damage")]
        internal static class Patch_Damage
        {
            private static void Postfix(Combatable __instance, int damage)
            {
                try
                {
                    if (damage <= 0 || SessionController.Instance.Role != SessionRole.Host)
                    {
                        return;
                    }

                    // Combatables can live on a child collider, so look up the card from the
                    // component upwards instead of assuming it sits on the card root.
                    GameCard card = __instance.GetComponentInParent<GameCard>();
                    if (card == null)
                    {
                        CoopLog.Warn("hit skip: combatable has no GameCard owner");
                        return;
                    }

                    uint netId = HostWorld.Instance.NetIdOf(card);
                    if (netId == 0)
                    {
                        CoopLog.Warn("hit skip: card has no netId (card=" + (card.CardData != null ? card.CardData.Id : "?") + ")");
                        return;
                    }

                    CoopLog.Info("hit broadcast net=" + netId + " dmg=" + damage);
                    SessionController.Instance.Broadcast(Messages.Hit(netId, (ushort)Mathf.Min(damage, ushort.MaxValue), false));
                }
                catch (System.Exception ex)
                {
                    CoopLog.Warn("hit broadcast failed: " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Combatable), "PerformAttack")]
        internal static class Patch_PerformAttack
        {
            private static void Postfix(Combatable __instance, Combatable target)
            {
                try
                {
                    if (target == null || SessionController.Instance.Role != SessionRole.Host)
                    {
                        return;
                    }

                    GameCard attackerCard = __instance.GetComponentInParent<GameCard>();
                    GameCard targetCard = target.GetComponentInParent<GameCard>();
                    if (attackerCard == null || targetCard == null)
                    {
                        CoopLog.Warn("attack skip: combatable has no GameCard owner");
                        return;
                    }

                    uint attackerNet = HostWorld.Instance.NetIdOf(attackerCard);
                    uint targetNet = HostWorld.Instance.NetIdOf(targetCard);
                    if (attackerNet == 0 || targetNet == 0)
                    {
                        CoopLog.Warn("attack skip: card has no netId");
                        return;
                    }

                    SessionController.Instance.Broadcast(Messages.Attack(attackerNet, targetNet));
                }
                catch (System.Exception ex)
                {
                    CoopLog.Warn("attack broadcast failed: " + ex.Message);
                }
            }
        }
    }
}
