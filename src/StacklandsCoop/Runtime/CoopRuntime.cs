using System;
using StacklandsCoop.Session;
using StacklandsCoop.UI;
using UnityEngine;

namespace StacklandsCoop
{
    /// <summary>
    /// Persistent runtime: pumps the session layer every frame and hosts the co-op panel.
    /// Survives scene changes so the session keeps running across the menu.
    /// </summary>
    public sealed class CoopRuntime : MonoBehaviour
    {
        private static CoopRuntime _instance;

        public static CoopRuntime Instance
        {
            get { return _instance; }
        }

        public static void EnsureCreated()
        {
            if (_instance != null)
            {
                return;
            }

            var go = new GameObject("StacklandsCoop");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<CoopRuntime>();
            Debug.Log("[Coop] runtime created");
        }

        private void Awake()
        {
            _instance = this;
        }

        private void Start()
        {
            // Prove the snapshot wire format round-trips before anyone connects, so a
            // field added to one side but not the other fails loudly at boot.
            string codecError = World.SnapshotCodec.SelfTest();
            if (codecError == null)
            {
                CoopLog.Info("codec ok");
            }
            else
            {
                CoopLog.Error("codec FAILED: " + codecError);
            }

            ApplyCommandLine();
            CoopLog.Info("loc " + UI.Loc.Describe());

            // Friend invitations must be received while still in the main menu, so the
            // callback is registered here instead of waiting for a transport to exist.
            Net.SteamInvite.Initialize();

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-coopPlay")
                {
                    DebugAutoPlay.Arm();
                    return;
                }
            }
        }

        /// <summary>
        /// Headless smoke tests: -coopHost [port] or -coopJoin host:port starts a
        /// session without touching the panel.
        /// </summary>
        private void ApplyCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            CoopLog.Info("args: " + string.Join(" ", args));

            // Chat is parsed up front: the host/join branches below return early.
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-coopLang")
                {
                    UI.Loc.Force(args[i + 1]);
                    CoopLog.Info("loc forced: " + args[i + 1]);
                }

                if (args[i] == "-coopChat")
                {
                    _chatAfterJoin = args[i + 1];
                }

                if (args[i] == "-coopReady")
                {
                    _readyAfterJoin = true;
                }

                if (args[i] == "-coopTravel" && i + 1 < args.Length)
                {
                    _travelAfterJoin = args[i + 1];
                }

                if (args[i] == "-coopTravelBack" && i + 1 < args.Length)
                {
                    _travelBackTo = args[i + 1];
                }

                if (args[i] == "-coopScenario")
                {
                    _scenario = true;
                }

                if (args[i] == "-coopStress")
                {
                    World.DebugScenario.StressMode = true;
                    _scenario = true;
                }

                if (args[i] == "-coopNoScenarioMonth")
                {
                    World.DebugScenario.SkipMonthEnd = true;
                }

                if (args[i] == "-coopFakeMods" && i + 1 < args.Length)
                {
                    SessionController.Instance.DebugModsHash = args[i + 1];
                    CoopLog.Info("fake mods hash: " + args[i + 1]);
                }
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-coopHost")
                {
                    int port = StacklandsCoop.Net.TcpTransport.DefaultPort;
                    if (i + 1 < args.Length)
                    {
                        int.TryParse(args[i + 1], out port);
                    }

                    CoopLog.Info("auto host from command line");
                    SessionController.Instance.StartHost(port, "Host");
                    return;
                }

                if (args[i] == "-coopJoin" && i + 1 < args.Length)
                {
                    string target = args[i + 1];
                    int split = target.LastIndexOf(':');
                    if (split > 0)
                    {
                        int port;
                        int.TryParse(target.Substring(split + 1), out port);
                        CoopLog.Info("auto join from command line: " + target);
                        SessionController.Instance.Join(target.Substring(0, split), port, "Client");
                        return;
                    }
                }

                if (args[i] == "-coopSteamHost")
                {
                    int port = StacklandsCoop.Net.TcpTransport.DefaultPort;
                    if (i + 1 < args.Length)
                    {
                        int.TryParse(args[i + 1], out port);
                    }

                    CoopLog.Info("auto steam host from command line");
                    SessionController.Instance.StartHost(port, "Host", true);
                    return;
                }

