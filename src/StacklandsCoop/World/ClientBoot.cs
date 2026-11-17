using System.Collections.Generic;
using StacklandsCoop.Net;
using StacklandsCoop.Session;

namespace StacklandsCoop.World
{
    /// <summary>
    /// A client has no world of its own: the host owns the simulation. This helper
    /// boots an empty local round so the snapshot renderer has cards, boards and a
    /// camera to work with, then the first full snapshot replaces its contents.
    /// The round is created with GhostMode bypassed because the boot itself writes
    /// world state; afterwards the client goes back to ghost mode.
    /// </summary>
    public static class ClientBoot
    {
        private static bool _attempted;

        public static void Reset()
        {
            _attempted = false;
        }

        /// <summary>Called every frame by the runtime; boots at most once per session.</summary>
        public static void Tick()
        {
            if (_attempted || SessionController.Instance.Role != SessionRole.Client)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            if (wm.IsPlaying)
            {
                _attempted = true;
                return;
            }

            _attempted = true;
            TryEnterWorld(wm);
        }

        private static void TryEnterWorld(WorldManager wm)
        {
            bool previous = GhostMode.Bypass;
            GhostMode.Bypass = true;
            try
            {
                wm.CurrentRunOptions = new RunOptions
                {
                    MoonLength = MoonLength.Normal,
                    IsPeacefulMode = false,
                    IsGreedEnabled = false,
                    IsHappinessEnabled = false,
                    IsDeathEnabled = false
                };
                wm.CurrentRunVariables = new RunVariables();
                wm.RoundExtraKeyValues = new List<SerializedKeyValuePair>();
                wm.StartNewRound();
                wm.Play();
                CoopLog.Info("客户端已进入空白世界，等待主机快照");
                SessionController.Instance.Send(Protocol.Messages.SyncRequest());
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("客户端自动进入世界失败: " + ex.Message);
            }
            finally
            {
                GhostMode.Bypass = previous;
            }
        }
    }
}
