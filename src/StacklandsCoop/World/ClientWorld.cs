using System;
using System.Collections.Generic;
using StacklandsCoop.Net;
using StacklandsCoop.Protocol;
using StacklandsCoop.Session;
using UnityEngine;

namespace StacklandsCoop.World
{
    /// <summary>
    /// Client side of the snapshot sync: assembles SyncStart/Chunk*/SyncEnd and
    /// reconciles the local world to match the host. Reconciling is a diff (create
    /// missing, destroy stale, re-stack when the order changed) so a repaint does not
    /// destroy cards the player is touching.
    /// </summary>
    public sealed class ClientWorld
    {
        public static readonly ClientWorld Instance = new ClientWorld();

        private WorldSnapshot _pending;
        private WorldSnapshot _last;
        private bool _dirty;
        private bool _defsMismatchLogged;
        private float _lastStackLog;
        private int _appliedCards;
        private int _loggedCards = -1;
        private int _loggedPacks = -1;
        private float _lastApplyLogAt;
        private int _appliedBoosters;
        private readonly Dictionary<uint, GameCard> _byNet = new Dictionary<uint, GameCard>();
        private readonly Dictionary<GameCard, uint> _netOf = new Dictionary<GameCard, uint>();
        private readonly Dictionary<uint, Boosterpack> _packByNet = new Dictionary<uint, Boosterpack>();
        private readonly Dictionary<Boosterpack, uint> _packOf = new Dictionary<Boosterpack, uint>();
        private readonly Dictionary<uint, float> _streamedAt = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> _packStreamedAt = new Dictionary<uint, float>();
        private readonly HashSet<uint> _timerLogged = new HashSet<uint>();
        private string _requestedBoard = string.Empty;

        // Board travel requests: the animated transition is a coroutine that never runs while the
        // ghost client's clock is frozen, so after a grace period we switch the board directly.
        private string _pendingTravel = string.Empty;
        private float _pendingTravelAt;

        // Follow mode: the board the host moved to while we were still holding a card.
        private string _followPending = string.Empty;
        private string _followSeen = string.Empty;
        private float _followSeenAt;
        private bool _suppressFollow;

        // The host advances timers at WorldManager.TimeScale (which includes SpeedUp), and the
        // client clock is frozen, so the client measures the host's rate from consecutive
        // snapshots and advances its own bars at that rate in between.
        private readonly Dictionary<uint, float> _timerRate = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> _timerBias = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> _timerPrevCur = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> _timerPrevAt = new Dictionary<uint, float>();

        // Host's current SpeedUp (1x/2x/3x) from the latest snapshot header. Bars advance at
        // this rate between samples instead of crawling at 1x while the host runs fast.
        private float _hostSpeedUp = 1f;
        private float _monthTimerBase;
        private float _monthTimerBaseAt;

        /// <summary>
        /// The client's own world clock is pinned at time scale 0, so <c>wm.MonthTimer</c> only
        /// moves when a snapshot happens to arrive. Interpolate it locally for the panel clock.
        /// </summary>
        public float EstimatedMonthTimer
        {
            get
            {
                float elapsed = Time.unscaledTime - _monthTimerBaseAt;
                float speed = World.MonthFlow.Instance != null && World.MonthFlow.Instance.HostPaused ? 0f : _hostSpeedUp;
                return _monthTimerBase + Mathf.Max(0f, elapsed) * Mathf.Max(0f, speed);
            }
        }

        /// <summary>Host pause/resume carries its speed so timer bars resume at the right rate.</summary>
        public void SetHostSpeedUp(float speedUp)
        {
            _hostSpeedUp = speedUp > 0.001f ? speedUp : 0f;
        }

        public uint NetIdOfPack(Boosterpack pack)
        {
            uint id;
            if (pack != null && _packOf.TryGetValue(pack, out id))
            {
                return id;
            }

            return 0u;
        }

        public uint NetIdOf(GameCard card)
        {
            uint id;
            if (card != null && _netOf.TryGetValue(card, out id))
            {
                return id;
            }

            return 0u;
        }

        public bool TryGetCard(uint netId, out GameCard card)
        {
            if (_byNet.TryGetValue(netId, out card) && card != null)
            {
                return true;
            }

            card = null;
            return false;
        }

        public void Forget(GameCard card)
        {
            uint id;
            if (card != null && _netOf.TryGetValue(card, out id))
            {
                _netOf.Remove(card);
                _byNet.Remove(id);
                _streamedAt.Remove(id);
                _timerRate.Remove(id);
                _timerPrevCur.Remove(id);
                _timerPrevAt.Remove(id);
            }
        }

        /// <summary>
        /// Drops bookkeeping for netIds the host no longer sends. Without this, a long session
        /// with a lot of card churn keeps growing the per-card dictionaries forever.
        /// </summary>
        private void PruneLive(WorldSnapshot snap, Dictionary<uint, GameCard> desired)
        {
            _liveIds.Clear();
            foreach (KeyValuePair<uint, GameCard> pair in desired)
            {
                _liveIds.Add(pair.Key);
            }

            for (int i = 0; i < snap.Boosters.Count; i++)
            {
                _liveIds.Add(snap.Boosters[i].NetId);
            }

            PruneBookkeeping(_liveIds);
        }

        private void PruneBookkeeping(HashSet<uint> live)
        {
            PruneMap(_streamedAt, live);
            PruneMap(_timerRate, live);
            PruneMap(_timerPrevCur, live);
            PruneMap(_timerPrevAt, live);
        }

        private static void PruneMap<T>(Dictionary<uint, T> map, HashSet<uint> live)
        {
            if (map.Count == 0)
            {
                return;
            }

            _scratch.Clear();
            foreach (uint id in map.Keys)
            {
                if (!live.Contains(id))
                {
                    _scratch.Add(id);
                }
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                map.Remove(_scratch[i]);
            }
        }

        private static readonly List<uint> _scratch = new List<uint>();
        private readonly HashSet<uint> _liveIds = new HashSet<uint>();

        public int AppliedCards { get { return _appliedCards; } }

        public int AppliedBoosters { get { return _appliedBoosters; } }

        private int _skippedApplies;

        /// <summary>Snapshots that reused the existing card set without a rebuild (diagnostics).</summary>
        public int SkippedApplies { get { return _skippedApplies; } }

        /// <summary>When the last snapshot finished applying (unscaled time).</summary>
        public float LastAppliedAt { get; private set; }

        private int _lastConflictCount = -1;
        private List<List<uint>> _conflicts = new List<List<uint>>();

        /// <summary>NetIds of fighting cards, one list per conflict (from the last snapshot).</summary>
        public List<List<uint>> Conflicts { get { return _conflicts; } }

        /// <summary>
        /// True when the host has gone quiet for a while even though the link is up — the
        /// silent failure mode that used to look like "my world stopped syncing".
        /// </summary>
        public bool IsStalled(float staleSeconds)
        {
            if (SessionController.Instance.Role != SessionRole.Client || LastAppliedAt <= 0f)
            {
                return false;
            }

            return Time.unscaledTime - LastAppliedAt > staleSeconds;
        }

        /// <summary>Seconds since the last applied snapshot, for the debug panel.</summary>
        public float SecondsSinceApplied()
        {
            if (LastAppliedAt <= 0f)
            {
                return -1f;
            }

            return Time.unscaledTime - LastAppliedAt;
        }

        /// <summary>Host rejected a command: put the local world back to the last state.</summary>
        public void ReapplyLast()
        {
            if (_last != null)
            {
                // Replaying an older snapshot must not move the player: the board in it can be
                // out of date, and following it here is what made clients bounce between boards.
                _suppressFollow = true;
                try
                {
                    Apply(_last);
                }
                finally
                {
                    _suppressFollow = false;
                }
            }
        }

        /// <summary>Host refused the command (card busy): drop the local drag first.</summary>
        public void HandleReject()
        {
            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            if (wm.DraggingCard != null)
            {
                try
                {
                    wm.DraggingCard.StopDragging();
                    wm.SetDraggingDraggableToNull();
                }
                catch (Exception ex)
                {
                    CoopLog.Error("结束本地拖拽失败: " + ex);
                }
            }

            _applyQueue.Clear();
            ReapplyLast();
        }

