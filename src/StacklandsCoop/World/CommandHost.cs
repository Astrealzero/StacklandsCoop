using System;
using System.Collections.Generic;
using StacklandsCoop.Protocol;
using StacklandsCoop.Session;
using UnityEngine;

namespace StacklandsCoop.World
{
    /// <summary>
    /// Host side command handler: turns client intents into real game actions.
    /// The host is the only place these run, so the dropped stack is authoritative
    /// and reaches everyone through the snapshot layer.
    /// </summary>
    public sealed class CommandHost
    {
        public static readonly CommandHost Instance = new CommandHost();

        // 60 Hz: motion is the only traffic that needs this rate, and it travels unreliable.
        private const float PosInterval = 0.0166f;

        private readonly Dictionary<uint, int> _locks = new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> _held = new Dictionary<uint, int>();
        private readonly Dictionary<uint, float> _lastPackClick = new Dictionary<uint, float>();
        private readonly Dictionary<uint, int> _heldPacks = new Dictionary<uint, int>();
        private float _nextPos;

        public void Handle(int peer, byte[] payload)
        {
            Command cmd;
            try
            {
                cmd = Command.Parse(payload);
            }
            catch (Exception ex)
            {
                CoopLog.Error("命令解析失败: " + ex.Message);
                return;
            }

            switch (cmd.Type)
            {
                case CmdType.PickUp:
                    PickUp(peer, cmd.NetId);
                    break;
                case CmdType.DropCard:
                    Drop(peer, cmd);
                    break;
                case CmdType.Pos:
                    Move(peer, cmd);
                    break;
                case CmdType.ClickCard:
                    Click(peer, cmd.NetId);
                    break;
                case CmdType.ClickPack:
                    ClickPack(peer, cmd.NetId);
                    break;
                case CmdType.PackPickUp:
                    PackPickUp(peer, cmd.NetId);
                    break;
                case CmdType.PackDrop:
                    PackDrop(peer, cmd);
                    break;
                case CmdType.PackPos:
                    PackMove(peer, cmd);
                    break;
                default:
                    CoopLog.Warn("未实现的命令 " + cmd.Type);
                    break;
            }

            // Repaint the clients right away instead of waiting for the next tick;
            // a dropped/built card must show up without a visible stall.
            if (cmd.Type != CmdType.Pos)
            {
                HostWorld.Instance.RequestImmediate();
            }
        }

        private void PickUp(int peer, uint netId)
        {
            int owner;
            if (_locks.TryGetValue(netId, out owner) && owner != peer)
            {
                Notice(peer, "busy:" + UI.Loc.T("that card is in use", "被使用中"));
                return;
            }

            GameCard card;
            if (!HostWorld.Instance.TryGetCard(netId, out card))
            {
                Notice(peer, "missing:" + UI.Loc.T("card not found", "卡牌不存在"));
                return;
            }

            _locks[netId] = peer;
            _held[netId] = peer;
            CoopLog.Info("命令 PickUp peer=" + peer + " netId=" + netId);

            try
            {
                // Detach from whatever stack it sat in; children stay attached so a
                // picked up stack travels as a whole, like the vanilla drag.
                if (card.Parent != null)
                {
                    card.RemoveFromParent();
                }

                if (!card.BeingDragged)
                {
                    card.StartDragging();
                }
            }
            catch (Exception ex)
            {
                CoopLog.Error("主机执行拾取失败: " + ex);
            }
        }

        private void Move(int peer, Command cmd)
        {
            int owner;
            if (!_held.TryGetValue(cmd.NetId, out owner) || owner != peer)
            {
                return;
            }

            GameCard card;
            if (!HostWorld.Instance.TryGetCard(cmd.NetId, out card) || card == null)
            {
                return;
            }

            var pos = new Vector3(cmd.X, card.transform.position.y, cmd.Z);
            card.TargetPosition = pos;
            card.transform.position = pos;
        }

        private void Drop(int peer, Command cmd)
        {
            GameCard card;
            if (!HostWorld.Instance.TryGetCard(cmd.NetId, out card) || card == null)
            {
                CoopLog.Warn("丢弃命令找不到卡 netId=" + cmd.NetId);
                return;
            }

            int owner;
            if (_locks.TryGetValue(cmd.NetId, out owner) && owner != peer)
            {
                Notice(peer, "busy:" + UI.Loc.T("that card is in use", "被使用中"));
                return;
            }

            _locks.Remove(cmd.NetId);
            _held.Remove(cmd.NetId);

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            try
            {
                var pos = new Vector3(cmd.X, card.transform.position.y, cmd.Z);
                card.TargetPosition = pos;
                card.transform.position = pos;

                // Physics queries (overlap based stack checks) need the new transform.
                Physics.SyncTransforms();

                // The host never drags through the vanilla path, so settle the drop here:
                // card targets first (stoves, sell slots, ...), otherwise stack on a card.
                CardTarget target = FindTarget(wm, card, pos);
                CoopLog.Info("命令 DropCard peer=" + peer + " netId=" + cmd.NetId + " pos=" + cmd.X + "," + cmd.Z
                             + " target=" + (target == null ? "none" : target.name));
                if (target != null)
                {
                    target.CardDropped(card);
                }
                else
                {
                    wm.CheckIfCanAddOnStack(card);
                }

            }
            catch (Exception ex)
            {
                CoopLog.Error("执行丢弃失败: " + ex);
            }

            // Vanilla runs the landed-card hooks after the stack/target operation.
            FinishDrop(card, cmd.NetId);
        }

