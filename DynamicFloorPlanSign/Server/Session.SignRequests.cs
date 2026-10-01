using System.Collections.Generic;
using DynamicFloorPlanSign.Common.Networking;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;


namespace DynamicFloorPlanSign
{
    // todo: remove partial class and split server from client session
    public partial class DynamicFloorPlanSignSession
    {
        void ProcessServerRequest(long gridId, Vector3I position, string text, ulong sender)
        {
            MyCubeBlock block;
            if (!TryResolveFloorPlan(gridId, position, out block))
                return;

            long identityId = ResolveSenderIdentityId(sender);
            if (!CanEditSign(block, identityId))
            {
                NotifyIfLocalSender(sender, EDIT_DENIED);
                return;
            }

            if (!ApplySignTextLocal(gridId, position, text))
                return;

            if (_network != null && MyAPIGateway.Multiplayer.MultiplayerActive && MyAPIGateway.Multiplayer.IsServer)
            {
                SignTextUpdate update = new SignTextUpdate
                {
                    GridId = gridId,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    Text = text
                };
                // Relay accepted intent to every remote player, including the requester.
                List<IMyPlayer> players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);
                for (int i = 0; i < players.Count; i++)
                {
                    IMyPlayer player = players[i];
                    if (!player.IsBot && player.SteamUserId != MyAPIGateway.Multiplayer.ServerId)
                        _network.TransmitToPlayer(update, player.SteamUserId);
                }
            }

            NotifyIfLocalSender(sender,
                text == null ? "Restored original sign." : "Sign changed to " + text + ".");
        }
    }
}
