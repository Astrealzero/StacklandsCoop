using System;

namespace StacklandsCoop.Protocol
{
    public enum CmdType : byte
    {
        PickUp = 1,
        DropCard = 2,
        ClickCard = 3,
        ClickPack = 4,
        Pos = 5,
        PackPickUp = 6,
        PackDrop = 7,
        PackPos = 8
    }

    /// <summary>
    /// Client -> host command. One command per message; the host validates against
    /// real world state and the resulting world change comes back as a snapshot.
    /// </summary>
    public struct Command
    {
        public CmdType Type;
        public uint NetId;
        public float X;
        public float Z;

        public static byte[] Encode(CmdType type, uint netId, float x, float z)
        {
            using (var w = new PacketWriter(MsgType.Cmd))
            {
                w.U8((byte)type);
                w.U32(netId);
                w.F32(x);
                w.F32(z);
                return w.ToArray();
            }
        }

        public static Command Parse(byte[] payload)
        {
            using (var r = new PacketReader(payload))
            {
                return new Command
                {
                    Type = (CmdType)r.U8(),
                    NetId = r.U32(),
                    X = r.F32(),
                    Z = r.F32()
                };
            }
        }
    }
}
