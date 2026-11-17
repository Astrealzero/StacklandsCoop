using System;
using System.Collections.Generic;
using StacklandsCoop.Protocol;
using StacklandsCoop.Session;
using UnityEngine;

namespace StacklandsCoop.World
{
    /// <summary>
    /// Host side of the snapshot sync: assigns stable netIds, builds a full world
    /// snapshot and broadcasts it when the world changed (plus a slow heartbeat).
    /// </summary>
    public sealed class HostWorld
    {
        public static readonly HostWorld Instance = new HostWorld();

        private const float SnapshotInterval = 0.2f;
        private const float HeartbeatInterval = 5f;
        private const float TimerRepaintInterval = 0.5f;

        /// <summary>Set by TickTimers each frame; true while at least one bar is filling.</summary>
        private bool _timersRunning;
        private const uint BoosterIdBase = 1000000u;

        private readonly Dictionary<GameCard, uint> _cardIds = new Dictionary<GameCard, uint>();
        private readonly Dictionary<Boosterpack, uint> _boosterIds = new Dictionary<Boosterpack, uint>();
        private uint _nextCardId = 1u;
        private uint _nextBoosterId = BoosterIdBase;
        private readonly HashSet<GameCard> _seen = new HashSet<GameCard>();
        private float _nextSend;
        private float _lastSendTime;
        private uint _lastHash;
        private int _lastPeerCount;
        private bool _hasSent;
        private uint _revision;

        public uint Revision { get { return _revision; } }

        private readonly Dictionary<uint, GameCard> _reverse = new Dictionary<uint, GameCard>();
        private readonly Dictionary<uint, Boosterpack> _packReverse = new Dictionary<uint, Boosterpack>();

        public bool TryGetPack(uint netId, out Boosterpack pack)
        {
            if (_packReverse.TryGetValue(netId, out pack) && pack != null)
            {
                return true;
            }

            pack = null;
            return false;
        }

        public uint NetIdOfPack(Boosterpack pack)
        {
            uint id;
            if (pack != null && _boosterIds.TryGetValue(pack, out id))
            {
                return id;
            }

            return 0u;
        }

        /// <summary>NetId of a live card (0 when unknown).</summary>
        public uint NetIdOf(GameCard card)
        {
            uint id;
            if (card != null && _cardIds.TryGetValue(card, out id))
            {
                return id;
            }

            return 0u;
        }

        public bool TryGetCard(uint netId, out GameCard card)
        {
            if (_reverse.TryGetValue(netId, out card) && card != null)
            {
                return true;
            }

            card = null;
            return false;
        }

        /// <summary>Skip the snapshot interval: next Tick paints immediately after a command.</summary>
        public void RequestImmediate()
        {
            _nextSend = 0f;
            _hasSent = false;
        }

