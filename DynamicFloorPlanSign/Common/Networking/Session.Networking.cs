using DynamicFloorPlanSign.Common;
using DynamicFloorPlanSign.Common.Networking;
using Generated;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRageMath;

namespace DynamicFloorPlanSign
{
    public partial class DynamicFloorPlanSignSession
    {
        const ushort NETWORK_CHANNEL = 41873;

        NetworkManager _network;

        static readonly string[] SignInputControlIds =
        {
            MyControlsSpace.SECONDARY_TOOL_ACTION.String,
            MyControlsSpace.BUILD_PLANNER.String,
            MyControlsSpace.CUBE_COLOR_CHANGE.String
        };

        bool _localSignInputBlocked;
        long _localSignInputBlockedIdentityId;

        [NetworkCallback(typeof(SignInputBlockState), NetworkCallbackFilter.None)]
        internal static void OnSignInputBlockState(ReceivedPacketEventArgs args)
        {
            if (MyAPIGateway.Multiplayer == null ||
                !MyAPIGateway.Multiplayer.IsServer ||
                args.IsFromServer)
                return;

            SignInputBlockState update = args.UnWrap<SignInputBlockState>();
            if (update == null)
                return;

            ulong senderId = args.SenderId;
            bool enabled = update.Enabled;

            MyAPIGateway.Utilities.InvokeOnGameThread(delegate
            {
                if (MyAPIGateway.Multiplayer == null ||
                    !MyAPIGateway.Multiplayer.IsServer ||
                    MyAPIGateway.Players == null)
                    return;

                long identityId = MyAPIGateway.Players.TryGetIdentityId(senderId);
                if (identityId == 0)
                    return;

                ApplyPlayerSignInputEnabled(identityId, enabled);
            });
        }

        internal void SetLocalSignInputBlocked(bool blocked)
        {
            if (MyAPIGateway.Session == null ||
                MyAPIGateway.Session.LocalHumanPlayer == null)
                return;

            long identityId = MyAPIGateway.Session.LocalHumanPlayer.IdentityId;
            if (_localSignInputBlocked == blocked &&
                (!blocked || _localSignInputBlockedIdentityId == identityId))
                return;

            if (_localSignInputBlocked &&
                _localSignInputBlockedIdentityId != 0 &&
                _localSignInputBlockedIdentityId != identityId)
            {
                ApplyPlayerSignInputEnabled(_localSignInputBlockedIdentityId, true);
            }

            bool enabled = !blocked;
            ApplyPlayerSignInputEnabled(identityId, enabled);

            if (MyAPIGateway.Multiplayer != null &&
                MyAPIGateway.Multiplayer.MultiplayerActive &&
                !MyAPIGateway.Multiplayer.IsServer &&
                _network != null)
            {
                _network.TransmitToServer(new SignInputBlockState
                {
                    Enabled = enabled
                }, sendToAllPlayers: false);
            }

            _localSignInputBlocked = blocked;
            _localSignInputBlockedIdentityId = blocked ? identityId : 0;
        }

        static void ApplyPlayerSignInputEnabled(long identityId, bool enabled)
        {
            if (identityId == 0)
                return;

            for (int i = 0; i < SignInputControlIds.Length; i++)
            {
                MyVisualScriptLogicProvider.SetPlayerInputBlacklistState(
                    SignInputControlIds[i],
                    identityId,
                    enabled);
            }
        }

        [NetworkCallback(typeof(SignTextUpdate), NetworkCallbackFilter.None)]
        internal static void OnSignTextUpdate(ReceivedPacketEventArgs args)
        {
            SignTextUpdate update = args.UnWrap<SignTextUpdate>();
            if (update == null)
                return;

            // null is the explicit "restore vanilla / remove custom text" intent.
            bool restore = update.Text == null;
            if (!restore && update.Text.Length > SignTextRules.MAX_SERIALIZED_LENGTH)
                return;

            DynamicFloorPlanSignSession session = Instance;
            if (session == null)
                return;

            MyAPIGateway.Utilities.InvokeOnGameThread(delegate
            {
                if (Instance != session)
                    return;

                if (!restore)
                {
                    update.Text = SignTextRules.Normalize(update.Text);
                    if (update.Text.Length == 0)
                        return;
                }

                if (MyAPIGateway.Multiplayer.IsServer && !args.IsFromServer)
                    session.ProcessServerRequest(update.GridId, new Vector3I(update.X, update.Y, update.Z),
                        update.Text, args.SenderId);
                else if (!MyAPIGateway.Multiplayer.IsServer && args.IsFromServer)
                    session.QueueSignUpdate(update);
            });
        }
    }
}
