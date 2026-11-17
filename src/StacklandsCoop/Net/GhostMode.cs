using StacklandsCoop.Session;
using StacklandsCoop.World;
using UnityEngine;

namespace StacklandsCoop.Net
{
    /// <summary>
    /// Client-side "ghost" switch. While a client, the world clock is pinned and
    /// game-side settlement is skipped; the world is driven by snapshots instead.
    /// </summary>
    public static class GhostMode
    {
        /// <summary>Set while the sync layer is deliberately writing the world.</summary>
        public static bool Bypass;

        public static bool IsClient
        {
            get { return SessionController.Instance.Role == SessionRole.Client; }
        }

        public static bool Enabled
        {
            get { return IsClient && !Bypass; }
        }

        public static void LogState(string where)
        {
            CoopLog.Info("ghost[" + where + "] client=" + IsClient + " enabled=" + Enabled);
        }
    }
}
