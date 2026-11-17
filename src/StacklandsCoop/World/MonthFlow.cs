using System.Collections.Generic;
using StacklandsCoop.Protocol;
using StacklandsCoop.Session;
using UnityEngine;

namespace StacklandsCoop.World
{
    /// <summary>
    /// Month flow for milestone 4: clients flag themselves ready, and once everyone is
    /// ready the host forces the end of the month. Also carries the host's manual pause,
    /// which freezes the shared world clock by driving WorldManager.SpeedUp to zero.
    /// </summary>
    public sealed class MonthFlow
    {
        public static readonly MonthFlow Instance = new MonthFlow();

        private readonly HashSet<int> _ready = new HashSet<int>();
        private float _savedSpeedUp = 1f;

        public bool LocalReady { get; private set; }
        public bool HostPaused { get; private set; }

        /// <summary>Hosts only: how many remote players flagged ready.</summary>
        public int ReadyCount
        {
            get { return _ready.Count; }
        }

        public int RemoteCount
        {
            get { return SessionController.Instance.RemotePeerCount; }
        }

        /// <summary>Hosts only: which remote players already flagged ready (for the panel list).</summary>
        public bool IsReady(int peerId)
        {
            return _ready.Contains(peerId);
        }

        public void ToggleLocalReady()
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Client)
            {
                return;
            }

            LocalReady = !LocalReady;
            session.Send(Messages.Ready(LocalReady));
            CoopLog.Info("ready = " + LocalReady);
        }

        public void ClearLocalReady()
        {
            LocalReady = false;
        }

        /// <summary>Host: a client toggled its ready flag.</summary>
        public void OnReady(int peer, bool ready)
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Host)
            {
                return;
            }

            if (ready)
            {
                _ready.Add(peer);
            }
            else
            {
                _ready.Remove(peer);
            }

            CoopLog.Info("ready peer=" + peer + " -> " + (ready ? "yes" : "no") +
                          " (" + _ready.Count + "/" + session.RemotePeerCount + ")");

            if (session.RemotePeerCount > 0 && _ready.Count >= session.RemotePeerCount)
            {
                ForceMonthEnd("all ready");
            }
        }

        public void OnPeerLeft(int peer)
        {
            _ready.Remove(peer);
        }

        /// <summary>Host only: jump to the end of the month right now.</summary>
        public void ForceMonthEnd(string reason)
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Host)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null || !wm.IsPlaying)
            {
                CoopLog.Warn("force month end skipped (" + reason + ") state=" +
                             (wm == null ? "no world" : wm.CurrentGameState.ToString()));                return;
            }

            CoopLog.Info("force month end: " + reason);
            _ready.Clear();
            try
            {
                wm.ForceEndOfMoon(new EndOfMonthParameters());
            }
            catch (System.Exception ex)
            {
                CoopLog.Error("ForceEndOfMoon failed: " + ex);
            }
        }

        /// <summary>Host: freeze / resume the shared clock without opening the pause menu.</summary>
        public void ToggleHostPause()
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Host)
            {
                return;
            }

            WorldManager wm = WorldManager.instance;
            if (wm == null)
            {
                return;
            }

            HostPaused = !HostPaused;
            if (HostPaused)
            {
                _savedSpeedUp = wm.SpeedUp;
                wm.SpeedUp = 0f;
            }
            else
            {
                wm.SpeedUp = _savedSpeedUp <= 0f ? 1f : _savedSpeedUp;
            }

            // Carry the speed so the client can resume timer bars at the right rate even when
            // no snapshot happens to arrive right after the resume.
            session.Broadcast(Messages.Pause(HostPaused, HostPaused ? 0f : wm.SpeedUp));
            CoopLog.Info("host pause = " + HostPaused);
        }

        /// <summary>Client: the host paused or resumed.</summary>
        public void OnHostPause(bool paused, float speedUp)
        {
            if (SessionController.Instance.Role == SessionRole.Client)
            {
                HostPaused = paused;
                ClientWorld.Instance.SetHostSpeedUp(speedUp);
            }
        }

        /// <summary>Reset per-session state when leaving a room.</summary>
        public void Reset()
        {
            _ready.Clear();
            LocalReady = false;
            HostPaused = false;
        }
    }
}
