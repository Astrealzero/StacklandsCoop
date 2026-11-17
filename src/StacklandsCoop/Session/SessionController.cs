using System;
using System.Collections.Generic;
using StacklandsCoop.Net;
using StacklandsCoop.Protocol;
using UnityEngine;

namespace StacklandsCoop.Session
{
    public enum SessionRole
    {
        Offline,
        Host,
        Client
    }

    /// <summary>
    /// Milestone 1 session layer: start a host, join by IP, validate the handshake,
    /// keep the peer list. No world sync yet.
    /// </summary>
    public sealed class SessionController
    {
        public const int MaxPlayers = 4;

        public static readonly SessionController Instance = new SessionController();

        private readonly Dictionary<int, string> _peers = new Dictionary<int, string>();
        private ITransport _net;

        public SessionRole Role { get; private set; }

        public int LocalPlayerId { get; private set; } = -1;

        public string SessionToken { get; private set; } = string.Empty;

        public string HostName { get; private set; } = string.Empty;

        public int Epoch { get; private set; }

        public string Nick = "Player";

        /// <summary>Test hook: when set, the client announces this mods hash instead of the
        /// real one, which makes the host exercise its reject path.</summary>
        public string DebugModsHash;

        public int Port = TcpTransport.DefaultPort;

        /// <summary>True when the active room is carried over Steam P2P instead of TCP.</summary>
        public bool UseSteam { get; private set; }

        public string LastError { get; private set; } = string.Empty;

        public bool IsOnline { get { return Role != SessionRole.Offline; } }

        /// <summary>Last measured round-trip time to the host, in milliseconds (0 = unknown).</summary>
        public float RttMs { get; private set; }

        private int _pingSeq;
        private float _lastPingAt;
        private float _pingSentAt;

        private const float PingInterval = 2f;

        /// <summary>Human readable room address for the panel (Steam lobby id or TCP port).</summary>
        public string RoomInfo
        {
            get
            {
                var steam = _net as Net.SteamTransport;
                if (steam != null)
                {
                    ulong lobby = steam.LobbyId;
                    return lobby == 0 ? "Steam 大厅创建中…" : "Steam 大厅: " + lobby;
                }

                return "端口: " + Port;
            }
        }

        /// <summary>Copyable room code: the Steam lobby id, or empty for a plain IP room.</summary>
        public string RoomCode
        {
            get
            {
                var steam = _net as Net.SteamTransport;
                if (steam == null || steam.LobbyId == 0)
                {
                    return string.Empty;
                }

                return steam.LobbyId.ToString();
            }
        }

        /// <summary>True while a client is trying to get back into the host's room.</summary>
        public bool IsReconnecting { get { return _reconnecting; } }

        private const float ReconnectWindow = 60f;
        private const float RetryInterval = 2f;

        private string _hostAddress = string.Empty;
        private int _hostPort;
        private bool _reconnecting;
        private bool _intentionalStop;
        private float _reconnectDeadline;
        private float _nextRetry;

        public int PeerCount
        {
            get { return _peers.Count; }
        }

        /// <summary>Clients connected besides the local player (host: clients, client: 1).</summary>
        public int RemotePeerCount
        {
            get { return Math.Max(0, _peers.Count - 1); }
        }

        public IEnumerable<KeyValuePair<int, string>> Peers
        {
            get { return _peers; }
        }

        /// <summary>Which board each known peer is looking at, for the player list.</summary>
        public Dictionary<int, string> PlayerBoards
        {
            get { return _playerBoards; }
        }

        private readonly Dictionary<int, string> _playerBoards = new Dictionary<int, string>();

        /// <summary>Tells the host (and through it, everyone) which board this player stands on.</summary>
        public void NotifyMyBoard(string boardId)
        {
            if (Role == SessionRole.Offline || string.IsNullOrEmpty(boardId))
            {
                return;
            }

            int id = LocalPlayerId;
            _playerBoards[id] = boardId;
            Send(Messages.PlayerBoard(id, boardId));
            CoopLog.Info("board notify " + boardId);
        }

