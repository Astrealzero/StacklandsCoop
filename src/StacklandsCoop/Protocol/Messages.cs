using System;

namespace StacklandsCoop.Protocol
{
    /// <summary>Wire message ids (see docs/DESIGN.md §3).</summary>
    public enum MsgType : byte
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        SyncStart = 4,
        SyncChunk = 5,
        SyncEnd = 6,
        Cmd = 7,
        CmdResult = 8,
        Event = 9,
        Revision = 10,
        Pos = 11,
        Cursor = 12,
        Ready = 13,
        ForceMonth = 14,
        Pause = 15,
        TimerStart = 16,
        TimerStop = 17,
        Hit = 18,
        SyncRequest = 19,
        Attack = 20,
        Ping = 21,
        Pong = 22,
        PackPos = 23,
        PlayerBoard = 24,
        Chat = 99
    }

    public static class ProtocolInfo
    {
        /// <summary>
        /// 4: shop records carry the host's rendered price text (the counters alone were not
        /// enough to make the client's label follow), motion packets travel unreliable.
        /// 3: the snapshot header grew shop counters, equipment/worker slots and conflict groups
        /// (0.3.1-0.3.3), and the follow-host board model replaced per-player boards.
        /// 2: Pause carries the host speed (0.2.0 payloads had only the paused flag), cursors carry
        /// a board id, and PlayerBoard was added. Peers on different versions are rejected on
        /// purpose instead of silently mis-parsing each other.
        /// 5: shop records carry the host-rendered label plus the shop position, so standalone
        /// shop targets (no card, no netId) can still be matched.
        /// </summary>
        public const int Version = 5;

        /// <summary>Placeholder until the world layer hashes the loaded card tables.</summary>
        public const string DevDefsHash = "dev-defs";

        /// <summary>Placeholder until the handshake hashes the loaded mod list.</summary>
        public const string DevModsHash = "dev-mods";
    }

    public static class RejectCode
    {
        public const byte VersionMismatch = 1;
        public const byte DefsMismatch = 2;
        public const byte ModsMismatch = 3;
        public const byte LobbyFull = 4;
        public const byte DuplicatePlayer = 5;
    }

    public struct HelloInfo
    {
        public int Protocol;
        public string ModVersion;
        public string DefsHash;
        public string ModsHash;
        public string Nick;
    }

    public struct WelcomeInfo
    {
        public int PlayerId;
        public string SessionToken;
        public string HostName;
        public int Epoch;
    }

    public struct RejectInfo
    {
        public byte Code;
        public string Text;
    }

    /// <summary>Handshake, command and event packets. Snapshot framing lives in World/Snapshot.cs.</summary>
    public static class Messages
    {
        public static byte[] Hello(string nick, string modVersion, string defsHash, string modsHash)
        {
            using (var w = new PacketWriter(MsgType.Hello))
            {
                w.I32(ProtocolInfo.Version);
                w.Str(modVersion);
                w.Str(defsHash);
                w.Str(modsHash);
                w.Str(nick);
                return w.ToArray();
            }
        }

        public static HelloInfo ParseHello(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Hello)
                {
                    throw new InvalidOperationException("not a Hello packet: " + r.Type);
                }

                return new HelloInfo
                {
                    Protocol = r.I32(),
                    ModVersion = r.Str(),
                    DefsHash = r.Str(),
                    ModsHash = r.Str(),
                    Nick = r.Str()
                };
            }
        }

        public static byte[] Welcome(int playerId, string sessionToken, string hostName, int epoch)
        {
            using (var w = new PacketWriter(MsgType.Welcome))
            {
                w.I32(playerId);
                w.Str(sessionToken);
                w.Str(hostName);
                w.I32(epoch);
                return w.ToArray();
            }
        }

        public static WelcomeInfo ParseWelcome(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Welcome)
                {
                    throw new InvalidOperationException("not a Welcome packet: " + r.Type);
                }

                return new WelcomeInfo
                {
                    PlayerId = r.I32(),
                    SessionToken = r.Str(),
                    HostName = r.Str(),
                    Epoch = r.I32()
                };
            }
        }

        public static byte[] Reject(byte code, string text)
        {
            using (var w = new PacketWriter(MsgType.Reject))
            {
                w.U8(code);
                w.Str(text);
                return w.ToArray();
            }
        }

        public static RejectInfo ParseReject(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Reject)
                {
                    throw new InvalidOperationException("not a Reject packet: " + r.Type);
                }

                return new RejectInfo { Code = r.U8(), Text = r.Str() };
            }
        }

        /// <summary>Free-form notice rendered in the co-op panel. Milestone 1 only.</summary>
        public static byte[] Notice(string text)
        {
            using (var w = new PacketWriter(MsgType.Event))
            {
                w.U8(0);
                w.Str(text);
                return w.ToArray();
            }
        }

        public static string ParseNotice(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Event)
                {
                    throw new InvalidOperationException("not an Event packet: " + r.Type);
                }

                r.U8();
                return r.Str();
            }
        }

        /// <summary>Live position of a booster pack somebody is holding (host -> everyone).</summary>
        public static byte[] PackPos(uint netId, float x, float z)
        {
            using (var w = new PacketWriter(MsgType.PackPos))
            {
                w.U32(netId);
                w.F32(x);
                w.F32(z);
                return w.ToArray();
            }
        }

        public static void ParsePackPos(byte[] payload, out uint netId, out float x, out float z)
        {
            using (var r = new PacketReader(payload))
            {
                netId = r.U32();
                x = r.F32();
                z = r.F32();
            }
        }

        /// <summary>Live position of a card somebody is holding (host -> everyone).</summary>
        public static byte[] CardPos(uint netId, float x, float z)
        {
            using (var w = new PacketWriter(MsgType.Pos))
            {
                w.U32(netId);
                w.F32(x);
                w.F32(z);
                return w.ToArray();
            }
        }

        /// <summary>A card started its timer on the host (host -> clients).</summary>
        public static byte[] TimerStart(uint netId, float target)
        {
            using (var w = new PacketWriter(MsgType.TimerStart))
            {
                w.U32(netId);
                w.F32(target);
                return w.ToArray();
            }
        }

        public static void ParseTimerStart(byte[] payload, out uint netId, out float target)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.TimerStart)
                {
                    throw new InvalidOperationException("not a TimerStart packet: " + r.Type);
                }

                netId = r.U32();
                target = r.F32();
            }
        }

        /// <summary>A card's timer finished or was cancelled on the host.</summary>
        public static byte[] TimerStop(uint netId)
        {
            using (var w = new PacketWriter(MsgType.TimerStop))
            {
                w.U32(netId);
                return w.ToArray();
            }
        }

        public static uint ParseTimerStop(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.TimerStop)
                {
                    throw new InvalidOperationException("not a TimerStop packet: " + r.Type);
                }

                return r.U32();
            }
        }

        public static void ParseCardPos(byte[] payload, out uint netId, out float x, out float z)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Pos)
                {
                    throw new InvalidOperationException("not a Pos packet: " + r.Type);
                }

                netId = r.U32();
                x = r.F32();
                z = r.F32();
            }
        }

        /// <summary>Client -> host: "I am ready to end the month".</summary>
        public static byte[] Ready(bool ready)
        {
            using (var w = new PacketWriter(MsgType.Ready))
            {
                w.U8(ready ? (byte)1 : (byte)0);
                return w.ToArray();
            }
        }

        public static bool ParseReady(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Ready)
                {
                    throw new InvalidOperationException("not a Ready packet: " + r.Type);
                }

                return r.U8() != 0;
            }
        }

        /// <summary>Host -> everyone: the host paused / resumed the shared world clock.</summary>
        public static byte[] Pause(bool paused, float speedUp)
        {
            using (var w = new PacketWriter(MsgType.Pause))
            {
                w.U8(paused ? (byte)1 : (byte)0);
                w.F32(speedUp);
                return w.ToArray();
            }
        }

        public static bool ParsePause(byte[] payload, out float speedUp)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Pause)
                {
                    throw new InvalidOperationException("not a Pause packet: " + r.Type);
                }

                bool paused = r.U8() != 0;
                speedUp = r.F32();
                return paused;
            }
        }

        /// <summary>
        /// Host -> clients: one combat hit landed. Clients only replay the floating
        /// damage text; the actual damage is already applied on the host.
        /// </summary>
        public static byte[] Hit(uint netId, ushort damage, bool veryEffective)
        {
            using (var w = new PacketWriter(MsgType.Hit))
            {
                w.U32(netId);
                w.U16(damage);
                w.U8(veryEffective ? (byte)1 : (byte)0);
                return w.ToArray();
            }
        }

        public static void ParseHit(byte[] payload, out uint netId, out ushort damage, out bool veryEffective)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Hit)
                {
                    throw new InvalidOperationException("not a Hit packet: " + r.Type);
                }

                netId = r.U32();
                damage = r.U16();
                veryEffective = r.U8() != 0;
            }
        }

        /// <summary>Client -> host: "my world is up, send a full snapshot now".</summary>
        public static byte[] SyncRequest()
        {
            using (var w = new PacketWriter(MsgType.SyncRequest))
            {
                return w.ToArray();
            }
        }

        /// <summary>Client -> host: keep-alive probe carrying a sequence number.</summary>
        public static byte[] Ping(int seq)
        {
            using (var w = new PacketWriter(MsgType.Ping))
            {
                w.I32(seq);
                return w.ToArray();
            }
        }

        public static int ParsePing(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                return r.I32();
            }
        }

        /// <summary>Host -> client: echo of the probe, used to measure round-trip time.</summary>
        public static byte[] Pong(int seq)
        {
            using (var w = new PacketWriter(MsgType.Pong))
            {
                w.I32(seq);
                return w.ToArray();
            }
        }

        public static int ParsePong(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                return r.I32();
            }
        }

        /// <summary>Host -> clients: attacker lunged at victim (cosmetic replay only).</summary>
        public static byte[] Attack(uint attacker, uint target)
        {
            using (var w = new PacketWriter(MsgType.Attack))
            {
                w.U32(attacker);
                w.U32(target);
                return w.ToArray();
            }
        }

        public static void ParseAttack(byte[] payload, out uint attacker, out uint target)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Attack)
                {
                    throw new InvalidOperationException("not an Attack packet: " + r.Type);
                }

                attacker = r.U32();
                target = r.U32();
            }
        }

        /// <summary>Any peer -> host -> everyone: a player's mouse position on a board.</summary>
        public static byte[] Cursor(int playerId, float x, float z, string board)
        {
            using (var w = new PacketWriter(MsgType.Cursor))
            {
                w.I32(playerId);
                w.F32(x);
                w.F32(z);
                w.Str(board ?? string.Empty);
                return w.ToArray();
            }
        }

        public static void ParseCursor(byte[] payload, out int playerId, out float x, out float z, out string board)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Cursor)
                {
                    throw new InvalidOperationException("not a Cursor packet: " + r.Type);
                }

                playerId = r.I32();
                x = r.F32();
                z = r.F32();
                board = r.Str();
            }
        }

        /// <summary>Any peer -> host -> everyone: a one-line text chat message.</summary>
        public static byte[] Chat(int playerId, string text)
        {
            using (var w = new PacketWriter(MsgType.Chat))
            {
                w.I32(playerId);
                w.Str(text ?? string.Empty);
                return w.ToArray();
            }
        }

        public static void ParseChat(byte[] payload, out int playerId, out string text)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.Chat)
                {
                    throw new InvalidOperationException("not a Chat packet: " + r.Type);
                }

                playerId = r.I32();
                text = r.Str();
            }
        }

        /// <summary>Peer -> host -> everyone: which board that player is currently looking at.</summary>
        public static byte[] PlayerBoard(int playerId, string board)
        {
            using (var w = new PacketWriter(MsgType.PlayerBoard))
            {
                w.I32(playerId);
                w.Str(board ?? string.Empty);
                return w.ToArray();
            }
        }

        public static void ParsePlayerBoard(byte[] payload, out int playerId, out string board)
        {
            using (var r = new PacketReader(payload))
            {
                if (r.Type != MsgType.PlayerBoard)
                {
                    throw new InvalidOperationException("not a PlayerBoard packet: " + r.Type);
                }

                playerId = r.I32();
                board = r.Str();
            }
        }
    }
}
