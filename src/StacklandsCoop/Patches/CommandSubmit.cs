using StacklandsCoop.Protocol;
using StacklandsCoop.Session;
using StacklandsCoop.World;
using UnityEngine;

namespace StacklandsCoop.Patches
{
    /// <summary>
    /// Client intent plumbing: local input becomes a command for the host instead of
    /// a local world change.
    /// </summary>
    internal static class CommandSubmit
    {
        public static void PickUp(GameCard card)
        {
            Send(CmdType.PickUp, card, Vector3.zero);
        }

        public static void Drop(GameCard card, Vector3 position)
        {
            Send(CmdType.DropCard, card, position);
        }

        public static void ClickCard(GameCard card)
        {
            Send(CmdType.ClickCard, card, Vector3.zero);
        }

        public static void ClickPack(Boosterpack pack)
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Client || pack == null)
            {
                return;
            }

            uint netId = World.ClientWorld.Instance.NetIdOfPack(pack);
            CoopLog.Info("submit ClickPack pack=" + pack.BoosterId + " netId=" + netId);
            if (netId == 0u)
            {
                return;
            }

            session.Send(Command.Encode(CmdType.ClickPack, netId, 0f, 0f));
        }

        /// <summary>Live position of the card this client is holding.</summary>
        public static void Move(GameCard card, Vector3 position)
        {
            Send(CmdType.Pos, card, position);
        }

        public static void PackPickUp(Boosterpack pack)
        {
            SendPack(CmdType.PackPickUp, pack, Vector3.zero);
        }

        public static void PackDrop(Boosterpack pack, Vector3 position)
        {
            SendPack(CmdType.PackDrop, pack, position);
        }

        public static void PackMove(Boosterpack pack, Vector3 position)
        {
            SendPack(CmdType.PackPos, pack, position);
        }

        private static void SendPack(CmdType type, Boosterpack pack, Vector3 position)
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Client || pack == null)
            {
                return;
            }

            uint netId = World.ClientWorld.Instance.NetIdOfPack(pack);
            if (netId == 0u)
            {
                return;
            }

            byte[] packet = Command.Encode(type, netId, position.x, position.z);
            if (type == CmdType.PackPos)
            {
                session.SendUnreliable(packet);
            }
            else
            {
                session.Send(packet);
            }
        }

        private static void Send(CmdType type, GameCard card, Vector3 position)
        {
            SessionController session = SessionController.Instance;
            if (session.Role != SessionRole.Client || card == null)
            {
                return;
            }

            uint netId = World.ClientWorld.Instance.NetIdOf(card);
            if (netId == 0u)
            {
                return;
            }

            byte[] packet = Command.Encode(type, netId, position.x, position.z);
            if (type == CmdType.Pos)
            {
                session.SendUnreliable(packet);
            }
            else
            {
                session.Send(packet);
            }
        }
    }
}
