using System;
using System.Collections.Generic;
using StacklandsCoop.Session;
using UnityEngine;

namespace StacklandsCoop.UI
{
    /// <summary>
    /// Player-facing co-op panel, drawn in the game's paper-and-ink style. IMGUI keeps
    /// it independent of the native screens; the default IMGUI font has no CJK glyphs,
    /// so a dynamic OS font is loaded once.
    /// </summary>
    public static class CoopPanel
    {
        private const float PanelWidth = 430f;
        private const float PanelHeight = 620f;
        private const float StripWidth = 268f;
        private const float StripHeight = 30f;

        private static bool _visible = true;
        private static bool _collapsed = true;
        private static Vector2 _drag;
        private static bool _dragging;
        private static bool _dragArmed;
        private static Vector2 _dragStartMouse;
        private static Vector2 _dragStartOffset;

        /// <summary>True once the player has opened or closed the panel at least once.</summary>
        public static bool EverOpened;

        private static bool _showLog;
        private static string _ip = "lobby:";
        private static string _port = "25565";
        private static bool _steam = true;
        private static string _nick = "Player";
        private static Vector2 _scroll;
        private static Vector2 _logScroll;
        private static Font _cjkFont;
        private static string _chatDraft = string.Empty;

        private static Font CjkFont()
        {
            if (_cjkFont == null)
            {
                try
                {
                    _cjkFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial Unicode MS" }, 14);
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("加载中文字体失败: " + ex.Message);
                }
            }

            return _cjkFont;
        }

        public static void Draw()
        {
            Font font = CjkFont();
            CoopTheme.EnsureBuilt(font);
            StatusBanner.Draw(font);

            if (!_visible)
            {
                return;
            }

            var session = SessionController.Instance;

            if (_collapsed)
            {
                DrawStrip(session);
            }
            else
            {
                DrawPanel(session);
            }
        }

        // ---------------------------------------------------------------- strip

