using System;
using System.IO;
using System.Text;

namespace StacklandsCoop.Protocol
{
    /// <summary>
    /// Counterpart of <see cref="PacketWriter"/>. The message type byte is consumed
    /// by the constructor.
    /// </summary>
    public sealed class PacketReader : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;

        public PacketReader(byte[] payload)
        {
            if (payload == null || payload.Length < 1)
            {
                throw new ArgumentException("empty packet", nameof(payload));
            }

            _stream = new MemoryStream(payload, false);
            _reader = new BinaryReader(_stream, Encoding.UTF8, true);
            Type = (MsgType)_reader.ReadByte();
        }

        public MsgType Type { get; private set; }

        public int Remaining
        {
            get { return (int)(_stream.Length - _stream.Position); }
        }

        public byte U8()
        {
            return _reader.ReadByte();
        }

        public ushort U16()
        {
            return _reader.ReadUInt16();
        }

        public uint U32()
        {
            return _reader.ReadUInt32();
        }

        public int I32()
        {
            return _reader.ReadInt32();
        }

        public float F32()
        {
            return _reader.ReadSingle();
        }

        public bool Bool()
        {
            return _reader.ReadByte() != 0;
        }

        public string Str()
        {
            int length = _reader.ReadUInt16();
            if (length == 0)
            {
                return string.Empty;
            }

            byte[] bytes = _reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException("truncated string");
            }

            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }
}