        /// <summary>
        /// Vanilla's post-drop hooks, copied from GameCard.StopDragging:
        /// WorldManager.DropCard ends with DraggingDraggable.StopDragging(), and
        /// GameCard.StopDragging ends with CardData.StoppedDragging() - a virtual hook
        /// that equips an Equipable onto the stack it just landed in
        /// (Equipable.StoppedDragging), fills a worker slot (Worker.StoppedDragging)
        /// and runs every other card's landed behaviour. The host never drags through
        /// the vanilla path, so without this a tool dropped on a villager did nothing.
        ///
        /// GameCard.StopDragging itself is deliberately not called: it also plays the
        /// drop sound and, when the drag threshold was never reached on the host, turns
        /// the release into a Clicked() call - which would use the card a second time.
        /// </summary>
        private static void FinishDrop(GameCard card, uint netId)
        {
            try
            {
                for (GameCard child = card.Child; child != null; child = child.Child)
                {
                    child.BeingDragged = false;
                }

                card.StackUpdate = true;
                if (card.CardData != null)
                {
                    card.CardData.StoppedDragging();
                }

                CoopLog.Info("落地钩子 net=" + netId + " data=" + (card.CardData == null ? "null" : card.CardData.Id)
                             + " equipped=" + card.IsEquipped + " worker=" + (card.WorkerHolder != null)
                             + " parent=" + (card.Parent == null ? 0 : 1));
            }
            catch (Exception ex)
            {
                CoopLog.Error("落地钩子失败: " + ex);
            }
            finally
            {
                card.BeingDragged = false;
            }
        }

        private static CardTarget FindTarget(WorldManager wm, GameCard card, Vector3 pos)
        {
            if (wm.CardTargets == null)
            {
                return null;
            }

            float best = 0.9f;
            CardTarget found = null;
            for (int i = 0; i < wm.CardTargets.Count; i++)
            {
                CardTarget target = wm.CardTargets[i];
                if (target == null || !target.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 delta = target.transform.position - pos;
                float dist = new Vector2(delta.x, delta.z).magnitude;
                if (dist > best)
                {
                    continue;
                }

                bool can;
                try
                {
                    can = target.CanHaveCard(card);
                }
                catch (Exception)
                {
                    can = false;
                }

                if (can)
                {
                    best = dist;
                    found = target;
                }
            }

            return found;
        }

        private void Click(int peer, uint netId)
        {
            GameCard card;
            if (!HostWorld.Instance.TryGetCard(netId, out card))
            {
                return;
            }

            try
            {
                card.Clicked();
            }
            catch (Exception ex)
            {
                CoopLog.Error("执行点击失败: " + ex);
            }
        }

        private void ClickPack(int peer, uint netId)
        {
            // One physical click can reach the patch twice (mouse-up click plus the
            // stop-dragging threshold); opening the pack twice would empty it instantly.
            float now = Time.unscaledTime;
            float last;
            if (_lastPackClick.TryGetValue(netId, out last) && now - last < 0.4f)
            {
                return;
            }

            _lastPackClick[netId] = now;

            Boosterpack pack;
            if (!HostWorld.Instance.TryGetPack(netId, out pack))
            {
                Notice(peer, "missing:" + UI.Loc.T("pack not found", "卡包不存在"));
                return;
            }

            try
            {
                CoopLog.Info("命令 ClickPack peer=" + peer + " netId=" + netId);
                pack.Clicked();
            }
            catch (Exception ex)
            {
                CoopLog.Error("执行开包失败: " + ex);
            }
        }

        /// <summary>A client picked a booster pack up; only one player may hold it.</summary>
        private void PackPickUp(int peer, uint netId)
        {
            int owner;
            if (_locks.TryGetValue(netId, out owner) && owner != peer)
            {
                Notice(peer, "busy:" + UI.Loc.T("that card is in use", "被使用中"));
                return;
            }

            Boosterpack pack;
            if (!HostWorld.Instance.TryGetPack(netId, out pack) || pack == null)
            {
                Notice(peer, "missing:" + UI.Loc.T("pack not found", "卡包不存在"));
                return;
            }

            _locks[netId] = peer;
            _heldPacks[netId] = peer;
            CoopLog.Info("命令 PackPickUp peer=" + peer + " netId=" + netId);
        }

        private void PackMove(int peer, Command cmd)
        {
            int owner;
            if (!_heldPacks.TryGetValue(cmd.NetId, out owner) || owner != peer)
            {
                return;
            }

            Boosterpack pack;
            if (!HostWorld.Instance.TryGetPack(cmd.NetId, out pack) || pack == null)
            {
                return;
            }

            var pos = new Vector3(cmd.X, pack.transform.position.y, cmd.Z);
            pack.TargetPosition = pos;
            pack.transform.position = pos;
        }

        private void PackDrop(int peer, Command cmd)
        {
            _heldPacks.Remove(cmd.NetId);
            _locks.Remove(cmd.NetId);

            Boosterpack pack;
            if (!HostWorld.Instance.TryGetPack(cmd.NetId, out pack) || pack == null)
            {
                return;
            }

            var pos = new Vector3(cmd.X, pack.transform.position.y, cmd.Z);
            pack.TargetPosition = pos;
            pack.transform.position = pos;
            CoopLog.Info("命令 PackDrop peer=" + peer + " netId=" + cmd.NetId);
        }

        /// <summary>Broadcast live positions of held cards so everyone sees the drag.</summary>
        public void Tick()
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Host)
            {
                return;
            }