        public void Handle(byte[] payload)
        {            if (payload == null || payload.Length == 0)
            {
                return;
            }

            MsgType type = (MsgType)payload[0];
            switch (type)
            {
                case MsgType.SyncStart:
                    HandleStart(payload);
                    break;
                case MsgType.SyncChunk:
                    HandleChunk(payload);
                    break;
                case MsgType.SyncEnd:
                    HandleEnd(payload);
                    break;
                case MsgType.TimerStart:
                    uint timerNetId;
                    float timerTarget;
                    Messages.ParseTimerStart(payload, out timerNetId, out timerTarget);
                    HandleTimerStart(timerNetId, timerTarget);
                    break;
                case MsgType.TimerStop:
                    HandleTimerStop(Messages.ParseTimerStop(payload));
                    break;
                case MsgType.Hit:
                    uint hitNetId;
                    ushort hitDamage;
                    bool veryEffective;
                    Messages.ParseHit(payload, out hitNetId, out hitDamage, out veryEffective);
                    HandleHit(hitNetId, hitDamage, veryEffective);
                    break;
            }
        }

        /// <summary>
        /// Replay the host's damage number. Damage itself is host-authoritative, so this
        /// only spawns the floating text on top of the card that was hit.
        /// </summary>
        public void HandleHit(uint netId, ushort damage, bool veryEffective)
        {
            CoopLog.Info("hit recv net=" + netId + " dmg=" + damage);

            GameCard card;
            if (!_byNet.TryGetValue(netId, out card) || card == null || card.Combatable == null)
            {
                CoopLog.Warn("hit recv: card not found or not combatalbe net=" + netId);
                return;
            }

            CoopLog.Info("hit net=" + netId + " dmg=" + damage + " crit=" + veryEffective);

            try
            {
                HitText text = card.Combatable.CreateHitText(damage.ToString(), null);
                if (text != null && veryEffective)
                {
                    text.SetVeryEffective(true);
                }
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("hit text failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The host says A hit B: replay the lunge locally. Damage and the number
        /// come from separate messages, so this is purely visual.
        /// </summary>
        public void HandleAttack(uint attackerNet, uint targetNet)
        {
            GameCard attacker;
            GameCard target;
            if (!_byNet.TryGetValue(attackerNet, out attacker) || attacker == null)
            {
                return;
            }

            if (!_byNet.TryGetValue(targetNet, out target) || target == null)
            {
                return;
            }

            try
            {
                AttackFx.Play(attacker, target);
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("attack fx failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The host started a timer: build the same bar here and advance it locally, so the
        /// fill moves every frame instead of jumping once per snapshot.
        /// </summary>
        public void HandleTimerStart(uint netId, float target)
        {
            GameCard card;
            if (!_byNet.TryGetValue(netId, out card) || card == null)
            {
                return;
            }

            if (card.CurrentStatusbar == null && !StartTimerOnClient(card, target))
            {
                return;
            }

            card.TargetTimerTime = target;
            card.TimerRunning = true;

            if (_timerLogged.Add(netId))
            {
                CoopLog.Info("timer start net=" + netId + " target=" + target.ToString("0.00"));
            }
        }

        public void HandleTimerStop(uint netId)
        {
            GameCard card;
            if (!_byNet.TryGetValue(netId, out card) || card == null)
            {
                return;
            }

            StopTimerOnClient(card);
        }

        /// <summary>Live position of a card held by another player (host broadcast).</summary>
        public void HandlePos(byte[] payload)
        {
            uint netId;
            float x;
            float z;
            Messages.ParseCardPos(payload, out netId, out x, out z);

            GameCard card;
            if (!_byNet.TryGetValue(netId, out card) || card == null)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm != null && wm.DraggingCard == card)
            {
                return;
            }

            _streamedAt[netId] = Time.unscaledTime;
            AddSample(_posSamples, netId, x, z);
        }

        /// <summary>Live position of a booster pack held by another player.</summary>
        public void HandlePackPos(byte[] payload)
        {
            uint netId;
            float x;
            float z;
            Messages.ParsePackPos(payload, out netId, out x, out z);

            Boosterpack pack;
            if (!_packByNet.TryGetValue(netId, out pack) || pack == null)
            {
                return;
            }

            if (WorldManager.instance != null && WorldManager.instance.DraggingDraggable == pack)
            {
                return;
            }

            AddSample(_packSamples, netId, x, z);
            _packStreamedAt[netId] = Time.unscaledTime;
        }

        private sealed class Sample
        {
            public float T;
            public float X;
            public float Z;
        }

        private readonly Dictionary<uint, List<Sample>> _posSamples = new Dictionary<uint, List<Sample>>();
        private readonly Dictionary<uint, List<Sample>> _packSamples = new Dictionary<uint, List<Sample>>();

        /// <summary>
        /// Latest wanted render position per card. Snapshots and the live stream both feed
        /// this, and TickMotion walks TargetPosition toward it at a bounded speed so a card
        /// that jumps in the data glides on screen instead of teleporting.
        /// </summary>
        private readonly Dictionary<uint, Vector3> _posWant = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, Vector3> _packWant = new Dictionary<uint, Vector3>();
        private readonly HashSet<uint> _packMoveLogged = new HashSet<uint>();

        private const float MaxCardSpeed = 9f;
        private const float MaxPackSpeed = 9f;

        private static void AddSample(Dictionary<uint, List<Sample>> map, uint netId, float x, float z)
        {
            List<Sample> list;
            if (!map.TryGetValue(netId, out list))
            {
                list = new List<Sample>(8);
                map[netId] = list;
            }

            float now = Time.unscaledTime;
            list.Add(new Sample { T = now, X = x, Z = z });

            // Keep roughly the last second of stream.
            while (list.Count > 2 && now - list[0].T > 1f)
            {
                list.RemoveAt(0);
            }
        }

        /// <summary>
        /// Network positions are written to TargetPosition only: Draggable's own update
        /// eases the card toward it, which is exactly the motion the game shows locally.
        /// While a live stream exists the card is sampled RenderDelay in the past and
        /// interpolated between packets, so a 30 Hz stream renders as continuous motion.
        /// </summary>
        public void TickMotion(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            // Must run before the early return below: expiring a silent stream is what
            // hands the card back to the snapshot, and that has to happen even when no
            // samples are left anywhere.
            ExpireStreams(Time.unscaledTime);

            if (_posSamples.Count == 0 && _packSamples.Count == 0 && _posWant.Count == 0 && _packWant.Count == 0)
            {
                return;
            }

            float renderAt = Time.unscaledTime - _renderDelay;
            WorldManager wm = WorldManager.instance;

            foreach (KeyValuePair<uint, List<Sample>> pair in _posSamples)
            {
                GameCard card;
                if (!_byNet.TryGetValue(pair.Key, out card) || card == null)
                {
                    continue;
                }

                if (wm != null && wm.DraggingCard == card)
                {
                    continue;
                }

                if (AttackFx.IsBusy(card))
                {
                    continue;
                }

                // Children follow their parent's stack layout, not the wire.
                if (card.Parent != null)
                {
                    continue;
                }

                float x;
                float z;
                if (!Interpolate(pair.Value, renderAt, out x, out z))
                {
                    continue;
                }

                _posWant[pair.Key] = new Vector3(x, card.transform.position.y, z);
            }

            foreach (KeyValuePair<uint, List<Sample>> pair in _packSamples)
            {
                Boosterpack pack;
                if (!_packByNet.TryGetValue(pair.Key, out pack) || pack == null)
                {
                    continue;
                }

                if (wm != null && wm.DraggingDraggable == pack)
                {
                    continue;
                }

                float x;
                float z;
                if (!Interpolate(pair.Value, renderAt, out x, out z))
                {
                    continue;
                }

                _packWant[pair.Key] = new Vector3(x, pack.transform.position.y, z);
            }

            // Walk every wanted position at a bounded speed. The game's own Draggable
            // easing does the rest, so a card that jumps 2 units in the data takes ~0.2 s
            // to arrive instead of appearing there between two frames.
            _staleWants.Clear();
            foreach (KeyValuePair<uint, Vector3> pair in _posWant)
            {
                GameCard card;
                if (!_byNet.TryGetValue(pair.Key, out card) || card == null)
                {
                    _staleWants.Add(pair.Key);
                    continue;
                }

                if (wm != null && wm.DraggingCard == card)
                {
                    continue;
                }

                if (AttackFx.IsBusy(card))
                {
                    continue;
                }

                // A card that just landed on a stack is laid out by the game itself.
                // Writing TargetPosition for it fights that layout and is the other half
                // of the twitch: drop the want instead of walking the card.
                if (card.Parent != null)
                {
                    _staleWants.Add(pair.Key);
                    continue;
                }

                Ramp(card, pair.Value, dt, MaxCardSpeed);
            }

            for (int i = 0; i < _staleWants.Count; i++)
            {
                _posWant.Remove(_staleWants[i]);
            }

            _staleWants.Clear();
            foreach (KeyValuePair<uint, Vector3> pair in _packWant)
            {
                Boosterpack pack;
                if (!_packByNet.TryGetValue(pair.Key, out pack) || pack == null)
                {
                    _staleWants.Add(pair.Key);
                    continue;
                }

                if (wm != null && wm.DraggingDraggable == pack)
                {
                    continue;
                }

                Ramp(pack, pair.Value, dt, MaxPackSpeed);
            }

            for (int i = 0; i < _staleWants.Count; i++)
            {
                _packWant.Remove(_staleWants[i]);
            }

            PruneSamples(_posSamples, renderAt - 1f);
            PruneSamples(_packSamples, renderAt - 1f);
        }

        private readonly List<uint> _staleWants = new List<uint>();

        /// <summary>
        /// How long a position stream may stay silent before the snapshot owns the card
        /// again. The host sends Pos only while a card is held, so silence means it was
        /// dropped. Its samples used to survive another second and keep overwriting the
        /// wanted position every frame with the pre-drop value while the snapshot wrote
        /// the real one - two writers trading the same target, which is the back-and-forth
        /// twitch seen in the 0.4.3 play test.
        /// </summary>
        private const float StreamTtl = 0.2f;

        private readonly List<uint> _expiredStreams = new List<uint>();

        private void ExpireStreams(float now)
        {
            ExpireStreams(_streamedAt, _posSamples, now, "card");
            ExpireStreams(_packStreamedAt, _packSamples, now, "pack");
        }

        private void ExpireStreams(
            Dictionary<uint, float> stamps,
            Dictionary<uint, List<Sample>> samples,
            float now,
            string kind)
        {
            if (stamps.Count == 0)
            {
                return;
            }

            _expiredStreams.Clear();
            foreach (KeyValuePair<uint, float> pair in stamps)
            {
                if (now - pair.Value > StreamTtl)
                {
                    _expiredStreams.Add(pair.Key);
                }
            }

            for (int i = 0; i < _expiredStreams.Count; i++)
            {
                uint id = _expiredStreams[i];
                stamps.Remove(id);

                int count = 0;
                List<Sample> list;
                if (samples.TryGetValue(id, out list))
                {
                    count = list.Count;
                    samples.Remove(id);
                }

                CoopLog.Info("motion stream end " + kind + " net=" + id + " samples=" + count);
            }
        }

        private static void Ramp(Component mover, Vector3 want, float dt, float speed)
        {
            Draggable draggable = mover as Draggable;
            if (draggable == null)
            {
                return;
            }

            Vector3 pos = mover.transform.position;
            float dx = want.x - pos.x;
            float dz = want.z - pos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            // A teleport-sized gap is a join or a board change: follow it immediately.
            if (dist > 6f)
            {
                mover.transform.position = new Vector3(want.x, pos.y, want.z);
                draggable.TargetPosition = mover.transform.position;
                return;
            }

            if (dist < 0.01f)
            {
                return;
            }

            Vector3 target = draggable.TargetPosition;
            Vector3 step = Vector3.MoveTowards(
                new Vector3(target.x, 0f, target.z),
                new Vector3(want.x, 0f, want.z),
                speed * dt);
            draggable.TargetPosition = new Vector3(step.x, target.y, step.z);
        }

        private static bool Interpolate(List<Sample> samples, float at, out float x, out float z)
        {
            x = 0f;
            z = 0f;
            if (samples.Count == 0)
            {
                return false;
            }

            if (at <= samples[0].T)
            {
                x = samples[0].X;
                z = samples[0].Z;
                return true;
            }

            for (int i = 1; i < samples.Count; i++)
            {
                if (at > samples[i].T)
                {
                    continue;
                }

                Sample a = samples[i - 1];
                Sample b = samples[i];
                float span = b.T - a.T;
                float k = span <= 0.0001f ? 1f : (at - a.T) / span;
                x = Mathf.Lerp(a.X, b.X, k);
                z = Mathf.Lerp(a.Z, b.Z, k);
                return true;
            }

            Sample last = samples[samples.Count - 1];
            x = last.X;
            z = last.Z;

            // Past the newest packet: a card that is being dragged keeps moving in a straight
            // line for a short while (driven by velocity), everything else just holds. Only the
            // streamed (dragged) cards get here, so extrapolation cannot make a resting card drift.
            if (ExtrapolateLimit > 0f && samples.Count >= 2)
            {
                float ahead = at - last.T;
                if (ahead > 0f && ahead <= ExtrapolateLimit)
                {
                    Sample prev = samples[samples.Count - 2];
                    float span = last.T - prev.T;
                    if (span > 0.0001f)
                    {
                        float vx = (last.X - prev.X) / span;
                        float vz = (last.Z - prev.Z) / span;
                        x = last.X + vx * ahead;
                        z = last.Z + vz * ahead;
                    }
                }
            }

            return true;
        }

        /// <summary>How far past the newest packet a dragged card may be projected.</summary>
        private const float ExtrapolateLimit = 0.1f;

        private static void PruneSamples(Dictionary<uint, List<Sample>> map, float before)
        {
            if (map.Count == 0)
            {
                return;
            }

            _scratch.Clear();
            foreach (KeyValuePair<uint, List<Sample>> pair in map)
            {
                if (pair.Value.Count == 0 || pair.Value[pair.Value.Count - 1].T < before)
                {
                    _scratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                map.Remove(_scratch[i]);
            }
        }

        private void HandleStart(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                _pending = SnapshotCodec.ReadStart(r);
            }

            // An empty world is still a real snapshot: applying it is what removes
            // cards/packs the client booted with. Never skip the apply just because
            // no chunk followed.
            _dirty = true;
        }

        private void HandleChunk(byte[] payload)
        {
            if (_pending == null)
            {
                return;
            }

            using (var r = new PacketReader(payload))
            {
                ChunkKind kind = (ChunkKind)r.U8();
                int count = r.U16();
                for (int i = 0; i < count; i++)
                {
                    if (kind == ChunkKind.Cards)
                    {
                        _pending.Cards.Add(SnapshotCodec.ReadCard(r));
                    }
                    else
                    {
                        _pending.Boosters.Add(SnapshotCodec.ReadBooster(r));
                    }
                }
            }

            _dirty = true;
        }

        private void HandleEnd(byte[] payload)
        {
            if (_pending == null)
            {
                return;
            }

            uint revision;
            using (var r = new PacketReader(payload))
            {
                revision = r.U32();
            }

            WorldSnapshot snap = _pending;
            _pending = null;
            if (!_dirty)
            {
                return;
            }

            snap.Revision = revision;

            // Render the world slightly in the past so a snapshot that arrives a little late
            // still lands on time, instead of the whole board jumping when it shows up.
            // The margin grows when arrival jitter grows, within the 50-90 ms window.
            float now = Time.unscaledTime;
            if (_lastSnapshotAt > 0f)
            {
                float gap = Mathf.Max(0f, now - _lastSnapshotAt);
                float err = Mathf.Clamp(Mathf.Abs(gap - 0.2f), 0f, 0.08f);
                _snapshotJitter = Mathf.Lerp(_snapshotJitter, err, 0.2f);
            }

            _lastSnapshotAt = now;
            _renderDelay = Mathf.Clamp(0.045f + _snapshotJitter, 0.05f, 0.09f);

            _applyQueue.Add(new QueuedSnapshot { Snap = snap, Due = now + _renderDelay });
            if (_applyQueue.Count > 8)
            {
                _applyQueue.RemoveAt(0);
            }
        }

        private sealed class QueuedSnapshot
        {
            public WorldSnapshot Snap;
            public float Due;
        }

        private readonly List<QueuedSnapshot> _applyQueue = new List<QueuedSnapshot>();

        /// <summary>Adaptive render margin: 50-90 ms, grown by measured snapshot jitter.</summary>
        private float _renderDelay = 0.06f;

        private float _lastSnapshotAt;
        private float _snapshotJitter = 0.02f;

        /// <summary>Called every frame: applies due snapshots (never mid-drag).</summary>
        /// <summary>
        /// Keep the client's month clock visually in step with the host. It is capped just below
        /// the end of the month so the frozen client can never run the end-of-month logic itself.
        /// </summary>
        private void TickMonthClock()
        {
            if (SessionController.Instance.Role != SessionRole.Client)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null || !wm.IsPlaying || _appliedCards == 0)
            {
                return;
            }

            float cap = Mathf.Max(0f, wm.MonthTime - 0.5f);
            wm.MonthTimer = Mathf.Min(EstimatedMonthTimer, cap);
        }

        public void Tick()
        {
            TickPendingTravel();
            TickFollowPending();
            TickMonthClock();

            if (_applyQueue.Count == 0)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm != null && wm.DraggingCard != null)
            {
                // Structural reconciliation is frozen during a drag (the held card must not be
                // rebuilt), but progress bars still have to be corrected or they stall until the
                // player lets go. Feeding the timers from the newest snapshot is enough.
                QueuedSnapshot newest = _applyQueue[_applyQueue.Count - 1];
                ApplyTimers(newest.Snap, _byNet);
                return;
            }

            float now = Time.unscaledTime;
            while (_applyQueue.Count > 0 && _applyQueue[0].Due <= now)
            {
                QueuedSnapshot next = _applyQueue[0];
                _applyQueue.RemoveAt(0);
                Apply(next.Snap);
            }
        }

        private void TickPendingTravel()
        {
            if (string.IsNullOrEmpty(_pendingTravel))
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            if (wm.CurrentBoard != null && wm.CurrentBoard.Id == _pendingTravel)
            {
                _pendingTravel = string.Empty;
                return;
            }

            if (Time.unscaledTime < _pendingTravelAt)
            {
                return;
            }

            // The animated transition runs on a coroutine the ghost client does not tick, so
            // after a grace period switch the board field directly and recentre the camera.
            try
            {
                GameBoard board = wm.GetBoardWithId(_pendingTravel);
                if (board == null)
                {
                    _pendingTravel = string.Empty;
                    return;
                }

                System.Reflection.FieldInfo field = typeof(WorldManager).GetField(
                    "<CurrentBoard>k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(wm, board);
                }

                if (GameCamera.instance != null)
                {
                    GameCamera.instance.CenterOnBoard(board);
                }

                CoopLog.Info("travel forced " + _pendingTravel);
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("travel force failed: " + ex.Message);
            }

            _pendingTravel = string.Empty;
        }

        private void Apply(WorldSnapshot snap)
        {
            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.AllCards == null)
            {
                return;
            }

            GhostMode.Bypass = true;
            try
            {
                _last = snap;
                _conflicts = snap.Conflicts;
                if (snap.Conflicts.Count != _lastConflictCount)
                {
                    _lastConflictCount = snap.Conflicts.Count;
                    CoopLog.Info("conflicts groups=" + snap.Conflicts.Count);
                }
                ulong structSig = StructSig(snap);
                ulong boosterSig = BoosterSig(snap);

                // Heartbeats repeat the same world, so create/destroy/restack passes would
                // re-walk every card for nothing. Only the per-record visual passes run.
                bool structural = structSig != _structSig
                    || boosterSig != _boosterSig
                    || _byNet.Count != snap.Cards.Count
                    || _appliesSinceFullScan >= FullScanInterval
                    || !KnownCardsMatch(snap);

                Dictionary<uint, GameCard> desired = structural
                    ? RebuildAndReconcile(wm, snap)
                    : new Dictionary<uint, GameCard>(_byNet);

                ApplyPositions(wm, snap, desired);
                ApplyTimers(snap, desired);
                ApplyCombat(snap, desired);
                ApplyShops(snap, desired);
                ApplySlots(snap, desired);
                PruneLive(snap, desired);

                wm.MonthTimer = snap.MonthTimer;
                _monthTimerBase = snap.MonthTimer;
                _monthTimerBaseAt = Time.unscaledTime;
                _hostSpeedUp = snap.SpeedUp > 0.001f ? snap.SpeedUp : 0f;
                SyncMonth(wm, snap.Month);
                SyncQuests(wm, snap.QuestsCompleted);
                CheckDefs(snap.DefsHash);
                FollowBoard(wm, snap.BoardId);
                _appliedCards = desired.Count;
                _appliedBoosters = snap.Boosters.Count;
                LastAppliedAt = Time.unscaledTime;
                _structSig = structSig;
                _boosterSig = boosterSig;
                _appliesSinceFullScan = structural ? 0 : _appliesSinceFullScan + 1;
                if (!structural)
                {
                    _skippedApplies++;
                }
                if (desired.Count != _loggedCards || snap.Boosters.Count != _loggedPacks ||
                    Time.unscaledTime - _lastApplyLogAt > 5f)
                {
                    _loggedCards = desired.Count;
                    _loggedPacks = snap.Boosters.Count;
                    _lastApplyLogAt = Time.unscaledTime;
                    CoopLog.Info("snapshot applied rev=" + snap.Revision + " cards=" + desired.Count + " packs=" + snap.Boosters.Count + " board=" + (MyBoard ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                CoopLog.Error("应用快照失败: " + ex);
            }
            finally
            {
                GhostMode.Bypass = false;
            }
        }

        private const int FullScanInterval = 30;

        /// <summary>
        /// Cheap invariant for the skip path: every remote card still has a live object with
        /// the expected definition. If the game destroyed or replaced any of them behind our
        /// back (combat, board switch, save load), fall back to the full pass instead of
        /// silently rendering a stale world.
        /// </summary>
        private bool KnownCardsMatch(WorldSnapshot snap)
        {
            List<CardRecord> cards = snap.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                CardRecord rec = cards[i];
                GameCard card;
                if (!_byNet.TryGetValue(rec.NetId, out card) || !IsAlive(card) || !DefMatches(card, rec.CardId))
                {
                    return false;
                }
            }

            return true;
        }

        private ulong _structSig;
        private ulong _boosterSig;
        private int _appliesSinceFullScan = FullScanInterval;

        private static void Mix(ref ulong hash, uint value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        /// <summary>Identity + shape of the card list: exactly what needs create/destroy/restack.</summary>
        private static ulong StructSig(WorldSnapshot snap)
        {
            ulong hash = 1469598103934665603UL;
            List<CardRecord> cards = snap.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                CardRecord rec = cards[i];
                Mix(ref hash, rec.NetId);
                Mix(ref hash, rec.ParentNetId);
                Mix(ref hash, (uint)rec.Index);
                Mix(ref hash, (uint)(rec.FaceUp ? 1 : 0));
                Mix(ref hash, (uint)(rec.Foil ? 1 : 0));
                Mix(ref hash, rec.Damage);
                Mix(ref hash, (uint)(rec.CardId != null ? rec.CardId.GetHashCode() : 0));
            }

            return hash;
        }

        private static ulong BoosterSig(WorldSnapshot snap)
        {
            ulong hash = 1469598103934665603UL;
            List<BoosterRecord> packs = snap.Boosters;
            for (int i = 0; i < packs.Count; i++)
            {
                BoosterRecord rec = packs[i];
                Mix(ref hash, rec.NetId);
                Mix(ref hash, (uint)(rec.BoosterId != null ? rec.BoosterId.GetHashCode() : 0));
                Mix(ref hash, (uint)rec.TimesOpened);
            }

            return hash;
        }

        /// <summary>
        /// Full pass: create missing cards, drop local-only leftovers, rebuild stacks whose
        /// membership or order differs. Runs only when the structure actually changed.
        /// </summary>
        private Dictionary<uint, GameCard> RebuildAndReconcile(WorldManager wm, WorldSnapshot snap)
        {
            var desired = new Dictionary<uint, GameCard>();
            var stale = new List<GameCard>();

            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord rec = snap.Cards[i];
                GameCard card;
                if (_byNet.TryGetValue(rec.NetId, out card) && IsAlive(card) && DefMatches(card, rec.CardId))
                {
                    desired[rec.NetId] = card;
                    continue;
                }

                if (IsAlive(card))
                {
                    _netOf.Remove(card);
                    try
                    {
                        if (card.Parent != null)
                        {
                            card.RemoveFromParent();
                        }

                        card.DestroyCard(false, false);
                    }
                    catch (Exception ex)
                    {
                        CoopLog.Warn("replacing card failed: " + ex.Message);
                    }
                }

                GameCard created = CreateCard(wm, rec);
                if (created != null)
                {
                    desired[rec.NetId] = created;
                }
            }

            foreach (KeyValuePair<uint, GameCard> pair in _byNet)
            {
                if (!desired.ContainsKey(pair.Key) && IsAlive(pair.Value))
                {
                    stale.Add(pair.Value);
                }
            }

            _byNet.Clear();
            _netOf.Clear();
            foreach (KeyValuePair<uint, GameCard> pair in desired)
            {
                _byNet[pair.Key] = pair.Value;
                _netOf[pair.Value] = pair.Key;
            }

            // Anything the host does not know about is local-only leftovers (the client's
            // own loaded save before joining): drop it so both sides see the host's world.
            var keep = new HashSet<GameCard>(desired.Values);
            for (int i = wm.AllCards.Count - 1; i >= 0; i--)
            {
                GameCard extra = wm.AllCards[i];
                if (extra == null || keep.Contains(extra) || wm.DraggingCard == extra)
                {
                    continue;
                }

                if (extra.Parent == null && !stale.Contains(extra))
                {
                    stale.Add(extra);
                }
            }

            // Destroy everything the host no longer has. A dead villager is often a child of a
            // stack, so unparent it first - destroying only roots left corpses on the client.
            for (int i = 0; i < stale.Count; i++)
            {
                GameCard victim = stale[i];
                if (victim == null || !IsAlive(victim) || wm.DraggingCard == victim)
                {
                    continue;
                }

                try
                {
                    if (victim.Parent != null)
                    {
                        victim.RemoveFromParent();
                    }

                    victim.DestroyCard(false, false);
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("removing stale card failed: " + ex.Message);
                }
            }

            ReconcileStacks(wm, snap, desired);
            ReconcileBoosters(wm, snap);
            return desired;
        }

        private static bool IsAlive(GameCard card)
        {
            return card != null && card.CardData != null;
        }

        private static bool DefMatches(GameCard card, string cardId)
        {
            return card != null && card.CardData != null && card.CardData.Id == cardId;
        }

        private GameCard CreateCard(WorldManager wm, CardRecord rec)
        {
            CardData prefab = wm.GetCardPrefab(rec.CardId, false);
            if (prefab == null)
            {
                CoopLog.Warn("未知卡牌定义 " + rec.CardId + "，已跳过");
                return null;
            }

            var pos = new Vector3(rec.X, 0f, rec.Z);

            // A fresh card should announce itself like it does on the host: the spawn sound
            // and the little bounce. The first sync after joining stays silent, otherwise
            // loading a board would fire hundreds of sounds at once.
            bool announce = _appliedCards > 0;
            CardData created = wm.CreateCard(pos, prefab, rec.FaceUp, false, announce, false);
            if (created == null || created.MyGameCard == null)
            {
                return null;
            }

            if (announce)
            {
                try
                {
                    // Bounce() is protected; reflect it instead of touching game state.
                    System.Reflection.MethodInfo bounce = HarmonyLib.AccessTools.Method(typeof(GameCard), "Bounce");
                    if (bounce != null)
                    {
                        bounce.Invoke(created.MyGameCard, null);
                    }
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("建卡弹跳失败: " + ex.Message);
                }
            }

            return created.MyGameCard;
        }

        /// <summary>
        /// Rebuild only the stacks whose membership or order differs from the snapshot.
        /// </summary>
        private void ReconcileStacks(WorldManager wm, WorldSnapshot snap, Dictionary<uint, GameCard> desired)
        {
            var groups = new Dictionary<uint, List<CardRecord>>();
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord rec = snap.Cards[i];
                if (rec.ParentNetId == 0u)
                {
                    continue;
                }

                List<CardRecord> list;
                if (!groups.TryGetValue(rec.ParentNetId, out list))
                {
                    list = new List<CardRecord>();
                    groups[rec.ParentNetId] = list;
                }

                list.Add(rec);
            }

            // Deep stacks must be rebuilt from the root downwards: attaching a card to a
            // parent that is not attached yet leaves the whole chain unstacked.
            var byNet = new Dictionary<uint, CardRecord>();
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                byNet[snap.Cards[i].NetId] = snap.Cards[i];
            }

            var order = new List<KeyValuePair<uint, List<CardRecord>>>(groups);
            order.Sort(delegate(KeyValuePair<uint, List<CardRecord>> a, KeyValuePair<uint, List<CardRecord>> b)
            {
                return Depth(byNet, a.Key).CompareTo(Depth(byNet, b.Key));
            });

            foreach (KeyValuePair<uint, List<CardRecord>> group in order)
            {
                GameCard parent;
                if (!desired.TryGetValue(group.Key, out parent))
                {
                    continue;
                }

                List<CardRecord> children = group.Value;
                children.Sort(delegate(CardRecord a, CardRecord b) { return a.Index.CompareTo(b.Index); });

                if (StackMatches(parent, children, desired))
                {
                    continue;
                }

                // Remove existing children first so the chain is rebuilt in host order.
                for (int i = 0; i < children.Count; i++)
                {
                    GameCard child;
                    if (desired.TryGetValue(children[i].NetId, out child) && child.Parent != null)
                    {
                        child.RemoveFromStack();
                    }
                }

                for (int i = 0; i < children.Count; i++)
                {
                    GameCard child;
                    if (desired.TryGetValue(children[i].NetId, out child))
                    {
                        child.SetParent(parent);
                    }
                }

                if (Time.unscaledTime - _lastStackLog > 1f)
                {
                    _lastStackLog = Time.unscaledTime;
                    CoopLog.Info("stack rebuild parent=" + group.Key + " children=" + children.Count);
                }
            }

            // Roots that still hang under a parent the host says is free.
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord rec = snap.Cards[i];
                if (rec.ParentNetId != 0u)
                {
                    continue;
                }

                GameCard card;
                if (desired.TryGetValue(rec.NetId, out card) && card.Parent != null)
                {
                    card.RemoveFromStack();
                }
            }
        }

