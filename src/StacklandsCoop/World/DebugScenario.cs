using System.Collections.Generic;
using StacklandsCoop.Session;
using UnityEngine;

namespace StacklandsCoop.World
{
    /// <summary>
    /// Test-only helper, enabled by the <c>-coopScenario</c> command line switch: boots a
    /// real round on the host and drops a small hand-made world into it. The protocol
    /// self-tests otherwise run against an empty board, which proves the handshake but
    /// says nothing about card creation, stacking, timers or health sync.
    /// </summary>
    public static class DebugScenario
    {
        private static bool _bootAttempted;
        private static bool _booted;
        private static bool _spawned;
        private static float _bootedAt;
        private static float _spawnedAt;
        private static int _growth;
        private static GameCard _removable;
        private static bool _removed;
        private static bool _boardSwitched;
        private static bool _packMoved;
        private static bool _roundRestarted;
        private static bool _monthForced;
        private static bool _monthLogged;
        private static int _monthBefore;
        private static bool _combatSpawned;
        private static bool _conflictLogged;
        private static bool _damageApplied;
        private static int _stressSpawned;

        /// <summary>Spam a few hundred cards so snapshot size/rate can be measured.</summary>
        public static bool StressMode;

        /// <summary>When set, the scenario never forces the month itself: the ready vote decides.</summary>
        public static bool SkipMonthEnd;

        private const int StressTarget = 120;
        private const int StressPerTick = 10;

        private const int GrowthCount = 2;
        private const float GrowthInterval = 6f;
        private const float RemovalDelay = 16f;

        /// <summary>Long enough that the timer is still running when a client joins.</summary>
        private const float ScenarioTimerSeconds = 180f;

        /// <summary>Wait for the growth/removal stages to land before hopping boards.</summary>
        private const float BoardSwitchDelay = 24f;
        private const float PackMoveDelay = 28f;
        private const float RestartDelay = 90f;

        /// <summary>End the month once the board hop has settled, so month sync gets exercised.</summary>
        private const float MonthEndDelay = 40f;

        /// <summary>Drop a villager and a mob after the settlement so combat can start.</summary>
        private const float CombatDelay = 55f;

        /// <summary>Place a merchant card, then pay into it, to exercise the shop channel.</summary>
        private const float ShopDelay = 62f;

        public static void Reset()
        {
            _bootAttempted = false;
            _booted = false;
            _spawned = false;
            _bootedAt = 0f;
            _spawnedAt = 0f;
            _growth = 0;
            _removable = null;
            _removed = false;
            _boardSwitched = false;
            _monthForced = false;
            _monthBefore = 0;
            _combatSpawned = false;
            _conflictLogged = false;
            _damageApplied = false;
            _stressSpawned = 0;
            _shopCard = null;
            _shopPaid = false;
            _packMoved = false;
            _roundRestarted = false;
        }

        public static void Tick()
        {
            if (SessionController.Instance.Role != SessionRole.Host)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            if (_spawned)
            {
                Grow(wm);
                Remove(wm);
                Stress(wm);
                SwitchBoard(wm);
                MovePack(wm);
                RestartRound(wm);
                ForceMonthEnd(wm);
                Combat(wm);
                Shop(wm);
                return;
            }

            if (!wm.IsPlaying && !_bootAttempted)
            {
                _bootAttempted = true;
                Boot(wm);
                return;
            }

            if (!wm.IsPlaying)
            {
                return;
            }

            if (!_booted)
            {
                _booted = true;
                _bootedAt = Time.unscaledTime;
                CoopLog.Info("scenario world booted");
                return;
            }

            if (Time.unscaledTime - _bootedAt < 1.5f)
            {
                return;
            }

            Spawn(wm);
        }