        /// <summary>Called from CoopRuntime.Update.</summary>
        public void Tick()
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Host || session.RemotePeerCount <= 0)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextSend)
            {
                return;
            }

            _nextSend = now + SnapshotInterval;

            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.AllCards == null)
            {
                return;
            }

            WorldSnapshot snap = Build(wm);
            if (snap == null)
            {
                return;
            }

            if (snap.Shops.Count != _lastShopCount)
            {
                _lastShopCount = snap.Shops.Count;
                CoopLog.Info("shop collect count=" + snap.Shops.Count);
            }

            // Repaint on change; a freshly joined peer gets a full copy, and a slow
            // heartbeat repairs any drift the clients accumulated in between.
            int peerCount = session.RemotePeerCount;
            bool peerJoined = peerCount > _lastPeerCount;
            _lastPeerCount = peerCount;

            bool heartbeat = now - _lastSendTime >= HeartbeatInterval;

            // Timers are not part of the hash (their value changes every frame), so a running
            // timer would never repaint. Refresh at 2 Hz while any bar is ticking; clients use
            // those samples to measure how fast the host clock advances.
            bool timerRepaint = _timersRunning && now - _lastSendTime >= TimerRepaintInterval;
            WorldManager wmNow = WorldManager.instance;
            uint draggingNetId = wmNow == null ? 0u : NetIdOf(wmNow.DraggingCard);
            uint hash = Hash(snap, draggingNetId);
            if (!peerJoined && !heartbeat && !timerRepaint && _hasSent && hash == _lastHash)
            {
                return;
            }

            _lastSendTime = now;
            _hasSent = true;
            _lastHash = hash;

            _revision++;
            snap.Revision = _revision;
            int bytes = 0;
            byte[] head = SnapshotCodec.SyncStart(snap);
            bytes += head.Length;
            session.Broadcast(head);
            for (int i = 0; i < snap.Cards.Count; i += SnapshotCodec.MaxCardsPerChunk)
            {
                int count = Math.Min(SnapshotCodec.MaxCardsPerChunk, snap.Cards.Count - i);
                byte[] chunk = SnapshotCodec.ChunkCards(snap.Cards, i, count);
                bytes += chunk.Length;
                session.Broadcast(chunk);
            }

            for (int i = 0; i < snap.Boosters.Count; i += SnapshotCodec.MaxBoostersPerChunk)
            {
                int count = Math.Min(SnapshotCodec.MaxBoostersPerChunk, snap.Boosters.Count - i);
                byte[] chunk = SnapshotCodec.ChunkBoosters(snap.Boosters, i, count);
                bytes += chunk.Length;
                session.Broadcast(chunk);
            }

            byte[] tail = SnapshotCodec.SyncEnd(snap.Revision);
            bytes += tail.Length;
            session.Broadcast(tail);
            _lastSnapshotBytes = bytes;
            // A snapshot already carries every running timer, so drop the "already announced"
            // bookkeeping: the next timer tick re-announces them, which is what makes timers
            // started before a player joined show up for that player too.
            _timerSent.Clear();
            LogThroughput(now, bytes, snap);
            int running = 0;
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                if (snap.Cards[i].TimerTarget > 0.01f)
                {
                    running++;
                }
            }

            CoopLog.Info("snapshot sent rev=" + _revision +
                         " cards=" + snap.Cards.Count +
                         " boosters=" + snap.Boosters.Count +
                         " shops=" + snap.Shops.Count +
                         " board=" + (snap.BoardId ?? string.Empty) +
                         " timers=" + running);
        }

        private int _lastSnapshotBytes;
        private int _lastShopCount = -1;
        private int _snapshotsInWindow;
        private long _bytesInWindow;
        private float _windowStartedAt;
        private float _nextThroughputLog;

        /// <summary>
        /// Bandwidth sanity check: one line every five seconds with the average snapshot
        /// size and the implied per-client rate, so a runaway snapshot is visible in logs.
        /// </summary>
        private void LogThroughput(float now, int bytes, WorldSnapshot snap)
        {
            if (_windowStartedAt <= 0f)
            {
                _windowStartedAt = now;
            }

            _snapshotsInWindow++;
            _bytesInWindow += bytes;
            if (now < _nextThroughputLog)
            {
                _nextThroughputLog = 0f;
            }

            if (now - _windowStartedAt < 5f)
            {
                return;
            }

            float seconds = now - _windowStartedAt;
            long perSecond = (long)(_bytesInWindow / Mathf.Max(0.001f, seconds));
            CoopLog.Info("snapshot rate avgBytes=" + (long)(_bytesInWindow / Mathf.Max(1, _snapshotsInWindow)) +
                         " bytesPerSec=" + perSecond +
                         " sends=" + _snapshotsInWindow +
                         " cards=" + snap.Cards.Count);
            _windowStartedAt = now;
            _snapshotsInWindow = 0;
            _bytesInWindow = 0;
        }

        private readonly Dictionary<uint, float> _timerSent = new Dictionary<uint, float>();

        /// <summary>
        /// Timers are streamed as start/stop events; the client then advances its own bar
        /// every frame, so the fill stays smooth instead of stepping once per snapshot.
        /// </summary>
        public void TickTimers()
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Host || session.RemotePeerCount <= 0)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.AllCards == null)
            {
                _timersRunning = false;
                return;
            }

            _timersRunning = false;
            for (int i = 0; i < wm.AllCards.Count; i++)
            {
                GameCard card = wm.AllCards[i];
                if (card == null)
                {
                    continue;
                }

                uint netId = NetIdOf(card);
                if (netId == 0u)
                {
                    continue;
                }

                bool running = card.TimerRunning && card.TargetTimerTime > 0.01f;
                float known;
                bool had = _timerSent.TryGetValue(netId, out known);
                if (running)
                {
                    _timersRunning = true;
                }

                if (running && (!had || Math.Abs(known - card.TargetTimerTime) > 0.01f))
                {
                    _timerSent[netId] = card.TargetTimerTime;
                    session.Broadcast(Messages.TimerStart(netId, card.TargetTimerTime));
                }
                else if (!running && had)
                {
                    _timerSent.Remove(netId);
                    session.Broadcast(Messages.TimerStop(netId));
                }
            }
        }

        private static uint Hash(WorldSnapshot snap, uint draggingNetId)
        {            uint h = 2166136261u;
            h = Mix(h, (uint)snap.Month);
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord c = snap.Cards[i];
                h = Mix(h, c.NetId);
                h = Mix(h, c.ParentNetId);
                h = Mix(h, c.Index);
                h = Mix(h, c.FaceUp ? 1u : 0u);
                h = Mix(h, c.Foil ? 1u : 0u);
                h = Mix(h, c.Damage);
                h = Mix(h, c.Health);
                h = Mix(h, c.EquipNetId);
                h = Mix(h, c.WorkerNetId);
                h = Mix(h, c.WorkerIndex);
                if (c.NetId != draggingNetId)
                {
                    // A dragged card moves continuously and is streamed separately.
                    h = Mix(h, (uint)(c.X * 20f));
                    h = Mix(h, (uint)(c.Z * 20f));
                }
            }

            for (int i = 0; i < snap.Boosters.Count; i++)
            {
                BoosterRecord b = snap.Boosters[i];
                h = Mix(h, b.NetId);
                h = Mix(h, (uint)b.TimesOpened);
                // Pack position is not streamed anywhere else: without it here the host could
                // drag a pack around and no snapshot would ever be sent.
                h = Mix(h, (uint)(b.X * 20f));
                h = Mix(h, (uint)(b.Z * 20f));
            }

            for (int i = 0; i < snap.Shops.Count; i++)
            {
                ShopRecord s = snap.Shops[i];
                h = Mix(h, s.NetId);
                h = Mix(h, s.Kind);
                h = Mix(h, (uint)s.A);
                h = Mix(h, (uint)s.B);
            }

            // Conflicts only get a box while they exist, and starting/ending one does not
            // always move another hashed value (a fight can start before the first hit), so
            // the membership has to be part of the hash too.
            h = Mix(h, (uint)snap.Conflicts.Count);
            for (int i = 0; i < snap.Conflicts.Count; i++)
            {
                List<uint> group = snap.Conflicts[i];
                h = Mix(h, (uint)group.Count);
                for (int g = 0; g < group.Count; g++)
                {
                    h = Mix(h, group[g]);
                }
            }

            return h;
        }

        private static uint Mix(uint h, uint v)
        {
            h ^= v;
            return h * 16777619u;
        }

        private WorldSnapshot Build(WorldManager wm)
        {
            var snap = new WorldSnapshot
            {
                Epoch = SessionController.Instance.Epoch,
                Month = wm.CurrentMonth,
                MonthTimer = wm.MonthTimer,
                BoardId = wm.CurrentBoard != null ? wm.CurrentBoard.Id : "main",
                QuestsCompleted = wm.QuestsCompleted,
                DefsHash = Protocol.Fingerprint.DefsHash(),
                SpeedUp = wm.SpeedUp <= 0f ? 0f : wm.SpeedUp
            };

            // Pass 1: give every live card a stable netId.
            _reverse.Clear();
            _seen.Clear();
            for (int i = wm.AllCards.Count - 1; i >= 0; i--)
            {
                GameCard card = wm.AllCards[i];
                if (card == null)
                {
                    wm.AllCards.RemoveAt(i);
                    continue;
                }

                if (!_cardIds.ContainsKey(card))
                {
                    _cardIds[card] = _nextCardId++;
                }

                _seen.Add(card);
            }

            for (int i = 0; i < wm.AllBoosters.Count; i++)
            {
                Boosterpack pack = wm.AllBoosters[i];
                if (pack == null)
                {
                    continue;
                }

                if (!_boosterIds.ContainsKey(pack))
                {
                    _boosterIds[pack] = _nextBoosterId++;
                }
            }

            // Pass 2: build records. Index is filled from the real chain below.
            var recByCard = new Dictionary<GameCard, CardRecord>();
            for (int i = 0; i < wm.AllCards.Count; i++)
            {
                GameCard card = wm.AllCards[i];
                if (card == null || card.CardData == null)
                {
                    continue;
                }

                uint parentNetId = 0u;
                if (card.Parent != null)
                {
                    uint p;
                    if (_cardIds.TryGetValue(card.Parent, out p))
                    {
                        parentNetId = p;
                    }
                }

                ushort index = 0;

                Vector3 pos = card.transform.position;
                var record = new CardRecord
                {
                    NetId = _cardIds[card],
                    ParentNetId = parentNetId,
                    Index = index,
                    CardId = card.CardData.Id,
                    BoardId = BoardIdOf(wm, card.MyBoard),
                    X = pos.x,
                    Z = pos.z,
                    FaceUp = card.FaceUp,
                    Foil = card.CardData != null && card.CardData.IsFoil,
                    Damage = (byte)(card.CardData != null && card.CardData.IsDamaged ? 1 : 0),
                    TimerCurrent = card.TimerRunning ? card.CurrentTimerTime : 0f,
                    TimerTarget = card.TimerRunning ? card.TargetTimerTime : 0f,
                    Health = card.Combatable == null
                        ? ushort.MaxValue
                        : (ushort)Mathf.Clamp(card.Combatable.HealthPoints, 0, ushort.MaxValue - 1),
                    EquipNetId = card.EquipmentHolder != null && _cardIds.TryGetValue(card.EquipmentHolder, out uint equipId) ? equipId : 0u,
                    WorkerNetId = card.WorkerHolder != null && _cardIds.TryGetValue(card.WorkerHolder, out uint workerId) ? workerId : 0u,
                    WorkerIndex = (byte)(card.CardData != null ? Mathf.Clamp(card.CardData.WorkerIndex, 0, 255) : 0)
                };
                snap.Cards.Add(record);
                recByCard[card] = record;
                _reverse[record.NetId] = card;
            }

            // Index = position in the real stack chain, so the client can replay it.
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                if (snap.Cards[i].ParentNetId != 0u)
                {
                    continue;
                }

                GameCard root = null;
                foreach (KeyValuePair<GameCard, CardRecord> pair in recByCard)
                {
                    if (pair.Value.NetId == snap.Cards[i].NetId)
                    {
                        root = pair.Key;
                        break;
                    }
                }

                ushort depth = 1;
                GameCard walk = root == null ? null : root.Child;
                while (walk != null && depth < 60000)
                {
                    CardRecord rec;
                    if (recByCard.TryGetValue(walk, out rec))
                    {
                        rec.Index = depth;
                    }

                    walk = walk.Child;
                    depth++;
                }
            }

            for (int i = 0; i < wm.AllBoosters.Count; i++)
            {
                Boosterpack pack = wm.AllBoosters[i];
                if (pack == null || pack.PackData == null)
                {
                    continue;
                }

                Vector3 pos = pack.transform.position;
                uint packNetId = _boosterIds[pack];
                _packReverse[packNetId] = pack;
                snap.Boosters.Add(new BoosterRecord
                {
                    NetId = packNetId,
                    BoosterId = pack.BoosterId,
                    BoardId = BoardIdOf(wm, pack.MyBoard),
                    X = pos.x,
                    Z = pos.z,
                    TimesOpened = pack.TimesOpened
                });
            }

            // Conflict membership: the client cannot run Conflict.UpdateConflictArrows
            // (frozen time scale), so it needs the participants to draw its own marker.
            try
            {
                List<Conflict> conflicts = wm.GetAllConflicts();
                if (conflicts != null)
                {
                    for (int i = 0; i < conflicts.Count && i < 32; i++)
                    {
                        Conflict conflict = conflicts[i];
                        if (conflict == null || conflict.Participants == null || conflict.Participants.Count < 2)
                        {
                            continue;
                        }

                        var group = new List<uint>(conflict.Participants.Count);
                        for (int j = 0; j < conflict.Participants.Count; j++)
                        {
                            Combatable combatable = conflict.Participants[j];
                            if (combatable == null)
                            {
                                continue;
                            }

                            GameCard card = combatable.GetComponentInParent<GameCard>();
                            uint netId;
                            if (card != null && _cardIds.TryGetValue(card, out netId))
                            {
                                group.Add(netId);
                            }
                        }

                        if (group.Count >= 2)
                        {
                            snap.Conflicts.Add(group);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("收集冲突失败: " + ex.Message);
            }

            // Shop counters (price already paid) live on the CardTarget, not in the card
            // data, so they need their own channel or the client keeps showing the full price.
            try
            {
                List<CardTarget> targets = wm.CardTargets;
                if (targets != null)
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        CardTarget target = targets[i];
                        if (target == null)
                        {
                            continue;
                        }

                        GameCard owner = target.GetComponentInParent<GameCard>();
                        uint netId = 0u;
                        if (owner != null)
                        {
                            _cardIds.TryGetValue(owner, out netId);
                        }

                        var box = target as BuyBoosterBox;
                        if (box != null)
                        {
                            Vector3 shopPos = target.transform.position;
                            snap.Shops.Add(new ShopRecord
                            {
                                NetId = netId,
                                Kind = 0,
                                A = box.StoredCostAmount,
                                B = box.GetCost(),
                                Text = box.BuyText != null ? box.BuyText.text : string.Empty,
                                X = shopPos.x,
                                Z = shopPos.z
                            });
                        }
                    }
                }

                // Merchant and TravellingCart are CardData subclasses (the card itself),
                // not CardTargets: their counters also drive the text on the card.
                for (int i = 0; i < wm.AllCards.Count; i++)
                {
                    GameCard card = wm.AllCards[i];
                    if (card == null || card.CardData == null)
                    {
                        continue;
                    }

                    uint netId;
                    if (!_cardIds.TryGetValue(card, out netId))
                    {
                        continue;
                    }

                    var merchant = card.CardData as Merchant;
                    if (merchant != null)
                    {
                        snap.Shops.Add(new ShopRecord
                        {
                            NetId = netId, Kind = 1, A = merchant.AmountGiven, B = merchant.AmountNeeded
                        });
                        continue;
                    }

                    var cart = card.CardData as TravellingCart;
                    if (cart != null)
                    {
                        snap.Shops.Add(new ShopRecord
                        {
                            NetId = netId, Kind = 2, A = cart.ItemsBought, B = cart.GoldToUse
                        });
                    }
                }
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("收集商店状态失败: " + ex.Message);
            }

            // Drop ids of cards that no longer exist so the table cannot grow forever.
            if (_cardIds.Count > wm.AllCards.Count * 2 + 64)
            {
                var dead = new List<GameCard>();
                foreach (KeyValuePair<GameCard, uint> pair in _cardIds)
                {
                    if (pair.Key == null || !_seen.Contains(pair.Key))
                    {
                        dead.Add(pair.Key);
                    }
                }

                for (int i = 0; i < dead.Count; i++)
                {
                    _cardIds.Remove(dead[i]);
                }
            }

            return snap;
        }

        private static string BoardIdOf(WorldManager wm, GameBoard board)
        {
            if (board != null && !string.IsNullOrEmpty(board.Id))
            {
                return board.Id;
            }

            if (wm.CurrentBoard != null)
            {
                return wm.CurrentBoard.Id;
            }

            return "main";
        }
    }
}