        private static int Depth(Dictionary<uint, CardRecord> byNet, uint netId)
        {
            int depth = 0;
            CardRecord rec;
            while (depth < 64 && byNet.TryGetValue(netId, out rec) && rec.ParentNetId != 0u)
            {
                netId = rec.ParentNetId;
                depth++;
            }

            return depth;
        }

        private static bool StackMatches(GameCard parent, List<CardRecord> children, Dictionary<uint, GameCard> desired)
        {
            GameCard current = parent.Child;
            for (int i = 0; i < children.Count; i++)
            {
                GameCard expected;
                if (!desired.TryGetValue(children[i].NetId, out expected))
                {
                    // A child this client has not created yet: the chain cannot be trusted,
                    // so rebuild on the next snapshot instead of pretending it matches.
                    return false;
                }

                if (current != expected)
                {
                    return false;
                }

                current = expected.Child;
            }

            return current == null;
        }

        /// <summary>
        /// Foil and damage are pure visuals owned by the host's card data; mirror them so a
        /// burnt or shiny card looks the same on every screen.
        /// </summary>
        private void ApplyCosmetics(GameCard card, CardRecord rec)
        {
            CardData data = card.CardData;
            if (data == null)
            {
                return;
            }

            if (rec.Foil && !data.IsFoil)
            {
                data.SetFoil();
            }

            if (rec.Damage != 0 && !data.IsDamaged)
            {
                data.SetCardDamaged(CardDamageType.Damaged);
            }
            else if (rec.Damage == 0 && data.IsDamaged)
            {
                data.SetCardUndamaged();
            }
        }