            // The host may be dragging too; its drag never goes through the command
            // channel, so pick it up from WorldManager directly.
            WorldManager wm = WorldManager.instance;
            GameCard hostHeld = wm == null ? null : wm.DraggingCard;
            Draggable hostHeldPack = wm == null ? null : wm.DraggingDraggable;
            if (_held.Count == 0 && hostHeld == null && _heldPacks.Count == 0 && !(hostHeldPack is Boosterpack))
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextPos)
            {
                return;
            }

            _nextPos = now + PosInterval;

            if (hostHeld != null)
            {
                uint heldId = HostWorld.Instance.NetIdOf(hostHeld);
                if (heldId != 0u)
                {
                    Vector3 hp = hostHeld.transform.position;
                    session.BroadcastUnreliable(Messages.CardPos(heldId, hp.x, hp.z));
                }
            }

            foreach (KeyValuePair<uint, int> pair in _held)
            {
                GameCard card;
                if (!HostWorld.Instance.TryGetCard(pair.Key, out card) || card == null)
                {
                    continue;
                }

                Vector3 p = card.transform.position;
                session.BroadcastUnreliable(Messages.CardPos(pair.Key, p.x, p.z));
            }

            // Booster packs are Draggable but never WorldManager.DraggingCard, so the
            // generic branch above misses them; stream the host's own pack drag here.
            if (hostHeldPack is Boosterpack hostPack)
            {
                uint packId = HostWorld.Instance.NetIdOfPack(hostPack);
                if (packId != 0u)
                {
                    Vector3 pp = hostPack.transform.position;
                    session.BroadcastUnreliable(Messages.PackPos(packId, pp.x, pp.z));
                }
            }

            foreach (KeyValuePair<uint, int> pair in _heldPacks)
            {
                Boosterpack pack;
                if (!HostWorld.Instance.TryGetPack(pair.Key, out pack) || pack == null)
                {
                    continue;
                }

                Vector3 pp = pack.transform.position;
                session.BroadcastUnreliable(Messages.PackPos(pair.Key, pp.x, pp.z));
            }
        }

        /// <summary>A client left while holding cards: put them back down.</summary>
        public void ReleasePeer(int peer)
        {
            var ids = new List<uint>();
            foreach (KeyValuePair<uint, int> pair in _held)
            {
                if (pair.Value == peer)
                {
                    ids.Add(pair.Key);
                }
            }

            WorldManager wm = WorldManager.instance;
            for (int i = 0; i < ids.Count; i++)
            {
                uint netId = ids[i];
                _locks.Remove(netId);
                _held.Remove(netId);

                GameCard card;
                if (wm == null || !HostWorld.Instance.TryGetCard(netId, out card) || card == null)
                {
                    continue;
                }

                try
                {
                    wm.NearbyCardTarget = null;
                    wm.CheckIfCanAddOnStack(card);
                    if (card.BeingDragged)
                    {
                        card.BeingDragged = false;
                    }
                }
                catch (Exception ex)
                {
                    CoopLog.Error("释放断线玩家的卡失败: " + ex);
                }
            }

            var staleLocks = new List<uint>();
            foreach (KeyValuePair<uint, int> pair in _locks)
            {
                if (pair.Value == peer)
                {
                    staleLocks.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleLocks.Count; i++)
            {
                _locks.Remove(staleLocks[i]);
            }

            // Packs held by the peer that just left: release the lock so they can be
            // picked up again (their visual position is already where they were).
            var stalePacks = new List<uint>();
            foreach (KeyValuePair<uint, int> pair in _heldPacks)
            {
                if (pair.Value == peer)
                {
                    stalePacks.Add(pair.Key);
                }
            }

            for (int i = 0; i < stalePacks.Count; i++)
            {
                _heldPacks.Remove(stalePacks[i]);
                _locks.Remove(stalePacks[i]);
            }
        }

        private static void Notice(int peer, string text)
        {
            SessionController.Instance.SendTo(peer, Messages.Notice(text));
        }

        public void ReleaseAll()
        {
            _locks.Clear();
            _held.Clear();
            _heldPacks.Clear();
        }
    }
}
