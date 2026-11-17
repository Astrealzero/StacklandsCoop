using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace StacklandsCoop.Net
{
    /// <summary>
    /// Steam P2P transport on SteamNetworkingSockets plus a friends-only lobby for
    /// invites. The game's SteamManager pumps SteamAPI.RunCallbacks every frame, so
    /// this class only registers callbacks. Peer ids mirror TCP: host 0, clients 1..n.
    /// Every entry point fails softly when Steam is unavailable so IP join stays usable.
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        private const int SendReliable = 0;

        /// <summary>Unreliable + NoNagle + NoDelay: motion packets must not queue behind anything.</summary>
        private static readonly int SteamUnreliable = Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoDelay;

        private sealed class Pending
        {
            public int Kind; // 0 connect, 1 disconnect, 2 message
            public int PeerId;
            public string Info;
            public byte[] Payload;
        }

        private readonly ConcurrentQueue<Pending> _pending = new ConcurrentQueue<Pending>();
        private readonly Dictionary<int, HSteamNetConnection> _connByPeer = new Dictionary<int, HSteamNetConnection>();
        private readonly Dictionary<HSteamNetConnection, int> _peerByConn = new Dictionary<HSteamNetConnection, int>();
        private readonly object _connLock = new object();

        private Callback<SteamNetConnectionStatusChangedCallback_t> _connStatusCallback;
        private Callback<LobbyEnter_t> _lobbyEnterCallback;
        private CallResult<LobbyCreated_t> _lobbyCreatedCall;
        private CallResult<LobbyEnter_t> _joinResultCall;

        private HSteamListenSocket _listenSocket;
        private HSteamNetPollGroup _pollGroup;
        private CSteamID _lobby = CSteamID.Nil;
        private int _nextPeerId = 1;
        private bool _running;
        private bool _host;

        public bool IsRunning { get { return _running; } }

        public int LocalPort { get { return 0; } }

        public bool IsHost { get { return _host; } }

        public CSteamID Lobby { get { return _lobby; } }

        /// <summary>Lobby id once the host's lobby exists, 0 before that.</summary>
        public ulong LobbyId { get { return _lobby.m_SteamID; } }

        public event Action<int, string> PeerConnected;
        public event Action<int, string> PeerDisconnected;
        public event Action<int, byte[]> MessageReceived;

        public static bool Available()
        {
            try
            {
                return SteamAPI.IsSteamRunning();
            }
            catch
            {
                return false;
            }
        }

        public bool StartHost(int port)
        {
            if (!Available())
            {
                CoopLog.Warn("Steam 不可用：无法创建 P2P 房间（请从 Steam 启动游戏）");
                return false;
            }

            try
            {
                _host = true;
                _pollGroup = SteamNetworkingSockets.CreatePollGroup();
                _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
                if (_listenSocket == HSteamListenSocket.Invalid || _pollGroup == HSteamNetPollGroup.Invalid)
                {
                    CoopLog.Warn("Steam P2P 监听失败");
                    Stop();
                    return false;
                }

                _connStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                _lobbyCreatedCall = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                _lobbyEnterCallback = Callback<LobbyEnter_t>.Create(OnLobbyEnter);

                _running = true;
                CoopLog.Info("Steam P2P 已监听，正在创建好友大厅");
                SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 4);
                _lobbyCreatedCall.Set(call);
                return true;
            }
            catch (Exception ex)
            {
                CoopLog.Error("Steam 建房失败: " + ex.Message);
                Stop();
                return false;
            }
        }

        /// <summary>Join by SteamID64, or by "lobby:&lt;lobbyId&gt;" to resolve the owner.</summary>
        public bool Connect(string host, int port)
        {
            if (!Available())
            {
                CoopLog.Warn("Steam 不可用：无法通过 Steam 连接");
                return false;
            }

            try
            {
                _host = false;
                _pollGroup = SteamNetworkingSockets.CreatePollGroup();
                _connStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                _lobbyEnterCallback = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                _joinResultCall = CallResult<LobbyEnter_t>.Create(OnJoinResult);
                _running = true;

                string text = (host ?? string.Empty).Trim();
                if (text.StartsWith("lobby:", StringComparison.OrdinalIgnoreCase))
                {
                    ulong lobbyId;
                    if (!ulong.TryParse(text.Substring(6), out lobbyId))
                    {
                        CoopLog.Warn("大厅 id 无效: " + text);
                        return false;
                    }

                    CoopLog.Info("正在加入大厅 " + lobbyId);
                    SteamAPICall_t call = SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
                    _joinResultCall.Set(call);
                    return true;
                }

                ulong steamId64;
                if (!ulong.TryParse(text, out steamId64))
                {
                    CoopLog.Warn("需要 SteamID64 或 lobby:<id>");
                    return false;
                }

                ConnectToSteamId(new CSteamID(steamId64));
                return true;
            }
            catch (Exception ex)
            {
                CoopLog.Error("Steam 加入失败: " + ex.Message);
                Stop();
                return false;
            }
        }

        private void ConnectToSteamId(CSteamID owner)
        {
            if (owner == CSteamID.Nil)
            {
                CoopLog.Warn("大厅没有房主，无法连接");
                return;
            }

            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID(owner);
            HSteamNetConnection conn = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
            if (conn == HSteamNetConnection.Invalid)
            {
                CoopLog.Warn("Steam P2P 连接发起失败");
                return;
            }

            CoopLog.Info("正在连接房主 SteamID " + owner.m_SteamID);
        }

        public void Stop()
        {
            _running = false;
            try
            {
                List<HSteamNetConnection> conns = new List<HSteamNetConnection>();
                lock (_connLock)
                {
                    conns.AddRange(_connByPeer.Values);
                    _connByPeer.Clear();
                    _peerByConn.Clear();
                }

                foreach (HSteamNetConnection conn in conns)
                {
                    SteamNetworkingSockets.CloseConnection(conn, 0, "bye", false);
                }

                if (_pollGroup != HSteamNetPollGroup.Invalid)
                {
                    SteamNetworkingSockets.DestroyPollGroup(_pollGroup);
                    _pollGroup = HSteamNetPollGroup.Invalid;
                }

                if (_listenSocket != HSteamListenSocket.Invalid)
                {
                    SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                    _listenSocket = HSteamListenSocket.Invalid;
                }

                if (_host && _lobby != CSteamID.Nil)
                {
                    SteamMatchmaking.LeaveLobby(_lobby);
                }

                _lobby = CSteamID.Nil;
                _connStatusCallback = null;
                _lobbyEnterCallback = null;
                _lobbyCreatedCall = null;
                _joinResultCall = null;
            }
            catch (Exception ex)
            {
                CoopLog.Warn("Steam 关闭失败: " + ex.Message);
            }
        }

        public void Send(int peerId, byte[] payload)
        {
            HSteamNetConnection conn;
            lock (_connLock)
            {
                if (!_connByPeer.TryGetValue(peerId, out conn))
                {
                    return;
                }
            }

            SendInternal(conn, payload);
        }

        public void Broadcast(byte[] payload)
        {
            List<HSteamNetConnection> targets = new List<HSteamNetConnection>();
            lock (_connLock)
            {
                targets.AddRange(_connByPeer.Values);
            }

            foreach (HSteamNetConnection conn in targets)
            {
                SendInternal(conn, payload);
            }
        }

        public void SendUnreliable(int peerId, byte[] payload)
        {
            HSteamNetConnection conn;
            lock (_connLock)
            {
                if (!_connByPeer.TryGetValue(peerId, out conn))
                {
                    return;
                }
            }

            SendInternal(conn, payload, SteamUnreliable);
        }

        public void BroadcastUnreliable(byte[] payload)
        {
            List<HSteamNetConnection> targets = new List<HSteamNetConnection>();
            lock (_connLock)
            {
                targets.AddRange(_connByPeer.Values);
            }

            foreach (HSteamNetConnection conn in targets)
            {
                SendInternal(conn, payload, SteamUnreliable);
            }
        }

        public void Disconnect(int peerId)
        {
            HSteamNetConnection conn;
            lock (_connLock)
            {
                if (!_connByPeer.TryGetValue(peerId, out conn))
                {
                    return;
                }
            }

            try
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "peer timeout", false);
            }
            catch (Exception ex)
            {
                CoopLog.Warn("Steam 断开连接失败: " + ex.Message);
            }
        }

        private static void SendInternal(HSteamNetConnection conn, byte[] payload, int flags = SendReliable)
        {
            if (payload == null || payload.Length == 0)
            {
                return;
            }

            IntPtr buffer = Marshal.AllocHGlobal(payload.Length);
            try
            {
                Marshal.Copy(payload, 0, buffer, payload.Length);
                long messageNumber;
                SteamNetworkingSockets.SendMessageToConnection(conn, buffer, (uint)payload.Length, flags, out messageNumber);
            }
            catch (Exception ex)
            {
                CoopLog.Warn("Steam 发包失败: " + ex.Message);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Poll()
        {
            if (_running)
            {
                try
                {
                    Receive();
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("Steam 收包失败: " + ex.Message);
                }
            }

            DrainPending();

            if (_running && _host && Time.unscaledTime >= _nextLobbyMeta)
            {
                _nextLobbyMeta = Time.unscaledTime + 5f;
                SetLobbyMeta();
            }
        }

        private float _nextLobbyMeta;

        /// <summary>
        /// Publishes host name and occupancy into the lobby so friends see the room in the
        /// Steam friends-list "join game" entry instead of a nameless lobby.
        /// </summary>
        private void SetLobbyMeta()
        {
            if (_lobby.m_SteamID == 0)
            {
                return;
            }

            try
            {
                SteamMatchmaking.SetLobbyData(_lobby, "host", SteamFriends.GetPersonaName());
                SteamMatchmaking.SetLobbyData(_lobby, "mod", "StacklandsCoop " + CoopMod.ModVersion);
                SteamMatchmaking.SetLobbyData(_lobby, "slots", "4");
                SteamMatchmaking.SetLobbyData(
                    _lobby,
                    "players",
                    SteamMatchmaking.GetNumLobbyMembers(_lobby).ToString());
            }
            catch (Exception ex)
            {
                CoopLog.Warn("更新大厅信息失败: " + ex.Message);
            }
        }

        private void Receive()
        {
            if (_pollGroup == HSteamNetPollGroup.Invalid)
            {
                return;
            }

            IntPtr[] pointers = new IntPtr[64];
            int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, pointers, pointers.Length);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointers[i]);
                    int peerId;
                    lock (_connLock)
                    {
                        if (!_peerByConn.TryGetValue(message.m_conn, out peerId))
                        {
                            continue;
                        }
                    }

                    byte[] payload = new byte[message.m_cbSize];
                    if (message.m_cbSize > 0)
                    {
                        Marshal.Copy(message.m_pData, payload, 0, message.m_cbSize);
                    }

                    _pending.Enqueue(new Pending { Kind = 2, PeerId = peerId, Payload = payload });
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("Steam 消息解析失败: " + ex.Message);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointers[i]);
                }
            }
        }

        private void DrainPending()
        {
            Pending item;
            while (_pending.TryDequeue(out item))
            {
                try
                {
                    if (item.Kind == 0)
                    {
                        Action<int, string> connected = PeerConnected;
                        if (connected != null)
                        {
                            connected(item.PeerId, item.Info);
                        }
                    }
                    else if (item.Kind == 1)
                    {
                        Action<int, string> disconnected = PeerDisconnected;
                        if (disconnected != null)
                        {
                            disconnected(item.PeerId, item.Info);
                        }
                    }
                    else
                    {
                        Action<int, byte[]> received = MessageReceived;
                        if (received != null)
                        {
                            received(item.PeerId, item.Payload);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CoopLog.Error("Steam 事件派发失败: " + ex.Message);
                }
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t data)
        {
            try
            {
                ESteamNetworkingConnectionState state = data.m_info.m_eState;

                if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && _host)
                {
                    if (SteamNetworkingSockets.AcceptConnection(data.m_hConn) == EResult.k_EResultOK)
                    {
                        SteamNetworkingSockets.SetConnectionPollGroup(data.m_hConn, _pollGroup);
                    }
                    else
                    {
                        SteamNetworkingSockets.CloseConnection(data.m_hConn, 0, "rejected", false);
                    }

                    return;
                }

                if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                {
                    int peerId;
                    bool isNew = false;
                    lock (_connLock)
                    {
                        if (!_peerByConn.TryGetValue(data.m_hConn, out peerId))
                        {
                            peerId = _host ? _nextPeerId++ : 0;
                            _peerByConn[data.m_hConn] = peerId;
                            _connByPeer[peerId] = data.m_hConn;
                            isNew = true;
                        }
                    }

                    if (!isNew)
                    {
                        return;
                    }

                    SteamNetworkingSockets.SetConnectionPollGroup(data.m_hConn, _pollGroup);

                    string info = "steam";
                    try
                    {
                        info = data.m_info.m_identityRemote.GetSteamID().m_SteamID.ToString();
                    }
                    catch
                    {
                    }

                    _pending.Enqueue(new Pending { Kind = 0, PeerId = peerId, Info = info });
                    return;
                }

                if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                    state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                {
                    RemoveConnection(data.m_hConn);
                }
            }
            catch (Exception ex)
            {
                CoopLog.Warn("Steam 连接状态处理失败: " + ex.Message);
            }
        }

        private void RemoveConnection(HSteamNetConnection conn)
        {
            int peerId;
            bool known = false;
            lock (_connLock)
            {
                if (_peerByConn.TryGetValue(conn, out peerId))
                {
                    _peerByConn.Remove(conn);
                    _connByPeer.Remove(peerId);
                    known = true;
                }
            }

            if (known)
            {
                _pending.Enqueue(new Pending { Kind = 1, PeerId = peerId, Info = "steam closed" });
            }
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            try
            {
                if (ioFailure || result.m_eResult != EResult.k_EResultOK)
                {
                    CoopLog.Warn("创建 Steam 大厅失败: " + result.m_eResult);
                    return;
                }

                _lobby = new CSteamID(result.m_ulSteamIDLobby);
                SteamMatchmaking.SetLobbyData(_lobby, "stacklands_coop", "1");
                SetLobbyMeta();
                CoopLog.Info("Steam 大厅已创建 " + _lobby.m_SteamID);
                CoopLog.Info("lobby created " + _lobby.m_SteamID);
                try
                {
                    SteamFriends.ActivateGameOverlayInviteDialog(_lobby);
                }
                catch (Exception ex)
                {
                    CoopLog.Warn("打开好友邀请界面失败: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                CoopLog.Warn("大厅创建回调失败: " + ex.Message);
            }
        }

        private void OnLobbyEnter(LobbyEnter_t data)
        {
            try
            {
                if (data.m_EChatRoomEnterResponse != 1)
                {
                    CoopLog.Warn("进入大厅失败: " + data.m_EChatRoomEnterResponse);
                    return;
                }

                if (_host)
                {
                    return;
                }

                CSteamID lobby = new CSteamID(data.m_ulSteamIDLobby);
                _lobby = lobby;
                ConnectToSteamId(SteamMatchmaking.GetLobbyOwner(lobby));
            }
            catch (Exception ex)
            {
                CoopLog.Warn("大厅进入回调失败: " + ex.Message);
            }
        }

        private void OnJoinResult(LobbyEnter_t result, bool ioFailure)
        {
            if (ioFailure)
            {
                CoopLog.Warn("加入大厅失败（IO）");
            }
        }
    }
}