        private void ApplyPositions(WorldManager wm, WorldSnapshot snap, Dictionary<uint, GameCard> desired)
        {
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord rec = snap.Cards[i];
                GameCard card;
                if (!desired.TryGetValue(rec.NetId, out card))
                {
                    continue;
                }

                if (wm.DraggingCard == card)
                {
                    continue;
                }

                // Stacked cards sit where the game's own stack layout puts them. Writing their
                // coordinates from the snapshot fights that layout and makes a dragged stack
                // jitter back and forth.
                if (card.Parent != null)
                {
                    card.FaceUp = rec.FaceUp;
                    ApplyCosmetics(card, rec);
                    continue;
                }

                // The attack replay owns this card's transform for a moment; a snapshot
                // landing mid-swing would otherwise freeze the animation.
                if (AttackFx.IsBusy(card))
                {
                    card.FaceUp = rec.FaceUp;
                    ApplyCosmetics(card, rec);
                    continue;
                }

                float streamedAt;
                if (_streamedAt.TryGetValue(rec.NetId, out streamedAt) && Time.unscaledTime - streamedAt < StreamTtl)
                {
                    // This card is being dragged by someone else and its live position
                    // arrives over the Pos stream; a snapshot must not snap it back.
                    card.FaceUp = rec.FaceUp;
                    ApplyCosmetics(card, rec);
                    continue;
                }

                var pos = new Vector3(rec.X, card.transform.position.y, rec.Z);

                // Let the game's own Draggable update do the motion (that is what makes
                // cards slide on the host). Only a teleport-sized jump — join, board
                // change — is applied directly so cards do not fly across the screen.
                Vector3 current = card.transform.position;
                if (new Vector2(current.x - pos.x, current.z - pos.z).magnitude > 6f)
                {
                    card.transform.position = pos;
                    card.TargetPosition = pos;
                    _posWant.Remove(rec.NetId);
                }
                else
                {
                    _posWant[rec.NetId] = pos;
                }

                card.FaceUp = rec.FaceUp;
                ApplyCosmetics(card, rec);
                GameBoard board = FindBoard(wm, rec.BoardId);
                if (board != null && card.MyBoard != board)
                {
                    card.MyBoard = board;
                }
            }
        }

