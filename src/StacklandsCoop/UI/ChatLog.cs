using System;
using System.Collections.Generic;

namespace StacklandsCoop.UI
{
    /// <summary>
    /// One-line chat transcript shown in the co-op panel. Chat is relayed by the
    /// host so every peer sees the same order; it is purely cosmetic and never
    /// touches the world.
    /// </summary>
    public static class ChatLog
    {
        private const int MaxLines = 50;
        private static readonly List<string> _lines = new List<string>();

        public static IReadOnlyList<string> Lines
        {
            get { return _lines; }
        }

        public static void Add(int playerId, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _lines.Add("[" + DateTime.Now.ToString("HH:mm") + "] P" + playerId + ": " + text);
            if (_lines.Count > MaxLines)
            {
                _lines.RemoveAt(0);
            }
        }

        public static void Clear()
        {
            _lines.Clear();
        }
    }
}