        public void StartHost(int port, string nick)
        {
            StartHost(port, nick, false);
        }

        public void StartHost(int port, string nick, bool useSteam)
        {
            Stop();

            Nick = string.IsNullOrEmpty(nick) ? "Host" : nick;
            UseSteam = useSteam && Net.SteamTransport.Available();
            var net = CreateTransport();
            if (!net.StartHost(port))
            {
                LastError = UseSteam ? UI.Loc.T("Could not create the Steam room", "Steam 房间创建失败") : UI.Loc.T("Cannot listen on port ", "端口 ") + port + UI.Loc.T("", " 无法监听");
                CoopLog.Error(LastError);
                Stop();
                return;
            }

            Attach(net);
            Role = SessionRole.Host;
            LocalPlayerId = 0;
            SessionToken = Guid.NewGuid().ToString("N");
            HostName = Environment.MachineName;
            Epoch = 0;
            _peers[0] = Nick + " (主机)";
            CoopLog.Info(UseSteam
                ? "Steam 房间已创建，等待好友加入"
                : "房间已创建，端口 " + net.LocalPort + "，等待玩家");
        }

        public void Join(string host, int port, string nick)
        {
            Join(host, port, nick, false);
        }

        public void Join(string host, int port, string nick, bool useSteam)
        {
            Stop();

            Nick = string.IsNullOrEmpty(nick) ? "Player" : nick;
            UseSteam = useSteam && Net.SteamTransport.Available();

            string targetHost = host;
            int targetPort = port;
            if (!UseSteam && !Net.TcpTransport.TryParseEndpoint(host, port, out targetHost, out targetPort))
            {
                LastError = UI.Loc.T("That address does not look right.", "地址格式不对，检查一下。");
                return;
            }

            _hostAddress = targetHost;
            _hostPort = targetPort;
            _intentionalStop = false;
            _reconnecting = false;
            var net = CreateTransport();
            Attach(net);
            Role = SessionRole.Client;
            net.Connect(targetHost, targetPort);
            CoopLog.Info((UseSteam ? "正在通过 Steam 连接 " : "正在连接 ") + targetHost + ":" + targetPort);
        }

        private Net.ITransport CreateTransport()
        {
            if (UseSteam && Net.SteamTransport.Available())
            {
                return new Net.SteamTransport();
            }

            return new Net.TcpTransport();
        }

        public void Stop()
        {
            _intentionalStop = true;
            _reconnecting = false;
            if (_net != null)
            {
                _net.PeerConnected -= OnPeerConnected;
                _net.PeerDisconnected -= OnPeerDisconnected;
                _net.MessageReceived -= OnMessage;
                _net.Stop();
                _net = null;
            }

            if (Role != SessionRole.Offline)
            {
                CoopLog.Info("会话已结束");
            }

            Role = SessionRole.Offline;
            LocalPlayerId = -1;
            UseSteam = false;
            _peers.Clear();
            _peerSeen.Clear();
            _playerBoards.Clear();
            UI.ChatLog.Clear();
            World.MonthFlow.Instance.Reset();
            World.CommandHost.Instance.ReleaseAll();
            World.ClientBoot.Reset();
        }

        public void Poll()
        {
            if (_net != null)
            {
                _net.Poll();
            }

            PollReconnect();
            PollPing();
            PollPeerTimeouts();
        }

        /// <summary>
        /// Keeps a fresh round-trip estimate on screen so a bad connection is visible before it
        /// turns into "why is everything laggy". One probe every two seconds is cheap.
        /// </summary>
        private void PollPing()
        {
            if (Role != SessionRole.Client || _net == null || _reconnecting)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now - _lastPingAt < PingInterval)
            {
                return;
            }

            _lastPingAt = now;
            _pingSentAt = now;
            _pingSeq++;
            Send(Messages.Ping(_pingSeq));
        }