        /// <summary>
        /// The month number lives in BoardMonths and only advances inside the end-of-month
        /// routine, which never runs on a client. Walk it forward to match the host.
        /// </summary>
        private void SyncMonth(WorldManager wm, int hostMonth)
        {
            int before = wm.CurrentMonth;
            for (int guard = 0; guard < 64 && wm.CurrentMonth < hostMonth; guard++)
            {
                wm.BoardMonths.IncrementMonth();
            }

            if (wm.CurrentMonth > before)
            {
                CoopLog.Info("month synced " + before + "->" + wm.CurrentMonth);
            }
        }

        /// <summary>
        /// Board the local player is standing on. Every player picks their own board: the world
        /// clock is shared, but two players may be on different boards at the same time.
        /// </summary>
        public string MyBoard { get; private set; } = string.Empty;

        /// <summary>
        /// Called on the first snapshot only: a fresh client starts on the host's board, after
        /// that it keeps whatever board the player travelled to.
        /// </summary>
        private void FollowBoard(WorldManager wm, string hostBoardId)
        {
            // Follow mode: everyone stands on the host's board. The game only simulates one
            // board at a time, so keeping every player on that board is also what keeps the
            // world running for everyone - no core gameplay patches needed.
            if (_suppressFollow || string.IsNullOrEmpty(hostBoardId))
            {
                return;
            }

            GameBoard board = wm.GetBoardWithId(hostBoardId);
            if (board == null)
            {
                return;
            }

            if (wm.CurrentBoard == board)
            {
                MyBoard = hostBoardId;
                _followPending = null;
                _followSeen = hostBoardId;
                return;
            }

            // Only follow a board the host has stayed on for a moment: a transition on the host
            // shows up as two board ids in quick succession, and chasing both made clients
            // bounce back and forth.
            if (hostBoardId != _followSeen)
            {
                _followSeen = hostBoardId;
                _followSeenAt = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _followSeenAt < 0.4f)
            {
                return;
            }

            // One board change at a time: while a switch is still settling, ignore new targets
            // and let the next snapshot retry.
            if (!string.IsNullOrEmpty(_pendingTravel))
            {
                return;
            }

            // Finish the current drag before switching, otherwise the held card ends up on a
            // board the player is leaving.
            if (wm.DraggingCard != null || wm.DraggingDraggable != null)
            {
                _followPending = hostBoardId;
                return;
            }

            _followPending = null;
            Travel(hostBoardId);
        }

