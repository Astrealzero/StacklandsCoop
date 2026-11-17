using Steamworks;
using StacklandsCoop.Session;

namespace StacklandsCoop.Net
{
    /// <summary>
    /// Accepts Steam friend invitations even when the player is still in the main menu.
    /// Steam only delivers GameLobbyJoinRequested if somebody registered a callback for it,
    /// so this is registered once at startup instead of waiting for a transport to exist.
    /// </summary>
    public static class SteamInvite
    {
        private static Callback<GameLobbyJoinRequested_t> _callback;

        public static bool Initialized { get; private set; }

        public static bool Available()
        {
            try
            {
                return SteamAPI.IsSteamRunning();
            }
            catch
            {
                return false;
            }
        }

        public static void Initialize()
        {
            if (Initialized || !Available())
            {
                return;
            }

            try
            {
                _callback = Callback<GameLobbyJoinRequested_t>.Create(OnInvite);
                Initialized = true;
                CoopLog.Info("steam invite listener ready");
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("steam invite listener failed: " + ex.Message);
            }
        }

        private static void OnInvite(GameLobbyJoinRequested_t data)
        {
            try
            {
                SessionController session = SessionController.Instance;
                if (session == null)
                {
                    return;
                }

                if (session.Role != SessionRole.Offline)
                {
                    CoopLog.Warn("steam invite ignored: already in a session");
                    UI.StatusBanner.Show("已经在一个房间里，先离开再加入好友", 6f);
                    return;
                }

                string lobbyId = data.m_steamIDLobby.m_SteamID.ToString();
                CoopLog.Info("steam invite accepted lobby=" + lobbyId);
                session.Join("lobby:" + lobbyId, 0, session.Nick, true);
            }
            catch (System.Exception ex)
            {
                CoopLog.Warn("accepting steam invite failed: " + ex.Message);
            }
        }
    }
}