        private static void DrawStrip(SessionController session)
        {
            // Top-centre: over the board horizon, clear of both side columns.
            var rect = new Rect((Screen.width - StripWidth) * 0.5f, 12f, StripWidth, StripHeight);
            GUILayout.BeginArea(rect, CoopTheme.Window);

            GUILayout.BeginHorizontal();
            GUILayout.Label(StatusLine(session), CoopTheme.Label);
            if (GUILayout.Button(Loc.T("Expand", "展开"), CoopTheme.ButtonGhost, GUILayout.Width(42f)))
            {
                _collapsed = false;
                EverOpened = true;
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static string StatusLine(SessionController session)
        {
            if (!session.IsOnline)
            {
                return Loc.T("Co-op · Offline", "联机 · 离线");
            }

            string who = session.Role == SessionRole.Host ? Loc.T("Host", "房主") : Loc.T("Client", "客户端");
            string peers = session.PeerCount + "/" + SessionController.MaxPlayers;
            if (session.Role == SessionRole.Client)
            {
                peers += "  " + (session.RttMs > 0f ? Mathf.RoundToInt(session.RttMs) + "ms" : "…");
            }

            return Loc.T("Co-op · ", "联机 · ") + who + " " + peers;
        }

        // ---------------------------------------------------------------- panel

        /// <summary>Lets the window be dragged by its title bar, so it can get out of the way.</summary>
        private static void HandleDrag(Rect header)
        {
            Event e = Event.current;
            if (e == null)
            {
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && header.Contains(e.mousePosition))
            {
                // Arm the drag but leave the event alone: the buttons in the title bar
                // (收起/展开) still need to receive this click.
                _dragArmed = true;
                _dragStartMouse = e.mousePosition;
                _dragStartOffset = _drag;
            }
            else if (e.type == EventType.MouseDrag && _dragArmed)
            {
                if (!_dragging && (e.mousePosition - _dragStartMouse).sqrMagnitude > 16f)
                {
                    _dragging = true;
                }

                if (_dragging)
                {
                    _drag = _dragStartOffset + (e.mousePosition - _dragStartMouse);
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseUp)
            {
                _dragArmed = false;
                _dragging = false;
            }
        }

        /// <summary>
        /// IMGUI text fields drawn inside our hand-made window sometimes lose the click, so
        /// focus is taken explicitly when the mouse goes down inside the field.
        /// </summary>
        private static string Field(string controlName, string value, params GUILayoutOption[] options)
        {
            GUI.SetNextControlName(controlName);
            string next = options.Length > 0
                ? GUILayout.TextField(value ?? string.Empty, CoopTheme.TextField, options)
                : GUILayout.TextField(value ?? string.Empty, CoopTheme.TextField);
            Event e = Event.current;
            if (e != null && e.type == EventType.MouseDown && e.button == 0
                && GUILayoutUtility.GetLastRect().Contains(e.mousePosition))
            {
                GUI.FocusControl(controlName);
            }

            return next;
        }
        private static void DrawPanel(SessionController session)
        {
            float height = Mathf.Min(Screen.height - 24f, PanelHeight);
            float x = Mathf.Clamp((Screen.width - PanelWidth) * 0.5f + _drag.x, 8f, Mathf.Max(8f, Screen.width - PanelWidth - 8f));
            float y = Mathf.Clamp(12f + _drag.y, 8f, Mathf.Max(8f, Screen.height - height - 8f));
            var rect = new Rect(x, y, PanelWidth, height);
            HandleDrag(new Rect(rect.x, rect.y, rect.width, 34f));

            GUILayout.BeginArea(rect, CoopTheme.Window);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Stacklands Co-op", CoopTheme.Title);
            GUILayout.FlexibleSpace();
            GUILayout.Label("v" + CoopMod.ModVersion, CoopTheme.Muted);
            if (GUILayout.Button(Loc.T("Collapse", "收起"), CoopTheme.ButtonGhost, GUILayout.Width(42f)))
            {
                _collapsed = true;
                return;
            }

            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);

            if (session.IsOnline)
            {
                DrawOnline(session);
            }
            else
            {
                DrawOffline(session);
            }

            DrawFooter(session);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawFooter(SessionController session)
        {
            if (!string.IsNullOrEmpty(session.LastError))
            {
                GUILayout.Space(4f);
                GUILayout.BeginVertical(CoopTheme.Card);
                GUILayout.Label("!", CoopTheme.Muted);
                GUILayout.Label(session.LastError, CoopTheme.Label);
                GUILayout.EndVertical();
            }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            _showLog = GUILayout.Toggle(_showLog, Loc.T(" Show log", " 显示日志"), CoopTheme.Toggle, GUILayout.Width(96f));
            GUILayout.FlexibleSpace();
            GUILayout.Label(Loc.T("F9 panel · F10 diagnostics", "F9 开关 · F10 诊断"), CoopTheme.Hint);
            GUILayout.EndHorizontal();

            if (_showLog)
            {
                _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(120f));
                IReadOnlyList<string> lines = CoopLog.Lines;
                for (int i = 0; i < lines.Count; i++)
                {
                    GUILayout.Label(lines[i], CoopTheme.Muted);
                }

                GUILayout.EndScrollView();
            }
        }

        // ---------------------------------------------------------------- offline

        private static void DrawOffline(SessionController session)
        {
            GUILayout.BeginVertical(CoopTheme.Card);
            GUILayout.Label(Loc.T("Your name", "你的名字"), CoopTheme.Muted);
            _nick = Field("coop_nick", _nick);
            GUILayout.EndVertical();

            bool steamReady = Net.SteamTransport.Available();

            GUILayout.Space(4f);
            GUILayout.Label(steamReady ? Loc.T("Connection", "连接方式") : Loc.T("Connection (Steam is not running, IP only)", "连接方式（Steam 未运行，只能按 IP）"), CoopTheme.Muted);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(steamReady ? Loc.T("Direct IP", "IP 直连") : "IP 直连", _steam ? CoopTheme.Chip : CoopTheme.ChipOn,
                    GUILayout.Height(26f)))
            {
                if (_steam)
                {
                    _steam = false;
                    if (string.IsNullOrEmpty(_ip) || _ip.StartsWith("lobby"))
                    {
                        _ip = "127.0.0.1";
                    }
                }
            }

            if (steamReady &&
                GUILayout.Button(Loc.T("Steam invite", "Steam 邀请"), _steam ? CoopTheme.ChipOn : CoopTheme.Chip, GUILayout.Height(26f)))
            {
                if (!_steam)
                {
                    _steam = true;
                    if (string.IsNullOrEmpty(_ip) || _ip == "127.0.0.1")
                    {
                        _ip = "lobby:";
                    }
                }
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.BeginVertical(CoopTheme.Card);
            GUILayout.Label(Loc.T("Start a game", "开一局"), CoopTheme.Title);
            GUILayout.Label(_steam ? Loc.T("Friends join from a Steam invite; no port needed.", "好友通过 Steam 邀请进入，不需要端口。") : Loc.T("Friends join with your IP and this port.", "好友填你的 IP 和这个端口加入。"),
                CoopTheme.Muted);

            if (!_steam)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("Port", "端口"), CoopTheme.Label, GUILayout.Width(34f));
                _port = Field("coop_port", _port);
                GUILayout.EndHorizontal();
            }

            if (GUILayout.Button(_steam ? Loc.T("Create Steam room", "创建 Steam 房间") : Loc.T("Create room", "创建房间"), CoopTheme.ButtonPrimary,
                    GUILayout.Height(32f)))
            {
                session.StartHost(ParsePort(_port), _nick, _steam);
            }

            GUILayout.EndVertical();

            GUILayout.Space(6f);
            GUILayout.BeginVertical(CoopTheme.Card);
            GUILayout.Label(Loc.T("Join a game", "加入一局"), CoopTheme.Title);
            GUILayout.Label(_steam ? Loc.T("Paste the room code after lobby: (shown in the host's panel)", "大厅号贴在 lobby: 后面（房主面板里的房间号）") : Loc.T("the IP shown in the host's panel", "房主在面板里显示的 IP"),
                CoopTheme.Muted);
            _ip = Field("coop_ip", _ip);
            if (GUILayout.Button(_steam ? Loc.T("Join via Steam", "通过 Steam 加入") : Loc.T("Join", "加入"), CoopTheme.Button, GUILayout.Height(30f)))
            {
                string target = (_ip ?? string.Empty).Trim();
                if (_steam && (target == "lobby" || target == "lobby:"))
                {
                    StatusBanner.Show(Loc.T("Paste the host's room code after lobby:", "把房主面板里的房间号填在 lobby: 后面"), 4f);
                }
                else
                {
                    session.Join(target, ParsePort(_port), _nick, _steam);
                }
            }

            GUILayout.EndVertical();
        }

