using System;

namespace StacklandsCoop.Net
{
    /// <summary>
    /// Length-prefixed framing: [u32 little-endian length][payload]. Used by the
    /// TCP fallback transport; Steam sockets carry the same payloads as messages.
    /// </summary>
    public static class FrameCodec
    {
        public const int HeaderSize = 4;

        public const int MaxPayload = 1 << 20;

        public static byte[] Encode(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payload.Length > MaxPayload)
            {
                throw new ArgumentException("payload too large: " + payload.Length, nameof(payload));
            }

            byte[] frame = new byte[HeaderSize + payload.Length];
            int len = payload.Length;
            frame[0] = (byte)(len & 0xFF);
            frame[1] = (byte)((len >> 8) & 0xFF);
            frame[2] = (byte)((len >> 16) & 0xFF);
            frame[3] = (byte)((len >> 24) & 0xFF);
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            return frame;
        }

        public static int ReadLength(byte[] header, int offset)
        {
            return header[offset]
                   | (header[offset + 1] << 8)
                   | (header[offset + 2] << 16)
                   | (header[offset + 3] << 24);
        }
    }
}
