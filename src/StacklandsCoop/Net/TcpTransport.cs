using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace StacklandsCoop.Net
{
    /// <summary>
    /// LAN / direct-IP fallback transport. Always reliable, so the session layer may
    /// use it for everything (handshake, commands, events) and only the Steam
    /// transport needs to distinguish reliable vs. unreliable sends.
    /// </summary>
    public sealed class TcpTransport : ITransport
    {
        public const int DefaultPort = 25565;

        /// <summary>Peer id used by a client for its connection to the host.</summary>
        public const int HostPeerId = 0;

        private sealed class Conn
        {
            public int PeerId;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
            public volatile bool Closed;
            public Thread ReaderThread;
        }

        private abstract class PendingEvent
        {
        }

        private sealed class PendingConnect : PendingEvent
        {
            public int PeerId;
            public string Address;
        }

        private sealed class PendingDisconnect : PendingEvent
        {
            public int PeerId;
            public string Reason;
        }

        private sealed class PendingMessage : PendingEvent
        {
            public int PeerId;
            public byte[] Payload;
        }

        private readonly ConcurrentQueue<PendingEvent> _pending = new ConcurrentQueue<PendingEvent>();
        private readonly List<Conn> _conns = new List<Conn>();
        private readonly object _connsLock = new object();

        private TcpListener _listener;
        private Thread _acceptThread;
        private TcpClient _dialClient;
        private volatile bool _running;
        private int _nextPeerId = 1;

        public bool IsRunning { get { return _running; } }

        public int LocalPort { get; private set; }

        public bool IsHost { get; private set; }

        public event Action<int, string> PeerConnected;

        public event Action<int, string> PeerDisconnected;

        public event Action<int, byte[]> MessageReceived;

        public bool StartHost(int port)
        {
            if (_running)
            {
                return false;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            catch (Exception ex)
            {
                CoopLog.Error("listen on " + port + " failed: " + ex.Message);
                _listener = null;
                return false;
            }

            _running = true;
            IsHost = true;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "CoopAccept"
            };
            _acceptThread.Start();
            CoopLog.Info("hosting on 0.0.0.0:" + LocalPort);
            return true;
        }

        public bool Connect(string host, int port)
        {
            if (_running)
            {
                return false;
            }

            if (!TryParseEndpoint(host, port, out string dialHost, out int dialPort))
            {
                CoopLog.Warn("dial failed: bad endpoint '" + host + "'");
                return false;
            }

            _running = true;
            IsHost = false;
            LocalPort = 0;
            CoopLog.Info("dialing " + dialHost + ":" + dialPort);
            var thread = new Thread(() => DialLoop(dialHost, dialPort))
            {
                IsBackground = true,
                Name = "CoopDial"
            };
            thread.Start();
            return true;
        }

        /// <summary>
        /// Accepts "1.2.3.4", "1.2.3.4:25566", "play.example.com:25566" and "[fe80::1]:25566"
        /// so the join field can take whatever the host pasted to the player.
        /// </summary>
        public static bool TryParseEndpoint(string input, int fallbackPort, out string host, out int port)
        {
            host = (input ?? string.Empty).Trim();
            port = fallbackPort;

            if (host.Length == 0)
            {
                return false;
            }

            if (host[0] == '[')
            {
                int close = host.IndexOf(']');
                if (close <= 1)
                {
                    return false;
                }

                string rest = host.Substring(close + 1);
                int parsed;
                if (rest.StartsWith(":") && int.TryParse(rest.Substring(1), out parsed) && parsed > 0 && parsed <= 65535)
                {
                    port = parsed;
                }

                host = host.Substring(1, close - 1);
                return host.Length > 0;
            }

            int colon = host.IndexOf(':');
            if (colon > 0 && host.IndexOf(':', colon + 1) < 0)
            {
                int parsed;
                if (int.TryParse(host.Substring(colon + 1), out parsed) && parsed > 0 && parsed <= 65535)
                {
                    port = parsed;
                    host = host.Substring(0, colon);
                }
            }

            return host.Length > 0;
        }

        public void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch (Exception)
            {
                // Socket is already gone.
            }

            lock (_connsLock)
            {
                for (int i = 0; i < _conns.Count; i++)
                {
                    CloseConn(_conns[i], "local shutdown");
                }

                _conns.Clear();
            }

            CoopLog.Info("transport stopped");
        }

        public void Poll()
        {
            PendingEvent ev;
            while (_pending.TryDequeue(out ev))
            {
                switch (ev)
                {
                    case PendingConnect connect:
                        SafeInvoke(PeerConnected, connect.PeerId, connect.Address);
                        break;
                    case PendingDisconnect disconnect:
                        SafeInvoke(PeerDisconnected, disconnect.PeerId, disconnect.Reason);
                        break;
                    case PendingMessage message:
                        SafeInvoke(MessageReceived, message.PeerId, message.Payload);
                        break;
                }
            }
        }

        public void Send(int peerId, byte[] payload)
        {
            Conn conn = FindConn(peerId);
            if (conn == null)
            {
                CoopLog.Warn("send to unknown peer " + peerId);
                return;
            }

            SendFrame(conn, payload);
        }

        public void Broadcast(byte[] payload)
        {
            Conn[] snapshot;
            lock (_connsLock)
            {
                snapshot = _conns.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                SendFrame(snapshot[i], payload);
            }
        }

        // TCP is a reliable stream: "unreliable" motion just takes the normal path.
        public void SendUnreliable(int peerId, byte[] payload)
        {
            Send(peerId, payload);
        }

        public void BroadcastUnreliable(byte[] payload)
        {
            Broadcast(payload);
        }

        public void Disconnect(int peerId)
        {
            Conn conn = FindConn(peerId);
            if (conn == null)
            {
                return;
            }

            CloseConn(conn, "peer timeout");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }

                if (client == null)
                {
                    continue;
                }

                var conn = new Conn
                {
                    PeerId = _nextPeerId++,
                    Client = client,
                    Stream = client.GetStream()
                };

                lock (_connsLock)
                {
                    _conns.Add(conn);
                }

                StartReader(conn);

                string address = "?";
                try
                {
                    address = client.Client.RemoteEndPoint != null
                        ? client.Client.RemoteEndPoint.ToString()
                        : "?";
                }
                catch (Exception)
                {
                    // Endpoint can vanish between accept and inspection.
                }

                _pending.Enqueue(new PendingConnect { PeerId = conn.PeerId, Address = address });
            }
        }

        private void DialLoop(string host, int port)
        {
            try
            {
                _dialClient = new TcpClient();
                _dialClient.Connect(host, port);
                _dialClient.NoDelay = true;
            }
            catch (Exception ex)
            {
                _running = false;
                _pending.Enqueue(new PendingDisconnect
                {
                    PeerId = HostPeerId,
                    Reason = "connect failed: " + ex.Message
                });
                return;
            }

            var conn = new Conn
            {
                PeerId = HostPeerId,
                Client = _dialClient,
                Stream = _dialClient.GetStream()
            };

            lock (_connsLock)
            {
                _conns.Add(conn);
            }

            StartReader(conn);
            _pending.Enqueue(new PendingConnect { PeerId = HostPeerId, Address = host + ":" + port });
        }

        private void StartReader(Conn conn)
        {
            conn.ReaderThread = new Thread(() => ReadLoop(conn))
            {
                IsBackground = true,
                Name = "CoopRead" + conn.PeerId
            };
            conn.ReaderThread.Start();
        }

        private void ReadLoop(Conn conn)
        {
            string reason = "peer closed";
            try
            {
                byte[] header = new byte[FrameCodec.HeaderSize];
                while (_running && !conn.Closed)
                {
                    if (!ReadExact(conn.Stream, header, FrameCodec.HeaderSize))
                    {
                        break;
                    }

                    int length = FrameCodec.ReadLength(header, 0);
                    if (length <= 0 || length > FrameCodec.MaxPayload)
                    {
                        reason = "bad frame length " + length;
                        break;
                    }

                    byte[] payload = new byte[length];
                    if (!ReadExact(conn.Stream, payload, length))
                    {
                        break;
                    }

                    _pending.Enqueue(new PendingMessage { PeerId = conn.PeerId, Payload = payload });
                }
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            CloseConn(conn, reason);
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n;
                try
                {
                    n = stream.Read(buffer, read, count - read);
                }
                catch (Exception)
                {
                    return false;
                }

                if (n <= 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }

        private void SendFrame(Conn conn, byte[] payload)
        {
            if (conn.Closed)
            {
                return;
            }

            byte[] frame = FrameCodec.Encode(payload);
            try
            {
                lock (conn.WriteLock)
                {
                    conn.Stream.Write(frame, 0, frame.Length);
                    conn.Stream.Flush();
                }
            }
            catch (Exception ex)
            {
                conn.Closed = true;
                _pending.Enqueue(new PendingDisconnect
                {
                    PeerId = conn.PeerId,
                    Reason = "send failed: " + ex.Message
                });
            }
        }

        private void CloseConn(Conn conn, string reason)
        {
            if (conn.Closed)
            {
                return;
            }

            conn.Closed = true;
            try
            {
                conn.Client.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }

            lock (_connsLock)
            {
                _conns.Remove(conn);
            }

            _pending.Enqueue(new PendingDisconnect { PeerId = conn.PeerId, Reason = reason });
        }

        private Conn FindConn(int peerId)
        {
            lock (_connsLock)
            {
                for (int i = 0; i < _conns.Count; i++)
                {
                    if (_conns[i].PeerId == peerId)
                    {
                        return _conns[i];
                    }
                }
            }

            return null;
        }

        private static void SafeInvoke<T1>(Action<T1> handler, T1 value)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                CoopLog.Error("event handler failed: " + ex);
            }
        }

        private static void SafeInvoke<T1, T2>(Action<T1, T2> handler, T1 first, T2 second)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(first, second);
            }
            catch (Exception ex)
            {
                CoopLog.Error("event handler failed: " + ex);
            }
        }
    }
}