        private void HandlePong(int seq)
        {
            if (seq != _pingSeq)
            {
                return;
            }

            float rtt = (Time.unscaledTime - _pingSentAt) * 1000f;
            if (rtt < 0f || rtt > PingInterval * 2000f)
            {
                return;
            }

            RttMs = rtt;
            if (Time.unscaledTime - _pingLoggedAt > 10f)
            {
                _pingLoggedAt = Time.unscaledTime;
                CoopLog.Info("ping ok rtt=" + Mathf.RoundToInt(rtt) + "ms");
            }
        }

        private float _pingLoggedAt;

        /// <summary>
        /// A dropped client keeps its room slot for 60 s: the world it was watching stays on
        /// screen, and every 2 s we dial the host again. World state never lives on the client,
        /// so a successful rejoin just replays the host's snapshot.
        /// </summary>
        private void PollReconnect()
        {
            if (!_reconnecting)
            {
                return;
            }

            float now = UnityEngine.Time.unscaledTime;
            if (now >= _reconnectDeadline)
            {
                _reconnecting = false;
                LastError = UI.Loc.T("Reconnect timed out.", "重连超时");
                CoopLog.Warn("重连超时，回到离线状态");
                Stop();
                return;
            }

            if (now < _nextRetry)
            {
                return;
            }

            _nextRetry = now + RetryInterval;
            CoopLog.Info("reconnecting 正在重连 " + _hostAddress + ":" + _hostPort);
            var net = CreateTransport();
            Attach(net);
            net.Connect(_hostAddress, _hostPort);
        }

        private void Attach(ITransport net)
        {
            _net = net;
            net.PeerConnected += OnPeerConnected;
            net.PeerDisconnected += OnPeerDisconnected;
            net.MessageReceived += OnMessage;
        }

        private void OnPeerConnected(int peerId, string address)
        {
            CoopLog.Info("连接建立 peer=" + peerId + " (" + address + ")");

            if (Role == SessionRole.Host)
            {
                _peerSeen[peerId] = Time.unscaledTime;
                UI.StatusBanner.Show(UI.Loc.T("Player ", "玩家 ") + peerId + UI.Loc.T(" is joining…", " 正在加入…"));
            }

            if (Role == SessionRole.Client)
            {
                Send(Messages.Hello(
                    Nick,
                    CoopMod.ModVersion,
                    ProtocolInfo.DevDefsHash,
                    string.IsNullOrEmpty(DebugModsHash) ? Protocol.Fingerprint.ModsHash() : DebugModsHash));
            }
        }

        private readonly Dictionary<int, float> _peerSeen = new Dictionary<int, float>();

        private float _nextPeerTimeoutCheck;

        private const float PeerTimeoutSeconds = 12f;