        /// <summary>Called from Tick: retries the board switch once the hand is free.</summary>
        private void TickFollowPending()
        {
            if (string.IsNullOrEmpty(_followPending))
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            if (wm.DraggingCard != null || wm.DraggingDraggable != null)
            {
                return;
            }

            string target = _followPending;
            _followPending = null;
            Travel(target);
        }

        /// <summary>
        /// Local board travel: the player asks to stand somewhere else. Nothing about the world
        /// changes, so no host round trip is needed; the host keeps simulating every board.
        /// </summary>
        public bool Travel(string boardId)
        {
            WorldManager wm = WorldManager.instance;
            if (wm == null || string.IsNullOrEmpty(boardId))
            {
                return false;
            }

            GameBoard board = wm.GetBoardWithId(boardId);
            if (board == null)
            {
                CoopLog.Warn("travel failed: no board " + boardId);
                return false;
            }

            // Travelling mid-drag would leave the held card pointing at a board the player is
            // about to leave, so refuse until it is put down.
            if (wm.DraggingCard != null || wm.DraggingDraggable != null)
            {
                CoopLog.Info("travel blocked (dragging)");
                UI.StatusBanner.Show("先放下手上的卡再换棋盘", 3f);
                return false;
            }

            CoopLog.Info("travel board " + boardId + (wm.CurrentBoard == board ? " (already there)" : string.Empty));

            if (wm.CurrentBoard == board)
            {
                MyBoard = boardId;
                _requestedBoard = boardId;
                return true;
            }

            // Already on the way there: repeating GoToBoard queues a second transition that
            // lands after the first one and throws the player back to the old board.
            if (_pendingTravel == boardId)
            {
                return true;
            }

            MyBoard = boardId;
            _requestedBoard = boardId;
            StacklandsCoop.Session.SessionController.Instance.NotifyMyBoard(boardId);
            try
            {
                wm.GoToBoard(board, null, "default");
                _pendingTravel = boardId;
                _pendingTravelAt = Time.unscaledTime + 1.5f;
                return true;
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("travel failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Board ids available locally, used by the panel's travel buttons.</summary>
        public List<string> BoardIds()
        {
            List<string> ids = new List<string>();
            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.Boards == null)
            {
                return ids;
            }

            for (int i = 0; i < wm.Boards.Count; i++)
            {
                GameBoard board = wm.Boards[i];
                if (board != null && !string.IsNullOrEmpty(board.Id))
                {
                    ids.Add(board.Id);
                }
            }

            return ids;
        }

        /// <summary>
        /// Completed quest count is a plain counter on the host; copying it keeps quest
        /// progress readable without running any quest logic on the client.
        /// </summary>
        private void SyncQuests(WorldManager wm, int hostQuests)
        {
            if (wm.QuestsCompleted == hostQuests)
            {
                return;
            }

            CoopLog.Info("同步任务进度 " + wm.QuestsCompleted + " -> " + hostQuests);
            wm.QuestsCompleted = hostQuests;
        }

        /// <summary>
        /// Compares the host's card/booster definition fingerprint with ours. A mismatch means
        /// one side has a different DLC set or a different mod build; cards would silently fail
        /// to appear, so it is worth a loud warning.
        /// </summary>
        private void CheckDefs(string hostDefsHash)
        {
            if (string.IsNullOrEmpty(hostDefsHash))
            {
                return;
            }

            string localDefsHash = Protocol.Fingerprint.DefsHash();
            if (string.IsNullOrEmpty(localDefsHash))
            {
                return;
            }

            if (localDefsHash == hostDefsHash)
            {
                if (!_defsMismatchLogged)
                {
                    _defsMismatchLogged = true;
                    CoopLog.Info("defs ok " + hostDefsHash);
                }

                return;
            }

            if (_defsMismatchLogged)
            {
                return;
            }

            _defsMismatchLogged = true;
            CoopLog.Warn("卡牌定义不一致（主机 " + hostDefsHash + "，本机 " + localDefsHash + "），可能有卡显示不出来");
            UI.StatusBanner.Show("卡牌定义与主机不一致，部分卡可能显示不出来", 8f);
        }

        /// <summary>
        /// Health is a pure number owned by the host; copying it keeps the health bar and the
        /// card's combat text in step without letting any combat logic run on the client.
        /// </summary>
        private void ApplyCombat(WorldSnapshot snap, Dictionary<uint, GameCard> desired)
        {
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord rec = snap.Cards[i];
                if (rec.Health == ushort.MaxValue)
                {
                    continue;
                }

                GameCard card;
                if (!desired.TryGetValue(rec.NetId, out card) || card == null || card.Combatable == null)
                {
                    continue;
                }

                if (card.Combatable.HealthPoints == rec.Health)
                {
                    continue;
                }

                card.Combatable.HealthPoints = rec.Health;
                RefreshCombatText(card.Combatable);
            }
        }

        private static readonly System.Reflection.MethodInfo UpdateCardTextMethod =
            HarmonyLib.AccessTools.Method(typeof(Combatable), "UpdateCardText");

        private static readonly System.Reflection.MethodInfo BoosterBoxUpdateMethod =
            HarmonyLib.AccessTools.Method(typeof(BuyBoosterBox), "Update");

        private static readonly System.Reflection.MethodInfo MerchantTextMethod =
            HarmonyLib.AccessTools.Method(typeof(Merchant), "UpdateCardText");

        /// <summary>
        /// Shop counters ("3 of 5 gold paid") are plain int fields on the CardTarget. They are
        /// not part of the card data, so without this pass the client keeps showing the price
        /// it had when the shop was first created.
        /// </summary>
        private float _nextShopNote;

        /// <summary>Throttled diagnostic so a missing shop component shows up in the log.</summary>
        private void ShopNote(string message)
        {
            if (Time.unscaledTime < _nextShopNote)
            {
                return;
            }

            _nextShopNote = Time.unscaledTime + 2f;
            CoopLog.Warn("shop " + message);
        }

        private readonly HashSet<uint> _shopMatched = new HashSet<uint>();

        /// <summary>
        /// Finds a shop target standing at a known spot. Standalone targets have no card, so the
        /// netId lookup cannot reach them and position is the only identifier both sides share.
        /// </summary>
        private static BuyBoosterBox FindBoosterBoxAt(float x, float z, float radius)
        {
            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.CardTargets == null)
            {
                return null;
            }

            BuyBoosterBox best = null;
            float bestDist = radius * radius;
            for (int i = 0; i < wm.CardTargets.Count; i++)
            {
                var box = wm.CardTargets[i] as BuyBoosterBox;
                if (box == null)
                {
                    continue;
                }

                Vector3 p = box.transform.position;
                float dx = p.x - x;
                float dz = p.z - z;
                float dist = dx * dx + dz * dz;
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    best = box;
                }
            }

