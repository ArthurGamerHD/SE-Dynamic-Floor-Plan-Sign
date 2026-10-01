using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace DynamicFloorPlanSign
{
    public partial class DynamicFloorPlanSignSession
    {
        const string EDIT_DENIED = "Only a major grid owner or a member of their faction can edit this sign.";

        static bool CanEditSign(MyCubeBlock block, long identityId)
        {
            if (block == null || block.CubeGrid == null || identityId == 0L)
                return false;

            var owners = block.CubeGrid.BigOwners;
            if (owners == null || owners.Count == 0)
                return false;

            for (int i = 0; i < owners.Count; i++)
            {
                if (owners[i] == identityId)
                    return true;
            }

            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Factions == null)
                return false;

            IMyFaction playerFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(identityId);
            if (playerFaction == null)
                return false;

            long playerFactionId = playerFaction.FactionId;
            for (int i = 0; i < owners.Count; i++)
            {
                IMyFaction ownerFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owners[i]);
                if (ownerFaction != null && ownerFaction.FactionId == playerFactionId)
                    return true;
            }

            return false;
        }

        static long GetLocalIdentityId()
        {
            IMyPlayer player = MyAPIGateway.Session != null
                ? MyAPIGateway.Session.LocalHumanPlayer
                : null;
            return player != null ? player.IdentityId : 0L;
        }

        static long ResolveSenderIdentityId(ulong sender)
        {
            if (MyAPIGateway.Players != null && sender != 0UL)
            {
                long identityId = MyAPIGateway.Players.TryGetIdentityId(sender);
                if (identityId != 0L)
                    return identityId;
            }

            // Singleplayer/listen-server requests may be invoked locally.
            if (MyAPIGateway.Multiplayer == null ||
                !MyAPIGateway.Multiplayer.MultiplayerActive ||
                sender == 0UL ||
                sender == MyAPIGateway.Multiplayer.MyId)
                return GetLocalIdentityId();

            return 0L;
        }

        internal static bool CanLocalPlayerEditSign(MyCubeBlock block)
        {
            return CanEditSign(block, GetLocalIdentityId());
        }
    }
}