        /// <summary>
        /// A client whose process died mid-frame leaves a half-open TCP socket (or a Steam
        /// connection stuck in Dead) that never raises a disconnect. Without this the host
        /// keeps the ghost peer, its held cards and its "ready" vote forever.
        /// </summary>
        private void PollPeerTimeouts()
        {
            if (Role != SessionRole.Host || _net == null || Time.unscaledTime < _nextPeerTimeoutCheck)
            {
                return;
            }

            _nextPeerTimeoutCheck = Time.unscaledTime + 1f;

            List<int> stale = null;
            foreach (KeyValuePair<int, float> entry in _peerSeen)
            {
                if (Time.unscaledTime - entry.Value > PeerTimeoutSeconds)
                {
                    if (stale == null)
                    {
                        stale = new List<int>();
                    }

                    stale.Add(entry.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            for (int i = 0; i < stale.Count; i++)
            {
                int peerId = stale[i];
                CoopLog.Warn("peer " + peerId + " timeout (" + Mathf.RoundToInt(PeerTimeoutSeconds) + "s 无消息)，主动断开");
                _peerSeen.Remove(peerId);
                _net.Disconnect(peerId);
            }
        }

        private void OnPeerDisconnected(int peerId, string reason)
        {
            CoopLog.Warn("peer " + peerId + " 断开: " + reason);
            _peers.Remove(peerId);
            _peerSeen.Remove(peerId);

            if (Role == SessionRole.Host)
            {
                World.CommandHost.Instance.ReleasePeer(peerId);
                World.MonthFlow.Instance.OnPeerLeft(peerId);
            }

            if (Role == SessionRole.Client)
            {
                LastError = UI.Loc.T("Disconnected from the host: ", "与主机断开: ") + reason;
                _peers.Clear();
                _playerBoards.Clear();
                if (_intentionalStop)
                {
                    Stop();
                    return;
                }

                if (_net != null)
                {
                    _net.PeerConnected -= OnPeerConnected;
                    _net.PeerDisconnected -= OnPeerDisconnected;
                    _net.MessageReceived -= OnMessage;
                    _net.Stop();
                    _net = null;
                }

                _reconnecting = true;
                _reconnectDeadline = UnityEngine.Time.unscaledTime + ReconnectWindow;
                _nextRetry = UnityEngine.Time.unscaledTime + RetryInterval;
                CoopLog.Warn("连接中断，60 秒内自动重连");
                UI.StatusBanner.Show(UI.Loc.T("Lost the host, reconnecting…", "与主机断开，正在重连…"), 6f);
            }
        }

        private void OnMessage(int peerId, byte[] payload)
        {
            if (Role == SessionRole.Host && payload != null && payload.Length > 0)
            {
                _peerSeen[peerId] = Time.unscaledTime;
            }

            MsgType type;
            try
            {
                type = (MsgType)payload[0];
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                switch (type)
                {
                    case MsgType.Hello:
                        HandleHello(peerId, payload);
                        break;
                    case MsgType.Welcome:
                        HandleWelcome(payload);
                        break;
                    case MsgType.Reject:
                        HandleReject(payload);
                        break;
                    case MsgType.Event:
                        string notice = Messages.ParseNotice(payload);
                        CoopLog.Info("通知: " + notice);
                        if (notice.Contains("busy:") || notice.Contains("被使用中"))
                        {
                            World.ClientWorld.Instance.HandleReject();
                        }

                        break;
                    case MsgType.SyncStart:
                    case MsgType.SyncChunk:
                    case MsgType.SyncEnd:
                        if (Role == SessionRole.Client)
                        {
                            World.ClientWorld.Instance.Handle(payload);
                        }

                        break;
                    case MsgType.Cmd:
                        if (Role == SessionRole.Host)
                        {
                            World.CommandHost.Instance.Handle(peerId, payload);
                        }

                        break;
                    case MsgType.Ready:
                        if (Role == SessionRole.Host)
                        {
                            World.MonthFlow.Instance.OnReady(peerId, Messages.ParseReady(payload));
                        }

                        break;
                    case MsgType.SyncRequest:
                        if (Role == SessionRole.Host)
                        {
                            World.HostWorld.Instance.RequestImmediate();
                            CoopLog.Info("客户端请求快照，立即下发");
                        }

                        break;
                    case MsgType.Ping:
                        if (Role == SessionRole.Host)
                        {
                            _net.Send(peerId, Messages.Pong(Messages.ParsePing(payload)));
                        }

                        break;
                    case MsgType.Pong:
                        HandlePong(Messages.ParsePong(payload));
                        break;
                    case MsgType.Pause:
                        float pauseSpeed;
                        bool paused = Messages.ParsePause(payload, out pauseSpeed);
                        World.MonthFlow.Instance.OnHostPause(paused, pauseSpeed);
                        break;
                    case MsgType.Pos:
                        if (Role == SessionRole.Client)
                        {
                            World.ClientWorld.Instance.HandlePos(payload);
                        }

                        break;
                    case MsgType.PackPos:
                        if (Role == SessionRole.Client)
                        {
                            World.ClientWorld.Instance.HandlePackPos(payload);
                        }

                        break;
                    case MsgType.TimerStart:
                    case MsgType.TimerStop:
                        if (Role == SessionRole.Client)
                        {
                            World.ClientWorld.Instance.Handle(payload);
                        }

                        break;
                    case MsgType.Attack:
                        if (Role == SessionRole.Client)
                        {
                            uint attackerNet;
                            uint targetNet;
                            Messages.ParseAttack(payload, out attackerNet, out targetNet);
                            World.ClientWorld.Instance.HandleAttack(attackerNet, targetNet);
                        }

                        break;
                    case MsgType.Hit:
                        if (Role == SessionRole.Client)
                        {
                            uint hitNet;
                            ushort hitDamage;
                            bool hitCrit;
                            Messages.ParseHit(payload, out hitNet, out hitDamage, out hitCrit);
                            World.ClientWorld.Instance.HandleHit(hitNet, hitDamage, hitCrit);
                        }

                        break;
                    case MsgType.Cursor:
                        // Cursors were removed (the extra ghost arrow annoyed more than it helped).
                        // The message id stays reserved so an older peer cannot desync the stream.
                        break;
                    case MsgType.PlayerBoard:
                        int boardFrom;
                        string boardId;
                        Messages.ParsePlayerBoard(payload, out boardFrom, out boardId);
                        if (Role == SessionRole.Host)
                        {
                            // Relay with the socket peer id so a client cannot claim another name.
                            _playerBoards[peerId] = boardId;
                            _net.Broadcast(Messages.PlayerBoard(peerId, boardId));
                            CoopLog.Info("player board P" + peerId + " " + boardId);
                        }
                        else
                        {
                            _playerBoards[boardFrom] = boardId;
                        }

                        break;
                    case MsgType.Chat:
                        int chatFrom;
                        string chatText;
                        Messages.ParseChat(payload, out chatFrom, out chatText);
                        if (Role == SessionRole.Host)
                        {
                            // Relaying: ignore the claimed sender id, use the socket peer id.
                            _net.Broadcast(Messages.Chat(peerId, chatText));
                            UI.ChatLog.Add(peerId, chatText);
                            CoopLog.Info("chat P" + peerId + ": " + chatText);
                        }
                        else
                        {
                            UI.ChatLog.Add(chatFrom, chatText);
                            CoopLog.Info("chat P" + chatFrom + ": " + chatText);
                        }

                        break;
                    default:
                        CoopLog.Warn("忽略了未实现的消息 " + type);
                        break;
                }
            }
            catch (Exception ex)
            {
                CoopLog.Error("处理 " + type + " 失败: " + ex.Message);
            }
        }

        private void HandleHello(int peerId, byte[] payload)
        {
            if (Role != SessionRole.Host)
            {
                return;
            }

            if (_peers.Count >= MaxPlayers)
            {
                _net.Send(peerId, Messages.Reject(RejectCode.LobbyFull, "房间已满（最多 " + MaxPlayers + " 人）"));
                return;
            }

            HelloInfo hello = Messages.ParseHello(payload);
            CoopLog.Info("收到 Hello: " + hello.Nick + " / v" + hello.ModVersion + " / mods " + hello.ModsHash + " / 本机 " + Protocol.Fingerprint.ModsHash());

            if (hello.Protocol != ProtocolInfo.Version)
            {
                _net.Send(peerId, Messages.Reject(
                    RejectCode.VersionMismatch,
                    "协议版本不一致（主机 " + ProtocolInfo.Version + "，对方 " + hello.Protocol + "）"));
                return;
            }

            if (hello.DefsHash != ProtocolInfo.DevDefsHash)
            {
                _net.Send(peerId, Messages.Reject(RejectCode.DefsMismatch, "卡牌/卡包定义不一致"));
                return;
            }

            string modsHash = Protocol.Fingerprint.ModsHash();
            CoopLog.Info("mods check mods=" + hello.ModsHash + " local=" + modsHash +
                         " ok=" + (hello.ModsHash == modsHash ? "true" : "false"));
            if (hello.ModsHash != modsHash)
            {
                _net.Send(peerId, Messages.Reject(
                    RejectCode.ModsMismatch,
                    "已安装 mod 列表不一致。主机: " + Protocol.Fingerprint.Describe() +
                    " / 对方: " + hello.ModsHash));
                CoopLog.Warn("拒绝 " + hello.Nick + "：mod 列表不一致（主机 " + modsHash + "，对方 " + hello.ModsHash + "）");
                CoopLog.Warn("reject mods mismatch peer=" + peerId);
                return;
            }

            _peers[peerId] = hello.Nick;
            _net.Send(peerId, Messages.Welcome(peerId, SessionToken, HostName, Epoch));
            _net.Send(peerId, Messages.Notice(UI.Loc.T("Welcome ", "欢迎 ") + hello.Nick + UI.Loc.T(" — joined (", " 加入（") + _peers.Count + "/" + MaxPlayers + ")"));
            CoopLog.Info(hello.Nick + " 加入，当前 " + _peers.Count + " 人");
            UI.StatusBanner.Show(UI.Loc.T("Player ", "玩家 ") + peerId + UI.Loc.T(" joined (", "（") + hello.Nick + UI.Loc.T(")", "）加入"));
        }

        private void HandleWelcome(byte[] payload)
        {
            if (Role != SessionRole.Client)
            {
                return;
            }

            WelcomeInfo welcome = Messages.ParseWelcome(payload);
            LocalPlayerId = welcome.PlayerId;
            SessionToken = welcome.SessionToken;
            HostName = welcome.HostName;
            Epoch = welcome.Epoch;
            if (_reconnecting)
            {
                _reconnecting = false;
                CoopLog.Info("reconnected 重连成功，等待主机快照");
                UI.StatusBanner.Show(UI.Loc.T("Reconnected to the host, syncing the world…", "已重连主机，正在同步世界…"), 5f);
            }
            else
            {
                UI.StatusBanner.Show(UI.Loc.T("Connected to the host ", "已连接主机 ") + HostName, 5f);
            }
            CoopLog.Info("握手成功，玩家 id=" + LocalPlayerId + "，主机 " + HostName + "，epoch=" + Epoch);
            Send(Messages.Notice(Nick + UI.Loc.T(" joined", " 已加入")));
        }

        private void HandleReject(byte[] payload)
        {
            RejectInfo reject = Messages.ParseReject(payload);
            LastError = UI.Loc.T("Rejected: ", "被拒绝: ") + reject.Text;
            CoopLog.Error(LastError);
            UI.StatusBanner.Show(UI.Loc.T("Join rejected: ", "加入被拒绝：") + reject.Text, 8f);
            Stop();
        }

        public void Send(byte[] payload)
        {
            if (_net == null)
            {
                return;
            }

            if (Role == SessionRole.Client)
            {
                _net.Send(TcpTransport.HostPeerId, payload);
            }
            else
            {
                _net.Broadcast(payload);
            }
        }

        /// <summary>Host-only: fire-and-forget to every connected client.</summary>
        public void Broadcast(byte[] payload)
        {
            if (_net == null || Role != SessionRole.Host)
            {
                return;
            }

            _net.Broadcast(payload);
        }

        /// <summary>Host-only: motion traffic (may be dropped, may arrive out of order).</summary>
        public void BroadcastUnreliable(byte[] payload)
        {
            if (_net == null || Role != SessionRole.Host)
            {
                return;
            }

            _net.BroadcastUnreliable(payload);
        }

        /// <summary>Client-only: motion traffic to the host.</summary>
        public void SendUnreliable(byte[] payload)
        {
            if (_net == null || Role != SessionRole.Client)
            {
                return;
            }

            _net.SendUnreliable(TcpTransport.HostPeerId, payload);
        }

        /// <summary>Host-only: send to one connected client.</summary>
        public void SendTo(int peerId, byte[] payload)
        {
            if (_net == null || Role != SessionRole.Host)
            {
                return;
            }

            _net.Send(peerId, payload);
        }
    }
}