            return best;
        }
        private void ApplyShops(WorldSnapshot snap, Dictionary<uint, GameCard> desired)
        {
            int changed = 0;
            for (int i = 0; i < snap.Shops.Count; i++)
            {
                ShopRecord rec = snap.Shops[i];
                GameCard card = null;

                // Some shops are standalone targets without a card, so the netId lookup can miss.
                // Fall back to "the shop standing at that spot" before giving up.
                if (rec.NetId != 0u)
                {
                    desired.TryGetValue(rec.NetId, out card);
                }

                try
                {
                    switch (rec.Kind)
                    {
                        case 0:
                        {
                            var box = card != null ? card.GetComponentInChildren<BuyBoosterBox>(true) : null;
                            if (box == null)
                            {
                                box = FindBoosterBoxAt(rec.X, rec.Z, 1.5f);
                                if (box != null && _shopMatched.Add(rec.NetId))
                                {
                                    CoopLog.Info("shop matched by pos net=" + rec.NetId + " x=" + rec.X.ToString("0.0") + " z=" + rec.Z.ToString("0.0"));
                                }
                            }

                            if (box == null)
                            {
                                ShopNote("no BuyBoosterBox for net=" + rec.NetId);
                                continue;
                            }

                            bool wantText = !string.IsNullOrEmpty(rec.Text) && box.BuyText != null &&
                                            box.BuyText.text != rec.Text;
                            if (box.StoredCostAmount == rec.A && !wantText)
                            {
                                continue;
                            }

                            box.StoredCostAmount = rec.A;
                            BoosterBoxUpdateMethod?.Invoke(box, null);
                            // The label is what the player reads: apply the host's text last so a
                            // local recompute cannot overwrite it with the full price again.
                            if (wantText)
                            {
                                box.BuyText.text = rec.Text;
                            }

                            changed++;
                            break;
                        }

                        case 1:
                        {
                            var merchant = card != null ? card.CardData as Merchant : null;
                            if (merchant == null || merchant.AmountGiven == rec.A)
                            {
                                continue;
                            }

                            merchant.AmountGiven = rec.A;
                            MerchantTextMethod?.Invoke(merchant, null);
                            changed++;
                            break;
                        }

                        case 2:
                        {
                            var cart = card != null ? card.CardData as TravellingCart : null;
                            if (cart == null || cart.ItemsBought == rec.A)
                            {
                                continue;
                            }

                            cart.ItemsBought = rec.A;
                            changed++;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("同步商店状态失败: " + ex.Message);
                }
            }

            if (changed > 0)
            {
                CoopLog.Info("shop sync changed=" + changed + " records=" + snap.Shops.Count);
            }
        }

        /// <summary>
        /// Equipment and worker cards are not stack children: they hold a plain GameCard
        /// reference, so a ghost client would otherwise draw them as loose board cards.
        /// </summary>
        private void ApplySlots(WorldSnapshot snap, Dictionary<uint, GameCard> desired)
        {
            int equipped = 0;
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord rec = snap.Cards[i];
                GameCard card;
                if (!desired.TryGetValue(rec.NetId, out card) || card == null || card.CardData == null)
                {
                    continue;
                }

                try
                {
                    GameCard equipHolder = null;
                    bool wantEquip = rec.EquipNetId != 0u
                        && desired.TryGetValue(rec.EquipNetId, out equipHolder)
                        && equipHolder != null;
                    if (wantEquip && card.EquipmentHolder != equipHolder)
                    {
                        ClearSlots(card);
                        var equipment = card.CardData as Equipable;
                        if (equipment != null)
                        {
                            equipHolder.Equip(equipment);
                            equipped++;
                        }
                    }
                    else if (!wantEquip && rec.WorkerNetId == 0u && card.EquipmentHolder != null)
                    {
                        ClearSlots(card);
                    }

                    GameCard workerHolder = null;
                    bool wantWorker = rec.WorkerNetId != 0u
                        && desired.TryGetValue(rec.WorkerNetId, out workerHolder)
                        && workerHolder != null;
                    if (wantWorker && card.WorkerHolder != workerHolder)
                    {
                        ClearSlots(card);
                        var worker = card.CardData as Worker;
                        if (worker != null)
                        {
                            workerHolder.EquipWorker(worker, rec.WorkerIndex);
                        }
                    }
                    else if (!wantWorker && rec.EquipNetId == 0u && card.WorkerHolder != null)
                    {
                        ClearSlots(card);
                    }
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("apply slot failed: " + ex.Message);
                }
            }

            if (equipped > 0)
            {
                CoopLog.Info("slot sync equipped=" + equipped);
            }
        }

        /// <summary>Detach from equipment/worker lists without running any card logic.</summary>
        private static void ClearSlots(GameCard card)
        {
            if (card.EquipmentHolder != null)
            {
                card.EquipmentHolder.EquipmentChildren.Remove(card);
            }

            card.EquipmentHolder = null;
            card.IsEquipped = false;

            if (card.WorkerHolder != null)
            {
                card.WorkerHolder.WorkerChildren.Remove(card);
            }

            card.WorkerHolder = null;
            card.IsWorking = false;
        }
        private static void RefreshCombatText(Combatable combatable)
        {
            try
            {
                UpdateCardTextMethod?.Invoke(combatable, null);
            }
            catch (Exception ex)
            {
                CoopLog.Warn("refresh combat text failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Progress bars are drawn from CurrentTimerTime / TargetTimerTime, so mirror the
        /// host's timer state onto the ghost cards (the client clock is frozen).
        /// </summary>
        private void ApplyTimers(WorldSnapshot snap, Dictionary<uint, GameCard> desired)
        {
            for (int i = 0; i < snap.Cards.Count; i++)
            {
                CardRecord rec = snap.Cards[i];
                GameCard card;
                if (!desired.TryGetValue(rec.NetId, out card) || card == null)
                {
                    continue;
                }

                bool running = rec.TimerTarget > 0.01f;
                if (!running)
                {
                    if (card.TimerRunning || card.CurrentStatusbar != null)
                    {
                        StopTimerOnClient(card);
                    }

                    continue;
                }

                if (!card.TimerRunning || card.CurrentStatusbar == null)
                {
                    // Empty action id: this timer is cosmetic on the client and must
                    // never fire the game's completion callback.
                    if (!StartTimerOnClient(card, rec.TimerTarget))
                    {
                        continue;
                    }

                    if (_timerLogged.Add(rec.NetId))
                    {
                        CoopLog.Info("timer bar from snapshot net=" + rec.NetId + " target=" + rec.TimerTarget.ToString("0.00"));
                    }
                }

                card.TargetTimerTime = rec.TimerTarget;
                float cur = rec.TimerCurrent;
                TrackTimerRate(rec.NetId, rec.TimerCurrent);
                if (card.TimerRunning && !GhostMode.Bypass)
                {
                    // The bar is not assigned the host value: samples land 200 ms apart and any
                    // value jump shows up as a visible stutter. Instead the client keeps its own
                    // monotonic time and only the *speed* is nudged toward the host (servo).
                    float local = card.CurrentTimerTime;
                    float err = rec.TimerCurrent - local;
                    if (Mathf.Abs(err) > 0.6f)
                    {
                        // Too far to catch up smoothly (join, pause, long stall): resync once.
                        cur = rec.TimerCurrent;
                        _timerBias.Remove(rec.NetId);
                    }
                    else
                    {
                        _timerBias[rec.NetId] = Mathf.Clamp(err * 0.8f, -0.35f, 0.35f);
                        cur = local;
                    }
                }

                if (cur > rec.TimerTarget)
                {
                    cur = rec.TimerTarget - 0.02f;
                }

                card.CurrentTimerTime = cur;
                card.TimerRunning = true;
            }
        }

        /// <summary>Same effect as the non-public GameCard.InitTimer(..., withStatusBar: true).</summary>
        private bool StartTimerOnClient(GameCard card, float target)
        {
            if (PrefabManager.instance == null || PrefabManager.instance.StatusBarPrefab == null)
            {
                return false;
            }

            try
            {
                Statusbar bar = UnityEngine.Object.Instantiate(PrefabManager.instance.StatusBarPrefab);
                bar.ParentCard = card;
                bar.StatusTime = target;
                card.CurrentStatusbar = bar;
                card.Status = "";
                card.TimerAction = null;
                card.TimerActionId = "";
                card.TargetTimerTime = target;
                card.CurrentTimerTime = 0f;
                card.TimerRunning = true;
                return true;
            }
            catch (Exception ex)
            {
                CoopLog.Error("创建进度条失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>Same effect as the non-public GameCard.StopTimer().</summary>
        private void StopTimerOnClient(GameCard card)
        {
            card.TimerRunning = false;
            card.TimerActionId = "";
            card.Status = "";
            card.TimerBlueprintId = "";
            card.TimerSubprintIndex = 0;
            card.CurrentTimerTime = 0f;
            uint stoppedId;
            if (_netOf.TryGetValue(card, out stoppedId))
            {
                _timerBias.Remove(stoppedId);
                _timerRate.Remove(stoppedId);
            }

            if (card.CurrentStatusbar != null)
            {
                card.CurrentStatusbar.DestroyMe = true;
                card.CurrentStatusbar = null;
            }
        }

        /// <summary>Smooth the progress bar between snapshots (client clock is pinned).</summary>
        public void TickTimers(float dt)
        {
            if (SessionController.Instance.Role != SessionRole.Client || GhostMode.Bypass)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.AllCards == null)
            {
                return;
            }

            for (int i = 0; i < wm.AllCards.Count; i++)
            {
                GameCard card = wm.AllCards[i];
                if (card == null || !card.TimerRunning || card.CurrentStatusbar == null)
                {
                    continue;
                }

                if (card.TimerActionId != null && card.TimerActionId.Length != 0)
                {
                    continue;
                }

                float target = card.TargetTimerTime;
                if (target <= 0.01f)
                {
                    continue;
                }

                uint netId;
                float rate = _hostSpeedUp > 0.001f ? _hostSpeedUp : 1f;
                float measured;
                if (_netOf.TryGetValue(card, out netId))
                {
                    if (_timerRate.TryGetValue(netId, out measured) && measured > 0.001f)
                    {
                        rate = 0.5f * rate + 0.5f * measured;
                    }

                    float bias;
                    if (_timerBias.TryGetValue(netId, out bias))
                    {
                        rate += bias;
                    }
                }

                rate = Mathf.Clamp(rate, 0.05f, 2.5f);

                // The host paused its clock, so the bar must stand still too.
                if (World.MonthFlow.Instance.HostPaused)
                {
                    continue;
                }

                float next = card.CurrentTimerTime + dt * rate;
                if (next > target - 0.02f)
                {
                    next = target - 0.02f;
                }

                // Monotonic: a bar never runs backwards, that is what looks like stuttering.
                if (next < card.CurrentTimerTime)
                {
                    next = card.CurrentTimerTime;
                }

                card.CurrentTimerTime = next;
            }
        }

        /// <summary>
        /// Estimate how fast the host advances this card's timer (1.0 = normal speed, &gt;1 while
        /// the host is sped up). Snapshot spacing jitter is smoothed with an EMA.
        /// </summary>
        private void TrackTimerRate(uint netId, float hostCurrent)
        {
            float now = Time.unscaledTime;
            float prevCur;
            float prevAt;
            if (_timerPrevCur.TryGetValue(netId, out prevCur) && _timerPrevAt.TryGetValue(netId, out prevAt))
            {
                float span = now - prevAt;
                if (span > 0.2f)
                {
                    float measured = Mathf.Max(0f, (hostCurrent - prevCur) / span);
                    float old;
                    _timerRate[netId] = _timerRate.TryGetValue(netId, out old)
                        ? Mathf.Clamp(Mathf.Lerp(old, measured, 0.35f), 0f, 8f)
                        : Mathf.Clamp(measured, 0f, 8f);
                }
            }

            _timerPrevCur[netId] = hostCurrent;
            _timerPrevAt[netId] = now;
        }

        private void ReconcileBoosters(WorldManager wm, WorldSnapshot snap)
        {
            var desired = new Dictionary<uint, Boosterpack>();
            for (int i = 0; i < snap.Boosters.Count; i++)
            {
                BoosterRecord rec = snap.Boosters[i];
                Boosterpack pack;
                if (_packByNet.TryGetValue(rec.NetId, out pack) && pack != null && pack.BoosterId == rec.BoosterId)
                {
                    desired[rec.NetId] = pack;
                }
                else
                {
                    if (pack != null)
                    {
                        UnityEngine.Object.Destroy(pack.gameObject);
                    }

                    var pos = new Vector3(rec.X, 0f, rec.Z);
                    pack = wm.CreateBoosterpack(pos, rec.BoosterId);
                    if (pack == null)
                    {
                        CoopLog.Warn("未知卡包定义 " + rec.BoosterId + "，已跳过");
                        continue;
                    }

                    desired[rec.NetId] = pack;
                }

                pack.TimesOpened = rec.TimesOpened;
                var packPos = new Vector3(rec.X, pack.transform.position.y, rec.Z);
                if (new Vector2(pack.transform.position.x - packPos.x, pack.transform.position.z - packPos.z).magnitude > 6f)
                {
                    pack.transform.position = packPos;
                    pack.TargetPosition = packPos;
                    _packWant.Remove(rec.NetId);
                }
                else
                {
                    _packWant[rec.NetId] = packPos;
                    float drift = new Vector2(pack.transform.position.x - packPos.x,
                        pack.transform.position.z - packPos.z).magnitude;
                    if (drift > 0.3f && !_packMoveLogged.Contains(rec.NetId))
                    {
                        _packMoveLogged.Add(rec.NetId);
                        CoopLog.Info("pack moved net=" + rec.NetId + " x=" + packPos.x.ToString("0.00") +
                                     " z=" + packPos.z.ToString("0.00"));
                    }
                    else if (drift < 0.05f)
                    {
                        _packMoveLogged.Remove(rec.NetId);
                    }
                }
                GameBoard board = FindBoard(wm, rec.BoardId);
                if (board != null)
                {
                    pack.MyBoard = board;
                }
            }

            var stale = new List<uint>();
            foreach (KeyValuePair<uint, Boosterpack> pair in _packByNet)
            {
                if (!desired.ContainsKey(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }

            // Packs the host never sent (the client's own loaded save) must go too,
            // otherwise the player clicks a local-only pack that has no netId.
            if (wm.AllBoosters != null)
            {
                var keep = new HashSet<Boosterpack>(desired.Values);
                for (int i = wm.AllBoosters.Count - 1; i >= 0; i--)
                {
                    Boosterpack extra = wm.AllBoosters[i];
                    if (extra == null || keep.Contains(extra))
                    {
                        continue;
                    }

                    CoopLog.Info("销毁本地多余卡包 " + extra.BoosterId);
                    UnityEngine.Object.Destroy(extra.gameObject);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                Boosterpack pack;
                if (_packByNet.TryGetValue(stale[i], out pack) && pack != null)
                {
                    UnityEngine.Object.Destroy(pack.gameObject);
                }
            }

            _packByNet.Clear();
            _packOf.Clear();
            foreach (KeyValuePair<uint, Boosterpack> pair in desired)
            {
                _packByNet[pair.Key] = pair.Value;
                _packOf[pair.Value] = pair.Key;
            }
        }

        private static GameBoard FindBoard(WorldManager wm, string boardId)
        {
            if (string.IsNullOrEmpty(boardId) || wm.Boards == null)
            {
                return null;
            }

            for (int i = 0; i < wm.Boards.Count; i++)
            {
                if (wm.Boards[i] != null && wm.Boards[i].Id == boardId)
                {
                    return wm.Boards[i];
                }
            }

            return null;
        }
    }
}