        private static void Boot(WorldManager wm)
        {
            try
            {
                wm.CurrentRunOptions = new RunOptions
                {
                    MoonLength = MoonLength.Normal,
                    IsPeacefulMode = false,
                    IsGreedEnabled = false,
                    IsHappinessEnabled = false,
                    IsDeathEnabled = false
                };
                wm.CurrentRunVariables = new RunVariables();
                wm.RoundExtraKeyValues = new List<SerializedKeyValuePair>();
                wm.StartNewRound();
                wm.Play();
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario boot failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Builds a board the client can be checked against: a stack of two berries, one free
        /// card, one card with a running timer and one booster pack.
        /// </summary>
        private static void Spawn(WorldManager wm)
        {
            _spawned = true;
            _spawnedAt = Time.unscaledTime;
            try
            {
                GameCard bottom = SpawnCard(wm, "berry", new Vector3(-1.0f, 0f, 0f));
                GameCard top = SpawnCard(wm, "berry", new Vector3(-1.1f, 0f, 0.1f));
                if (bottom != null && top != null)
                {
                    top.SetParent(bottom);
                }

                _removable = SpawnCard(wm, "wood", new Vector3(0.6f, 0f, 0.4f));

                GameCard timed = SpawnCard(wm, "rock", new Vector3(1.4f, 0f, -0.6f));
                if (timed != null)
                {
                    // Card cosmetics travel in the snapshot.
                    timed.CardData.SetCardDamaged(CardDamageType.Damaged);
                    StartTimer(timed, ScenarioTimerSeconds);
                }

                GameCard extra = SpawnCard(wm, "stone", new Vector3(0.2f, 0f, -1.2f));
                if (extra != null)
                {
                    StackUnder(extra, bottom);
                }

                wm.CreateBoosterpack(new Vector3(2.0f, 0f, 0.8f), "starter");

                CoopLog.Info("scenario spawned cards=" + wm.AllCards.Count + " boosters=" + wm.AllBoosters.Count);
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario spawn failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Adds a card every few seconds after the initial board, so the client has to take
        /// the incremental path (structure already known, one new card) rather than replaying
        /// a full rebuild from the first snapshot.
        /// </summary>
        private static void Grow(WorldManager wm)
        {
            if (_growth >= GrowthCount)
            {
                return;
            }

            if (Time.unscaledTime - _spawnedAt < GrowthInterval * (_growth + 1))
            {
                return;
            }

            _growth++;
            SpawnCard(wm, "wood", new Vector3(-0.2f + 0.4f * _growth, 0f, 1.2f));
            CoopLog.Info("scenario grew cards=" + wm.AllCards.Count);
        }

        /// <summary>
        /// Destroys one free card after the growth phase, so the client has to drop a card
        /// that vanished on the host (death/removal) instead of only ever adding cards.
        /// </summary>
        private static void Remove(WorldManager wm)
        {
            if (_removed || _growth < GrowthCount)
            {
                return;
            }

            if (Time.unscaledTime - _spawnedAt < RemovalDelay)
            {
                return;
            }

            _removed = true;
            try
            {
                if (_removable != null)
                {
                    _removable.DestroyCard(false, false);
                }
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario removal failed: " + ex.Message);
            }

            CoopLog.Info("scenario removed cards=" + wm.AllCards.Count);
        }

        /// <summary>
        /// Fills the board with up to <see cref="StressTarget"/> cards, ten per tick, to make
        /// the snapshot size/rate measurement meaningful. Only used with <c>-coopStress</c>.
        /// </summary>
        private static void Stress(WorldManager wm)
        {
            if (!StressMode || _stressSpawned >= StressTarget || _growth < GrowthCount)
            {
                return;
            }

            for (int i = 0; i < StressPerTick && _stressSpawned < StressTarget; i++)
            {
                int index = _stressSpawned;
                float x = -3f + (index % 12) * 0.5f;
                float z = -2f + (index / 12) * 0.45f;
                SpawnCard(wm, "wood", new Vector3(x, 0f, z));
                _stressSpawned++;
            }

            CoopLog.Info("scenario stress cards=" + wm.AllCards.Count);
        }

        private static GameCard _shopCard;
        private static bool _shopPaid;

        /// <summary>
        /// Spawn whichever card the game treats as a merchant, then pay one unit into it.
        /// The client has no merchant logic, so the only way its price text can move is the
        /// shop channel in the snapshot; the log line proves that path works end to end.
        /// </summary>
        /// <summary>Merchants are spawned by events, not listed as plain prefabs, so try the
        /// usual card ids first and fall back to a scan by type name.</summary>
        private static CardData FindMerchantPrefab(WorldManager wm)
        {
            string[] guesses = { "merchant", "market", "travelling_cart" };
            for (int i = 0; i < guesses.Length; i++)
            {
                CardData direct = wm.GetCardPrefab(guesses[i], false);
                if (direct != null && direct is Merchant)
                {
                    return direct;
                }
            }

            List<CardData> prefabs = wm.CardDataPrefabs;
            for (int i = 0; i < prefabs.Count; i++)
            {
                CardData prefab = prefabs[i];
                if (prefab is Merchant)
                {
                    return prefab;
                }

                string id = prefab.Id ?? string.Empty;
                if (id.IndexOf("merchant", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return prefab;
                }
            }

            return null;
        }
        private static void Shop(WorldManager wm)
        {
            if (Time.unscaledTime - _spawnedAt < ShopDelay)
            {
                return;
            }

            try
            {
                if (_shopCard == null)
                {
                    CardData prefab = FindMerchantPrefab(wm);

                    if (prefab == null)
                    {
                        CoopLog.Warn("scenario shop skipped (no merchant card)");
                        return;
                    }

                    CardData created = wm.CreateCard(new Vector3(-2.2f, 0f, 1.6f), prefab, true, false, false, false);
                    _shopCard = created != null ? created.MyGameCard : null;
                    CoopLog.Info("scenario shop spawned id=" + prefab.Id);
                    return;
                }

                if (_shopPaid)
                {
                    return;
                }

                var merchant = _shopCard.CardData as Merchant;
                if (merchant == null)
                {
                    CoopLog.Warn("scenario shop skipped (not a merchant)");
                    return;
                }

                merchant.AmountGiven += 1;
                _shopPaid = true;
                CoopLog.Info("scenario shop paid given=" + merchant.AmountGiven);
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario shop failed: " + ex.Message);
            }
        }
        private static GameCard SpawnCard(WorldManager wm, string cardId, Vector3 pos)
        {
            CardData prefab = wm.GetCardPrefab(cardId, false);
            if (prefab == null)
            {
                CoopLog.Warn("scenario card missing: " + cardId);
                return null;
            }

            CardData created = wm.CreateCard(pos, prefab, true, false, false, false);
            return created != null ? created.MyGameCard : null;
        }

        private static void StackUnder(GameCard child, GameCard parent)
        {
            if (child == null || parent == null)
            {
                return;
            }

            child.SetParent(parent);
        }

        /// <summary>No-op timer callback: keeps GameCard.UpdateTimer from null-dereferencing.</summary>
        public static void NoopTimerAction()
        {
        }

        /// <summary>
        /// Moves the host to another board if one is available, so the snapshot's BoardId field
        /// and the client's FollowBoard path get exercised instead of only ever sitting on "main".
        /// </summary>
        private static void SwitchBoard(WorldManager wm)
        {
            if (_boardSwitched || Time.unscaledTime - _spawnedAt < BoardSwitchDelay)
            {
                return;
            }

            _boardSwitched = true;
            string[] candidates = { "forest", "island" };
            for (int i = 0; i < candidates.Length; i++)
            {
                GameBoard board = wm.GetBoardWithId(candidates[i]);
                if (board == null)
                {
                    continue;
                }

                try
                {
                    wm.GoToBoard(board, null, "default");
                    CoopLog.Info("scenario board switched to " + candidates[i]);
                }
                catch (System.Exception ex)
                {
                    CoopLog.Warn("scenario board switch failed: " + ex.Message);
                }

                return;
            }

            CoopLog.Warn("scenario board switch skipped (no forest/island board)");
        }

        /// <summary>
        /// Nudges the starter pack a little and leaves it there. Pack position is not streamed
        /// anywhere else, so this only reaches the client if the snapshot hash covers it.
        /// </summary>
        private static void MovePack(WorldManager wm)
        {
            if (_packMoved || Time.unscaledTime - _spawnedAt < PackMoveDelay)
            {
                return;
            }

            _packMoved = true;
            if (wm.AllBoosters == null || wm.AllBoosters.Count == 0)
            {
                CoopLog.Warn("scenario pack move skipped (no boosters)");
                return;
            }

            try
            {
                Boosterpack pack = wm.AllBoosters[0];
                Vector3 pos = pack.transform.position + new Vector3(1.4f, 0f, 0.9f);
                pack.transform.position = pos;
                pack.TargetPosition = pos;
                CoopLog.Info("scenario pack moved to " + pos.x.ToString("0.00") + "," + pos.z.ToString("0.00"));
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario pack move failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Ends the month once, using the same call the "force end month" button makes, so the
        /// client's month counter (which only ever increments locally) gets validated against a
        /// real settlement instead of an empty board.
        /// </summary>
        private static void ForceMonthEnd(WorldManager wm)
        {
            if (_monthForced)
            {
                int now = wm.CurrentMonth;
                if (now > _monthBefore && !_monthLogged)
                {
                    _monthLogged = true;
                    CoopLog.Info("scenario month advanced " + _monthBefore + "->" + now);
                }
                return;
            }

            if (!_monthForced && SkipMonthEnd && Time.unscaledTime - _spawnedAt >= MonthEndDelay)
            {
                // Let the ready-vote path be the only trigger (used by selftest-ready.ps1),
                // but still log the month change so it can be asserted.
                int now = wm.CurrentMonth;
                if (_monthBefore == 0)
                {
                    _monthBefore = now;
                }
                else if (now > _monthBefore && !_monthLogged)
                {
                    _monthLogged = true;
                    _monthForced = true;
                    CoopLog.Info("scenario month advanced " + _monthBefore + "->" + now + " (ready vote)");
                }

                return;
            }

            if (Time.unscaledTime - _spawnedAt < MonthEndDelay)
            {
                return;
            }

            _monthForced = true;
            _monthBefore = wm.CurrentMonth;
            try
            {
                wm.ForceEndOfMoon(new EndOfMonthParameters
                {
                    SkipSpecialEvents = true,
                    SkipEndConfirmation = true
                });
                CoopLog.Info("scenario month forced from=" + _monthBefore);
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario month end failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Drops a villager next to a mob once the month has settled, so the host actually
        /// starts a conflict and the client's hit/attack replay paths get exercised. The mob
        /// card ids differ between the base game and DLCs, so the first that resolves wins.
        /// </summary>
        private static void Combat(WorldManager wm)
        {
            if (!_combatSpawned)
            {
                if (Time.unscaledTime - _spawnedAt < CombatDelay)
                {
                    return;
                }

                _combatSpawned = true;
                GameCard villager = SpawnCard(wm, "villager", new Vector3(4.0f, 0f, 4.0f));
                string[] enemies = { "rat", "skeleton", "goblin", "slime" };
                for (int i = 0; i < enemies.Length; i++)
                {
                    GameCard enemy = SpawnCard(wm, enemies[i], new Vector3(4.32f, 0f, 4.05f));
                    if (enemy == null)
                    {
                        continue;
                    }

                    CoopLog.Info("scenario combat spawned enemy=" + enemies[i] + " villager=" + (villager != null));
                    return;
                }

                CoopLog.Warn("scenario combat skipped (no known mob card id)");
                return;
            }

            if (!_damageApplied && Time.unscaledTime - _spawnedAt > CombatDelay + 6f)
            {
                _damageApplied = true;
                try
                {
                    GameCard target = null;
                    foreach (GameCard card in wm.AllCards)
                    {
                        if (card != null && card.CardData != null && card.CardData.Id == "villager" && card.Combatable != null)
                        {
                            target = card;
                            break;
                        }
                    }

                    if (target != null)
                    {
                        // Hits the game's own damage path, which the combat patch turns into a
                        // network Hit message; the client must replay the floating number.
                        target.Combatable.Damage(1);
                        CoopLog.Info("scenario damage applied to villager");
                    }
                    else
                    {
                        CoopLog.Warn("scenario damage skipped (villager not found)");
                    }
                }
                catch (System.Exception ex)
                {
                    CoopLog.Warn("scenario damage failed: " + ex.Message);
                }
            }

            if (_conflictLogged)
            {
                return;
            }

            try
            {
                List<Conflict> conflicts = wm.GetAllConflicts();
                if (conflicts != null && conflicts.Count > 0)
                {
                    _conflictLogged = true;
                    CoopLog.Info("scenario conflict started n=" + conflicts.Count);
                }
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario conflict check failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Throws the whole round away and starts a fresh one on the host while clients are still
        /// connected, which is what happens when the host loads a save or restarts. Every card the
        /// client knew about is gone, so this exercises the structural rebuild path.
        /// </summary>
        private static void RestartRound(WorldManager wm)
        {
            if (_roundRestarted || Time.unscaledTime - _spawnedAt < RestartDelay)
            {
                return;
            }

            _roundRestarted = true;
            try
            {
                wm.StartNewRound();
                CoopLog.Info("scenario round restarted");
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario round restart failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Starts a real card timer through the game's own non-public InitTimer. The action id is
        /// left empty so the completion branch never fires; the callback still has to be non-null
        /// because UpdateTimer invokes it unconditionally.
        /// </summary>
        private static void StartTimer(GameCard card, float seconds)
        {
            try
            {
                System.Type actionType = typeof(GameCard).Assembly.GetType("TimerAction");
                if (actionType == null)
                {
                    foreach (System.Reflection.Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        actionType = asm.GetType("TimerAction");
                        if (actionType != null)
                        {
                            break;
                        }
                    }
                }
                if (actionType == null)
                {
                    CoopLog.Warn("scenario timer: TimerAction type not found");
                    return;
                }

                System.Delegate callback = System.Delegate.CreateDelegate(
                    actionType,
                    typeof(DebugScenario).GetMethod(
                        "NoopTimerAction",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
                System.Reflection.MethodInfo init = HarmonyLib.AccessTools.Method(typeof(GameCard), "InitTimer");
                if (init == null)
                {
                    CoopLog.Warn("scenario timer: InitTimer not found");
                    return;
                }

                init.Invoke(card, new object[] { seconds, callback, "scenario", string.Empty, true });
                CoopLog.Info("scenario timer started target=" + seconds);
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("scenario timer failed: " + ex.Message);
            }
        }
    }
}


