using System;
using DynamicFloorPlanSign.Client.UI;
using DynamicFloorPlanSign.Common;
using DynamicFloorPlanSign.Common.Networking;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace DynamicFloorPlanSign
{
    // todo: remove partial class and split to dedicated editor class
    public partial class DynamicFloorPlanSignSession
    {
        // I could use ADK command for this, but why bother?
        const string COMMAND = "/sign";

        const double RAY_LENGTH = 20.0;
        
        const string RAYCAST_FAIL = "Look at a FloorPlan sign to edit it.";

        internal static void Notify(string text, string font = MyFontEnum.Green)
        {
            if (MyAPIGateway.Utilities != null && !string.IsNullOrWhiteSpace(text))
                MyAPIGateway.Utilities.ShowNotification(text, 1500, font);
        }

        internal static void OpenEditorFromUseObject(MyCubeBlock block, IMyEntity user)
        {
            if (Instance == null || block == null || user == null || MyAPIGateway.Session == null)
                return;

            IMyPlayer localPlayer = MyAPIGateway.Session.LocalHumanPlayer;
            if (localPlayer == null || localPlayer.Character == null || localPlayer.Character.EntityId != user.EntityId)
                return;

            Instance.OpenEditor(block);
        }

        void OpenEditor(MyCubeBlock block)
        {
            if (!IsFloorPlan(block?.SlimBlock))
                return;

            if (!CanLocalPlayerEditSign(block))
            {
                Notify(EDIT_DENIED, MyFontEnum.Red);
                return;
            }

            long gridId = block.CubeGrid.EntityId;
            Vector3I position = block.Position;
            string initialText = GetCurrentLabel(block);

            TextInputHelper.SpawnForLocalPlayer(
                "Floor Plan Sign editor",
                delegate(string entered) { SubmitText(gridId, position, entered); },
                initialText,
                "A-Z, spaces, | / \\ + - = _ < >; max " + SignTextRules.MAX_LINE_LENGTH + " chars/line, 2 lines");
        }

        void OnMessageEntered(string messageText, ref bool sendToOthers)
        {
            if (messageText == null)
                return;

            string trimmed = messageText.Trim();
            if (!trimmed.StartsWith(COMMAND, StringComparison.OrdinalIgnoreCase))
                return;

            if (trimmed.Length > COMMAND.Length && !char.IsWhiteSpace(trimmed[COMMAND.Length]))
                return;

            sendToOthers = false;

            MyCubeBlock block;
            if (!TryGetLookedAtFloorPlan(out block))
                return;

            string text = trimmed.Length > COMMAND.Length
                ? trimmed.Substring(COMMAND.Length).Trim()
                : string.Empty;

            if (text.Length == 0)
            {
                OpenEditor(block);
                return;
            }

            SubmitText(block.CubeGrid.EntityId, block.Position, text);
        }

        public void SubmitText(long gridId, Vector3I position, string rawText)
        {
            string normalized = SignTextRules.Normalize(rawText);
            if (normalized.Length == 0)
            {
                Notify("Sign text is empty after filtering.");
                return;
            }

            MyCubeBlock currentBlock;
            if (!TryResolveFloorPlan(gridId, position, out currentBlock))
                return;

            if (!CanLocalPlayerEditSign(currentBlock))
            {
                Notify(EDIT_DENIED, MyFontEnum.Red);
                return;
            }

            if (string.Equals(GetCurrentLabel(currentBlock), normalized, StringComparison.Ordinal))
                return;

            if (MyAPIGateway.Multiplayer != null && MyAPIGateway.Multiplayer.MultiplayerActive &&
                !MyAPIGateway.Multiplayer.IsServer)
            {
                if (_network != null)
                    _network.TransmitToServer(new SignTextUpdate
                    {
                        GridId = gridId,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                        Text = normalized
                    }, sendToAllPlayers: false);
                return;
            }

            ProcessServerRequest(gridId, position, normalized, MyAPIGateway.Multiplayer != null ? MyAPIGateway.Multiplayer.MyId : 0UL);
        }

        bool TryGetLookedAtFloorPlan(out MyCubeBlock fat)
        {
            fat = null;
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Camera == null)
            {
                Notify("No camera available.", MyFontEnum.Red);
                return false;
            }

            Vector3D from = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            Vector3D to = from + MyAPIGateway.Session.Camera.WorldMatrix.Forward * RAY_LENGTH;
            IHitInfo hit;
            if (!MyAPIGateway.Physics.CastRay(from, to, out hit) || hit == null)
            {
                Notify(RAYCAST_FAIL, MyFontEnum.Red);
                return false;
            }

            IMyCubeGrid grid = hit.HitEntity as IMyCubeGrid;
            if (grid == null)
            {
                Notify(RAYCAST_FAIL, MyFontEnum.Red);
                return false;
            }

            Vector3I? cell = grid.RayCastBlocks(from, to);
            if (!cell.HasValue)
            {
                Notify(RAYCAST_FAIL, MyFontEnum.Red);
                return false;
            }

            IMySlimBlock slim = grid.GetCubeBlock(cell.Value);
            if (!IsFloorPlan(slim))
            {
                Notify(RAYCAST_FAIL, MyFontEnum.Red);
                return false;
            }

            fat = slim.FatBlock as MyCubeBlock;
            return fat != null;
        }

        void NotifyIfLocalSender(ulong sender, string message)
        {
            if (MyAPIGateway.Multiplayer == null || !MyAPIGateway.Multiplayer.MultiplayerActive ||
                sender == MyAPIGateway.Multiplayer.MyId)
                Notify(message);
        }
    }
}