        // ---------------------------------------------------------------- online

        private static void DrawOnline(SessionController session)
        {
            DrawStatusCard(session);
            DrawPlayers(session);
            DrawMonth(session);
            DrawActions(session);
            DrawChat(session);
            DrawTravel();

            GUILayout.Space(6f);
            if (GUILayout.Button(Loc.T("Leave", "离开房间"), CoopTheme.Button, GUILayout.Height(28f)))
            {
                session.Stop();
            }
        }

        private static void DrawStatusCard(SessionController session)
        {
            GUILayout.BeginVertical(CoopTheme.Card);
            if (session.Role == SessionRole.Host)
            {
                GUILayout.Label(Loc.T("Host · ", "房主 · ") + session.PeerCount + Loc.T(" players", " 人"), CoopTheme.Title);
                GUILayout.Label(session.RoomInfo, CoopTheme.Label);
                string code = session.RoomCode;
                if (!string.IsNullOrEmpty(code))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("Room code ", "房间号 ") + code, CoopTheme.Label);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("Copy", "复制"), CoopTheme.Button, GUILayout.Width(52f)))
                    {
                        GUIUtility.systemCopyBuffer = code;
                        StatusBanner.Show(Loc.T("Room code copied", "房间号已复制"), 3f);
                    }

                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.Label(Loc.T("Connected to ", "已连接 ") + session.HostName, CoopTheme.Title);
                float since = World.ClientWorld.Instance.SecondsSinceApplied();
                string sync = since < 0f
                    ? Loc.T("Waiting for world data…", "等待世界数据…")
                    : (since > 5f ? Loc.T("Sync stalled ", "同步中断 ") + Mathf.RoundToInt(since) + Loc.T("s", " 秒") : Loc.T("Synced", "同步正常"));
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("Ping ", "延迟 ") + (session.RttMs > 0f ? Mathf.RoundToInt(session.RttMs) + " ms" : Loc.T("measuring", "测量中")),
                    CoopTheme.Label);
                GUILayout.FlexibleSpace();
                GUILayout.Label(sync, CoopTheme.Label);
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
        }

        private static void DrawPlayers(SessionController session)
        {
            GUILayout.BeginVertical(CoopTheme.Card);
            GUILayout.Label(Loc.T("Player", "玩家"), CoopTheme.Title);

            int shown = 0;
            foreach (KeyValuePair<int, string> peer in session.Peers)
            {
                shown++;
                string where;
                bool hasBoard = session.PlayerBoards.TryGetValue(peer.Key, out where) &&
                                !string.IsNullOrEmpty(where);
                string name = (peer.Key == session.LocalPlayerId ? Loc.T("You · ", "你 · ") : string.Empty) + peer.Value;

                GUILayout.BeginHorizontal();
                Rect dot = GUILayoutUtility.GetRect(10f, 10f, GUILayout.Width(10f), GUILayout.Height(16f));
                GUI.DrawTexture(new Rect(dot.x, dot.y + 3f, 8f, 8f), CoopTheme.Dot(peer.Key == session.LocalPlayerId
                    ? CoopTheme.Accent
                    : CoopTheme.Blue));
                GUILayout.Label(name + (hasBoard ? "  @ " + BoardName(where) : string.Empty),
                    CoopTheme.Label);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            if (shown == 0)
            {
                GUILayout.Label(Loc.T("Waiting for players…", "等待玩家加入…"), CoopTheme.Muted);
            }

            GUILayout.EndVertical();
        }

        private static void DrawMonth(SessionController session)
        {
            WorldManager world = WorldManager.instance;
            if (world == null || !world.IsPlaying)
            {
                return;
            }

            // The client's clock is pinned at 0, so it needs the locally interpolated value.
            float monthTimer = session.Role == SessionRole.Client
                ? World.ClientWorld.Instance.EstimatedMonthTimer
                : world.MonthTimer;
            float left = Mathf.Max(0f, world.MonthTime - monthTimer);
            float fraction = world.MonthTime > 0.01f ? Mathf.Clamp01(1f - monthTimer / world.MonthTime) : 0f;

            GUILayout.BeginVertical(CoopTheme.Card);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Month ", "第 ") + world.CurrentMonth + Loc.T("", " 个月"), CoopTheme.Label);
            GUILayout.FlexibleSpace();
            GUILayout.Label(Mathf.FloorToInt(left / 60f).ToString("00") + ":" +
                            Mathf.FloorToInt(left % 60f).ToString("00"), CoopTheme.Label);
            GUILayout.EndHorizontal();

            Rect bar = GUILayoutUtility.GetRect(1f, 10f, GUILayout.Height(10f));
            bar = new Rect(bar.x, bar.y + 1f, bar.width, 8f);
            CoopTheme.DrawBar(bar, fraction);

            if (World.MonthFlow.Instance.HostPaused)
            {
                GUILayout.Label(Loc.T("Host paused the world clock", "房主暂停了世界时钟"), CoopTheme.Muted);
            }

            GUILayout.EndVertical();
        }

        private static void DrawActions(SessionController session)
        {
            GUILayout.BeginVertical(CoopTheme.Card);

            GUILayout.Label(session.Role == SessionRole.Host
                ? Loc.T("The host ends the month; everyone follows", "月底由房主结算，其他人跟着走")
                : Loc.T("The host ends the month; nothing for you to do", "月底由房主结算，你不用操作"), CoopTheme.Muted);

            if (session.Role == SessionRole.Host &&
                GUILayout.Button(World.MonthFlow.Instance.HostPaused ? Loc.T("Resume world", "恢复世界") : Loc.T("Pause world", "暂停世界"),
                    CoopTheme.Button, GUILayout.Height(26f)))
            {
                World.MonthFlow.Instance.ToggleHostPause();
            }

            GUILayout.EndVertical();
        }

        private static void DrawChat(SessionController session)
        {
            GUILayout.BeginVertical(CoopTheme.Card);
            GUILayout.Label(Loc.T("Chat", "聊天"), CoopTheme.Title);

            IReadOnlyList<string> lines = ChatLog.Lines;
            int start = Math.Max(0, lines.Count - 4);
            for (int i = start; i < lines.Count; i++)
            {
                GUILayout.Label(lines[i], CoopTheme.Muted);
            }

            GUILayout.BeginHorizontal();
            _chatDraft = Field("coop_chat", _chatDraft);
            if (GUILayout.Button(Loc.T("Send", "发送"), CoopTheme.Button, GUILayout.Width(52f)) &&
                !string.IsNullOrEmpty(_chatDraft))
            {
                session.Send(Protocol.Messages.Chat(session.LocalPlayerId, _chatDraft));
                _chatDraft = string.Empty;
            }

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        // ---------------------------------------------------------------- boards

        private static readonly Dictionary<string, string> BoardNames = new Dictionary<string, string>
        {
            { "main", Loc.T("Main", "初始") },
            { "forest", Loc.T("Forest", "森林") },
            { "island", Loc.T("Island", "岛屿") },
            { "greed", Loc.T("Greed", "贪婪") },
            { "happiness", Loc.T("Happiness", "幸福") },
            { "death", Loc.T("Death", "死亡") },
            { "cities", Loc.T("Cities", "城市") }
        };

        private static void DrawTravel()
        {
            WorldManager wm = WorldManager.instance;
            World.ClientWorld world = World.ClientWorld.Instance;
            if (wm == null)
            {
                return;
            }

            bool isClient = SessionController.Instance.Role == SessionRole.Client;
            string current = isClient
                ? world.MyBoard
                : (wm.CurrentBoard != null ? wm.CurrentBoard.Id : string.Empty);
            List<string> ids = isClient ? world.BoardIds() : HostBoardIds(wm);
            if (ids.Count <= 1)
            {
                return;
            }

            GUILayout.BeginVertical(CoopTheme.Card);
            GUILayout.Label(Loc.T("World (test — do not touch!) · ", "所在世界（测试，不要点！！！）· ") + BoardName(current), CoopTheme.Title);
            GUILayout.Label(isClient ? Loc.T("The host picks the world; you follow", "换世界由房主决定，你会跟着房主走") : Loc.T("Switching worlds moves everyone; time is shared", "换世界所有人一起走，世界时间共享"), CoopTheme.Muted);

            int column = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                bool here = id == current;
                if (column == 0)
                {
                    GUILayout.BeginHorizontal();
                }

                if (isClient)
                {
                    GUILayout.Label(here ? BoardName(id) + " ·" : BoardName(id),
                        here ? CoopTheme.ChipOn : CoopTheme.Chip, GUILayout.Height(26f), GUILayout.Width(78f));
                }
                else if (GUILayout.Button(here ? BoardName(id) + " ·" : BoardName(id),
                        here ? CoopTheme.ChipOn : CoopTheme.Chip, GUILayout.Height(26f), GUILayout.Width(78f)) &&
                    !here)
                {
                    Travel(id, isClient, world, wm);
                }

                column++;
                if (column >= 4 || i == ids.Count - 1)
                {
                    GUILayout.EndHorizontal();
                    column = 0;
                }
            }

            GUILayout.EndVertical();
        }

        private static void Travel(string id, bool isClient, World.ClientWorld world, WorldManager wm)
        {
            if (isClient)
            {
                world.Travel(id);
                return;
            }

            GameBoard board = wm.GetBoardWithId(id);
            if (board == null)
            {
                return;
            }

            wm.GoToBoard(board, null, "default");
            SessionController.Instance.NotifyMyBoard(id);
            CoopLog.Info("travel board " + id);
        }

        private static List<string> HostBoardIds(WorldManager wm)
        {
            var ids = new List<string>();
            if (wm.Boards == null)
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

        private static string BoardName(string id)
        {
            string name;
            return BoardNames.TryGetValue(id, out name) ? name : id;
        }

        // ---------------------------------------------------------------- cursors
        // Remote cursors were removed: the extra arrow read as a bug more than as a feature.
        // PlayerName() below is still used by the chat and player list.

        /// <summary>Nickname if we know it, otherwise the peer id - never a bare "P0".</summary>
        private static string PlayerName(SessionController session, int playerId)
        {
            foreach (KeyValuePair<int, string> peer in session.Peers)
            {
                if (peer.Key == playerId && !string.IsNullOrEmpty(peer.Value))
                {
                    return peer.Value;
                }
            }

            return Loc.T("Player ", "玩家 ") + playerId;
        }

        private static int ParsePort(string text)
        {
            int port;
            return int.TryParse(text, out port) && port > 0 && port <= 65535
                ? port
                : StacklandsCoop.Net.TcpTransport.DefaultPort;
        }

        /// <summary>
        /// Called once per frame from CoopRuntime.Update. OnGUI runs several times per frame
        /// (Layout, Repaint and each event), so toggling from Draw() flipped the panel twice
        /// and the key appeared to do nothing.
        /// </summary>
        public static void PollHotkeys()
        {
            HandleToggleKey();
        }

        private static void HandleToggleKey()
        {
            if (!Hotkeys.F9Pressed())
            {
                return;
            }

            _visible = !_visible;
            EverOpened = true;
        }
    }
}
