using System;
using System.Collections.Generic;
using Adk.Utils;
using DynamicFloorPlanSign.Client.Rendering;
using DynamicFloorPlanSign.Common;
using DynamicFloorPlanSign.Common.Networking;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace DynamicFloorPlanSign
{
    
    // todo: remove partial class and split to dedicated render class
    public partial class DynamicFloorPlanSignSession
    {
        readonly HashSet<long> _applying = new HashSet<long>();

        void RestoreTrackedModels()
        {
            if (MyAPIGateway.Utilities != null && MyAPIGateway.Utilities.IsDedicated)
                return;

            List<MyCubeBlock> blocks = new List<MyCubeBlock>(_tracked.Values);
            foreach (var block in blocks)
                RestoreBlockModel(block);
        }

        public void RestoreBlockModel(MyCubeBlock block)
        {
            if (block == null || block.MarkedForClose || !IsFloorPlan(block.SlimBlock))
                return;

            if (MyAPIGateway.Utilities != null && MyAPIGateway.Utilities.IsDedicated)
                return;

            string customText;
            if (TryGetStoredText(block, out customText))
                ApplyCustomTextModel(block, customText);
            else
                ApplyInteractiveVanillaModel(block);
        }
        
        
        public void RestoreVanillaModel(MyCubeBlock block, bool sync = true)
        {
            if (block == null || block.BlockDefinition == null || block.CubeGrid == null)
                return;

            string text;
            if (!VanillaSignCatalog.TryGetLabel(block.BlockDefinition.Id.SubtypeName, out text))
            {
                Notify("Unable to find vanilla text", MyFontEnum.Red);
                return;
            }

            long gridId = block.CubeGrid.EntityId;
            Vector3I position = block.Position;

            if (sync && MyAPIGateway.Multiplayer != null && MyAPIGateway.Multiplayer.MultiplayerActive)
            {
                if (!MyAPIGateway.Multiplayer.IsServer)
                {
                    if (_network != null)
                        _network.TransmitToServer(new SignTextUpdate
                        {
                            GridId = gridId,
                            X = position.X,
                            Y = position.Y,
                            Z = position.Z,
                            Text = null
                        }, sendToAllPlayers: false);
                    return;
                }

                // Listen server/host goes through the same permission and broadcast path.
                ProcessServerRequest(gridId, position, null, MyAPIGateway.Multiplayer.MyId);
                return;
            }

            ClearStoredText(block);
            if (MyAPIGateway.Utilities == null || !MyAPIGateway.Utilities.IsDedicated)
                ApplyInteractiveVanillaModel(block);
            Notify("Restored vanilla model for " + text);
        }

        void OnBlockModelChange(MyCubeBlock block)
        {
            if (!_startupReady || block == null || _applying.Contains(block.EntityId) || !IsFloorPlan(block.SlimBlock))
                return;

            RestoreBlockModel(block);
        }

        void ApplyCustomTextModel(MyCubeBlock block, string text)
        {
            if (block == null || string.IsNullOrEmpty(text))
                return;

            Matrix orientation;
            string vanillaModel = block.CalculateCurrentModel(out orientation);
            if (string.IsNullOrWhiteSpace(vanillaModel))
                return;

            string renderModel = RuntimeMwmBuilder.BuildModel(text, typeof(DynamicFloorPlanSignSession),
                () => RestoreBlockModel(block));
            if (renderModel == null)
                return;

            ApplyRenderModelKeepingVanillaState(block, renderModel, vanillaModel);
        }

        void ApplyInteractiveVanillaModel(MyCubeBlock block)
        {
            if (block == null)
                return;

            Matrix orientation;
            string vanillaModel = block.CalculateCurrentModel(out orientation);
            if (string.IsNullOrWhiteSpace(vanillaModel))
                return;

            string renderModel = RuntimeMwmBuilder.BuildDetectorModel(vanillaModel, typeof(DynamicFloorPlanSignSession));
            ApplyRenderModelKeepingVanillaState(block, renderModel, vanillaModel);
        }

        /// <summary>
        /// Tries to apply a model without f* up the vanilla underlying model
        /// </summary>
        void ApplyRenderModelKeepingVanillaState(MyCubeBlock block, string renderModel, string vanillaModel)
        {
            if (block == null || string.IsNullOrWhiteSpace(renderModel) || string.IsNullOrWhiteSpace(vanillaModel))
                return;

            long id = block.EntityId;
            if (!_applying.Add(id))
                return;

            try
            {
                // 1) Select generated model.
                block.RefreshModels(renderModel, null);

                // 2) Rebuild GPU/render state from generated model.
                block.Render.RemoveRenderObjects();
                block.Render.AddRenderObjects();

                // Detector/highlight data resolves model sections through the block's active
                // CPU-side model, so keep the generated model active after loading detectors.
                // The model path is not serialized by MyCubeBlock.GetObjectBuilderCubeBlock().
                ((IMyCubeBlock)block).ReloadDetectors(false);
            }
            catch (Exception e)
            {
                LogHelper.Log(MyLogSeverity.Error, "model apply failed: " + e);

                // Best effort rollback if generated model application itself fails.
                try
                {
                    if (!string.IsNullOrWhiteSpace(vanillaModel))
                    {
                        block.RefreshModels(vanillaModel, null);
                        block.Render.RemoveRenderObjects();
                        block.Render.AddRenderObjects();
                        ((IMyCubeBlock)block).ReloadDetectors(false);
                    }
                }
                catch
                {
                    LogHelper.Log(MyLogSeverity.Error, "model rollback failed");
                }
            }
            finally
            {
                _applying.Remove(id);
            }
        }
    }
}