                if (args[i] == "-coopSteamJoin" && i + 1 < args.Length)
                {
                    CoopLog.Info("auto steam join from command line: " + args[i + 1]);
                    SessionController.Instance.Join(args[i + 1], 0, "Client", true);
                    return;
                }

                if (args[i] == "-coopChat" && i + 1 < args.Length)
                {
                    continue;
                }
            }
        }

        private string _chatAfterJoin;
        private float _chatAt;
        private bool _readyAfterJoin;
        private bool _readySent;
        private float _readyAt;
        private bool _scenario;
        private string _travelAfterJoin;
        private string _travelBackTo;
        private float _travelBackAt;
        private const float TravelBackDelay = 12f;
        private bool _travelSent;
        private float _travelAt;

        private float _nextDebug;

        private void Update()
        {
            try
            {
                SessionController.Instance.Poll();
                World.HostWorld.Instance.Tick();
                World.HostWorld.Instance.TickTimers();
                World.ClientWorld.Instance.Tick();
                World.ClientWorld.Instance.TickMotion(Time.deltaTime);
                World.ClientWorld.Instance.TickTimers(Time.deltaTime);
                World.AttackFx.Tick(Time.deltaTime);
                World.ClientBoot.Tick();
                World.CommandHost.Instance.Tick();
                SendHeldPosition();
                SendStartupChat();
                SendStartupReady();
                SendStartupTravel();
                SendStartupTravelBack();
                DebugAutoPlay.Tick();
                DebugTick();
                CoopPanel.PollHotkeys();
                HandleDiagnosticsKey();
                CheckSyncStall();
                CheckMainMenuHint();
                if (_scenario)
                {
                    World.DebugScenario.Tick();
                }
            }
            catch (Exception ex)
            {
                CoopLog.Error("poll failed: " + ex);
            }
        }

        private float _nextHeldPos;

        private float _nextStallCheck;
        private bool _stallBannerShown;

        private float _nextHintCheck;
        private bool _hintShown;

        /// <summary>
        /// Nobody reads docs. If this is a fresh install the player sits on the main menu with
        /// no idea the mod is there, so point at the hotkey once per session.
        /// </summary>
        private void CheckMainMenuHint()
        {
            if (_hintShown || UI.CoopPanel.EverOpened || Time.unscaledTime < _nextHintCheck)
            {
                return;
            }

            _nextHintCheck = Time.unscaledTime + 3f;

            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.IsPlaying)
            {
                return;
            }

            _hintShown = true;
            UI.StatusBanner.Show(UI.Loc.T("Co-op mod loaded: press F9 to open the panel (host / join)", "联机 mod 已加载：按 F9 打开联机面板（建房 / 加入）"), 8f);
            CoopLog.Info("panel hint shown");
            HintLanguage();
        }

        /// <summary>
        /// The game ships Simplified/Traditional Chinese itself, so the mod never translates
        /// the base UI. Players stuck on English just get pointed at the in-game selector.
        /// </summary>
        private void HintLanguage()
        {
            try
            {
                SokLoc loc = SokLoc.instance;
                if (loc == null)
                {
                    CoopLog.Info("language hint skipped (SokLoc not ready)");
                    return;
                }

                string language = loc.CurrentLanguage ?? string.Empty;
                if (language.IndexOf("Chinese", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    CoopLog.Info("language already Chinese (" + language + ")");
                    return;
                }

                UI.StatusBanner.Show(UI.Loc.T("The game ships with more languages: Settings - Language", "游戏自带简体中文：设置 → Language → Chinese (Simplified)"), 10f);
                CoopLog.Info("language hint shown (" + language + ")");
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("language hint failed: " + ex.Message);
            }
        }

        /// <summary>
        /// A connected client that stops receiving snapshots looks like "the world froze" and
        /// used to be silent. Warn once per stall episode instead of spamming the log.
        /// </summary>
        private void CheckSyncStall()
        {            if (Time.unscaledTime < _nextStallCheck)
            {
                return;
            }

            _nextStallCheck = Time.unscaledTime + 1f;

            if (SessionController.Instance.Role != SessionRole.Client)
            {
                _stallBannerShown = false;
                return;
            }

            if (SessionController.Instance.IsReconnecting)
            {
                _stallBannerShown = false;
                return;
            }

            float since = World.ClientWorld.Instance.SecondsSinceApplied();
            bool stalled = World.ClientWorld.Instance.IsStalled(5f);
            if (stalled && !_stallBannerShown)
            {
                _stallBannerShown = true;
                string text = since < 0f
                    ? "尚未收到主机的世界数据，正在等待…"
                    : "已 " + Mathf.RoundToInt(since) + " 秒没收到主机同步，正在等待…";
                UI.StatusBanner.Show(text, 5f);
                CoopLog.Warn("sync stalled for " + Mathf.RoundToInt(Mathf.Max(0f, since)) + "s");
            }
            else if (!stalled)
            {
                _stallBannerShown = false;
            }
        }

        /// <summary>
        /// F10 dumps a one-shot diagnostic line. Bug reports without numbers are hard to act on,
        /// so this gives the player a single key that puts the important state into Player.log.
        /// </summary>
        private void HandleDiagnosticsKey()
        {
            if (!UI.Hotkeys.F10Pressed())
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            SessionController session = SessionController.Instance;
            CoopLog.Info(
                "diag role=" + session.Role +
                " peers=" + session.PeerCount +
                " rtt=" + Mathf.RoundToInt(session.RttMs) + "ms" +
                " ghost=" + Net.GhostMode.Enabled +
                " cards=" + (wm != null && wm.AllCards != null ? wm.AllCards.Count : 0) +
                " boosters=" + (wm != null && wm.AllBoosters != null ? wm.AllBoosters.Count : 0) +
                " applied=" + World.ClientWorld.Instance.AppliedCards + "/" + World.ClientWorld.Instance.AppliedBoosters +
                " month=" + (wm != null ? wm.CurrentMonth : 0) +
                " state=" + (wm != null ? wm.CurrentGameState.ToString() : "null") +
                " since=" + Mathf.RoundToInt(World.ClientWorld.Instance.SecondsSinceApplied()) + "s" +
                " skip=" + World.ClientWorld.Instance.SkippedApplies +
                " board=" + LocalBoardId() +
                " lobby=" + (session.RoomInfo ?? string.Empty));
            UI.StatusBanner.Show(UI.Loc.T("Diagnostics written to the log (F10)", "诊断已写入日志 (F10)"), 3f);
        }

        /// <summary>Stream the position of the card this client is dragging (30 Hz).</summary>
        private void SendHeldPosition()
        {
            if (SessionController.Instance.Role != SessionRole.Client)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            GameCard card = wm.DraggingCard;
            var pack = wm.DraggingDraggable as Boosterpack;
            if (card == null && pack == null)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextHeldPos)
            {
                return;
            }

            // A booster pack is Draggable too, so it needs its own stream or it stays
            // frozen for everyone else while the holder drags it around.
            _nextHeldPos = now + 0.033f;
            if (card != null)
            {
                Patches.CommandSubmit.Move(card, card.transform.position);
            }
            else
            {
                Patches.CommandSubmit.PackMove(pack, pack.transform.position);
            }
        }

        /// <summary>Reserved for a future re-introduction; cursors are currently not streamed.</summary>
        private void SendCursor()
        {
        }

        /// <summary>Which board this process is looking at, so remote cursors can be filtered.</summary>
        private static string LocalBoardId()
        {
            if (SessionController.Instance.Role == SessionRole.Client)
            {
                return World.ClientWorld.Instance.MyBoard ?? string.Empty;
            }

            WorldManager wm = WorldManager.instance;
            return wm != null && wm.CurrentBoard != null ? wm.CurrentBoard.Id : string.Empty;
        }

        /// <summary>This game uses the new Input System, so legacy Input.mousePosition throws.</summary>
        private static bool TryGetMousePosition(out Vector3 mouse)
        {
            try
            {
                UnityEngine.InputSystem.Mouse current = UnityEngine.InputSystem.Mouse.current;
                if (current != null)
                {
                    Vector2 value = current.position.ReadValue();
                    mouse = new Vector3(value.x, value.y, 0f);
                    return true;
                }
            }
            catch
            {
                // fall through to legacy input
            }

            try
            {
                mouse = UnityEngine.Input.mousePosition;
                return true;
            }
            catch
            {
                mouse = Vector3.zero;
                return false;
            }
        }

        /// <summary>Smoke tests: -coopTravel &lt;board&gt; hops to that board a few seconds after joining.</summary>
        private void SendStartupTravel()
        {
            if (string.IsNullOrEmpty(_travelAfterJoin) || _travelSent)
            {
                return;
            }

            if (SessionController.Instance.Role != SessionRole.Client)
            {
                return;
            }

            if (_travelAt == 0f)
            {
                _travelAt = Time.unscaledTime + 12f;
                return;
            }

            if (Time.unscaledTime < _travelAt)
            {
                return;
            }

            _travelSent = true;
            string target = _travelAfterJoin;
            CoopLog.Info("auto travel from command line: " + target);
            World.ClientWorld.Instance.Travel(target);

            if (!string.IsNullOrEmpty(_travelBackTo))
            {
                _travelBackAt = Time.unscaledTime + TravelBackDelay;
            }
        }

        /// <summary>Smoke tests: -coopTravelBack &lt;board&gt; comes back after the first hop, to prove往返。</summary>
        private void SendStartupTravelBack()
        {
            if (string.IsNullOrEmpty(_travelBackTo) || _travelBackAt == 0f)
            {
                return;
            }

            if (Time.unscaledTime < _travelBackAt)
            {
                return;
            }

            _travelBackAt = 0f;
            CoopLog.Info("auto travel back from command line: " + _travelBackTo);
            World.ClientWorld.Instance.Travel(_travelBackTo);
        }

        /// <summary>Smoke-test helper: -coopChat "text" sends one chat line after joining.</summary>
        private void SendStartupChat()
        {
            if (string.IsNullOrEmpty(_chatAfterJoin))
            {
                return;
            }

            if (SessionController.Instance.Role == SessionRole.Offline)
            {
                return;
            }

            if (_chatAt == 0f)
            {
                _chatAt = Time.unscaledTime + 4f;
                return;
            }

            if (Time.unscaledTime < _chatAt)
            {
                return;
            }

            string text = _chatAfterJoin;
            _chatAfterJoin = null;
            CoopLog.Info("auto chat from command line: " + text);
            SessionController.Instance.Send(Protocol.Messages.Chat(SessionController.Instance.LocalPlayerId, text));
        }

        /// <summary>Smoke tests: -coopReady flags this client ready a few seconds after joining.</summary>
        private void SendStartupReady()
        {
            if (!_readyAfterJoin || _readySent)
            {
                return;
            }

            if (SessionController.Instance.Role != SessionRole.Client)
            {
                return;
            }

            if (_readyAt == 0f)
            {
                _readyAt = Time.unscaledTime + 8f;
                return;
            }

            if (Time.unscaledTime < _readyAt)
            {
                return;
            }

            _readySent = true;
            CoopLog.Info("auto ready from command line");
            World.MonthFlow.Instance.ToggleLocalReady();
        }

        private void DebugTick()
        {
            if (Time.unscaledTime < _nextDebug)
            {
                return;
            }

            _nextDebug = Time.unscaledTime + 5f;
            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                CoopLog.Info("dbg wm=null role=" + SessionController.Instance.Role);
                return;
            }

            CoopLog.Info("dbg role=" + SessionController.Instance.Role +
                         " state=" + wm.CurrentGameState +
                         " cards=" + (wm.AllCards == null ? -1 : wm.AllCards.Count) +
                         " boosters=" + (wm.AllBoosters == null ? -1 : wm.AllBoosters.Count) +
                         " month=" + wm.CurrentMonth +
                         " timeScale=" + wm.TimeScale +
                         " colCards=" + World.ClientWorld.Instance.AppliedCards);
        }

        private void OnGUI()
        {
            CoopPanel.Draw();
            UI.ConflictOverlay.Draw();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
