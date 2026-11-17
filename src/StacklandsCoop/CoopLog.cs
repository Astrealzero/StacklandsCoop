using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StacklandsCoop
{
    /// <summary>
    /// Small in-memory log sink so the co-op panel can show what happened without
    /// the player digging through Player.log.
    /// </summary>
    public static class CoopLog
    {
        private const int MaxLines = 200;
        private static readonly List<string> _lines = new List<string>();

        public static IReadOnlyList<string> Lines
        {
            get { return _lines; }
        }

        public static void Info(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            _lines.Add(line);
            if (_lines.Count > MaxLines)
            {
                _lines.RemoveAt(0);
            }

            Debug.Log("[Coop] " + message);
        }

        public static void Warn(string message)
        {
            Info("!! " + message);
        }

        public static void Error(string message)
        {
            Info("ERR " + message);
            Debug.LogError("[Coop] " + message);
        }

        public static string Dump(int max)
        {
            var sb = new StringBuilder();
            int start = Math.Max(0, _lines.Count - max);
            for (int i = start; i < _lines.Count; i++)
            {
                sb.AppendLine(_lines[i]);
            }

            return sb.ToString();
        }
    }
}
