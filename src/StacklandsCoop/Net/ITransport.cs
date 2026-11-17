using System;

namespace StacklandsCoop.Net
{
    /// <summary>
    /// Transport abstraction. The session layer only knows about peers and opaque
    /// payloads; reliability is a transport concern (Steam sockets expose both,
    /// TCP is always reliable).
    /// </summary>
    public interface ITransport
    {
        bool IsRunning { get; }

        int LocalPort { get; }

        bool IsHost { get; }

        bool StartHost(int port);

        bool Connect(string host, int port);

        void Stop();

        /// <summary>Drain pending events on the main thread. Must be called every frame.</summary>
        void Poll();

        void Send(int peerId, byte[] payload);

        void Broadcast(byte[] payload);

        /// <summary>
        /// Motion traffic (dragged card / pack positions): best effort, may be dropped and may
        /// arrive out of order. TCP has no such mode, so it degrades to the reliable path there.
        /// </summary>
        void SendUnreliable(int peerId, byte[] payload);

        void BroadcastUnreliable(byte[] payload);

        /// <summary>
        /// Drop one peer from the host side (a silent client that stopped answering).
        /// Raises PeerDisconnected like any other drop.
        /// </summary>
        void Disconnect(int peerId);

        event Action<int, string> PeerConnected;

        event Action<int, string> PeerDisconnected;

        event Action<int, byte[]> MessageReceived;
    }
}
