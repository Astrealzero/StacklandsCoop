using System;
using System.IO;
using System.Text;

namespace StacklandsCoop.Protocol
{
    /// <summary>
    /// Little-endian packet writer. Every packet starts with its 1-byte message type.
    /// Strings are u16 length + UTF-8 bytes.
    /// </summary>
    public sealed class PacketWriter : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryWriter _writer;

        public PacketWriter(MsgType type)
        {
            _stream = new MemoryStream(128);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, true);
            _writer.Write((byte)type);
        }

        public PacketWriter U8(byte value)
        {
            _writer.Write(value);
            return this;
        }

        public PacketWriter U16(ushort value)
        {
            _writer.Write(value);
            return this;
        }

        public PacketWriter U32(uint value)
        {
            _writer.Write(value);
            return this;
        }

        public PacketWriter I32(int value)
        {
            _writer.Write(value);
            return this;
        }

        public PacketWriter F32(float value)
        {
            _writer.Write(value);
            return this;
        }

        public PacketWriter Bool(bool value)
        {
            _writer.Write((byte)(value ? 1 : 0));
            return this;
        }

        public PacketWriter Str(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                _writer.Write((ushort)0);
                return this;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("string too long: " + bytes.Length, nameof(value));
            }

            _writer.Write((ushort)bytes.Length);
            _writer.Write(bytes);
            return this;
        }

        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
