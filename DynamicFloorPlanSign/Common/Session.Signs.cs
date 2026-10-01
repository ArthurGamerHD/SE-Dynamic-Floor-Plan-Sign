using System;
using System.Collections.Generic;
using DynamicFloorPlanSign.Common;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace DynamicFloorPlanSign
{
    public partial class DynamicFloorPlanSignSession
    {
        const string FLOOR_PLAN_SUBTYPE_PREFIX = "LargeBlockFloorPlanSign";

        static readonly Guid TextStorageKey = new Guid("4383a2b9-cc56-4f7f-a8b9-8cd9e084f63f");

        static bool IsFloorPlan(IMySlimBlock slim)
        {
            if (slim == null)
                return false;

            MyCubeBlock block = slim.FatBlock as MyCubeBlock;
            if (block == null || block.BlockDefinition == null)
                return false;

            string subtype = block.BlockDefinition.Id.SubtypeName;
            return !string.IsNullOrEmpty(subtype) &&
                   subtype.StartsWith(FLOOR_PLAN_SUBTYPE_PREFIX, StringComparison.Ordinal);
        }

        bool ApplySignTextLocal(long gridId, Vector3I position, string text)
        {
            MyCubeBlock block;
            if (!TryResolveFloorPlan(gridId, position, out block))
                return false;

            if (text == null)
            {
                ClearStoredText(block);
                if (MyAPIGateway.Utilities == null || !MyAPIGateway.Utilities.IsDedicated)
                    ApplyInteractiveVanillaModel(block);
                return true;
            }

            if (text.Length == 0)
                return false;
            
            if (string.Equals(GetCurrentLabel(block), text, StringComparison.Ordinal))
            {
                RestoreBlockModel(block);
                return true;
            }

            string vanillaSubtype;
            if (VanillaSignCatalog.TryGetBestSubtype(text, out vanillaSubtype))
            {
                string vanillaLabel;
                bool exactMatch = VanillaSignCatalog.TryGetLabel(vanillaSubtype, out vanillaLabel) &&
                                  string.Equals(text, vanillaLabel, StringComparison.Ordinal);
                
                // this will try to follow vanilla signs, so removing the mod still leaves a useful sign (PORT HANGAR becomes vanilla HANGAR).
                bool replaced = string.Equals(block.BlockDefinition.Id.SubtypeName, vanillaSubtype, StringComparison.Ordinal);
                if (!replaced)
                    replaced = ReplaceWithVanillaBlock(block, vanillaSubtype);

                if (replaced && exactMatch)
                {
                    if (!TryResolveFloorPlan(gridId, position, out block))
                        return false;
                    ClearStoredText(block);
                    RestoreBlockModel(block);
                    return true;
                }
                
                if (!TryResolveFloorPlan(gridId, position, out block))
                    return false;
            }

            StoreText(block, text);

            if (MyAPIGateway.Utilities == null || !MyAPIGateway.Utilities.IsDedicated)
                ApplyCustomTextModel(block, text);

            return true;
        }

        bool TryResolveFloorPlan(long gridId, Vector3I position, out MyCubeBlock block)
        {
            block = null;
            IMyEntity entity;
            if (MyAPIGateway.Entities == null || !MyAPIGateway.Entities.TryGetEntityById(gridId, out entity))
                return false;

            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid == null)
                return false;

            IMySlimBlock slim = grid.GetCubeBlock(position);
            if (!IsFloorPlan(slim))
                return false;

            block = slim.FatBlock as MyCubeBlock;
            return block != null;
        }

        bool ReplaceWithVanillaBlock(MyCubeBlock block, string targetSubtype)
        {
            if (block == null || block.SlimBlock == null || block.CubeGrid == null)
                return false;

            IMySlimBlock slim = block.SlimBlock;
            IMyCubeGrid grid = block.CubeGrid;
            string oldSubtype = block.BlockDefinition.Id.SubtypeName;
            string oldText;
            bool hadCustomText = TryGetStoredText(block, out oldText);

            ClearStoredText(block);
            MyObjectBuilder_CubeBlock builder = slim.GetObjectBuilder(true);
            builder.SubtypeName = targetSubtype;
            builder.EntityId = 0L;

            block.CubeGrid.RazeBlocksClient(new List<Vector3I> { slim.Min });
            IMySlimBlock replacement = grid.AddBlock(builder, false);
            if (replacement != null)
                return true;
            
            builder.SubtypeName = oldSubtype;
            builder.EntityId = 0L;
            IMySlimBlock restored = grid.AddBlock(builder, false);
            if (restored != null && hadCustomText && restored.FatBlock is MyCubeBlock)
            {
                MyCubeBlock restoredFat = restored.FatBlock as MyCubeBlock;
                StoreText(restoredFat, oldText);
                if (MyAPIGateway.Utilities == null || !MyAPIGateway.Utilities.IsDedicated)
                    ApplyCustomTextModel(restoredFat, oldText);
            }
            return false;
        }

        static void StoreText(MyCubeBlock block, string text)
        {
            if (block == null)
                return;

            if (block.Storage == null)
                block.Storage = new MyModStorageComponent();
            block.Storage.SetValue(TextStorageKey, text);
        }

        static void ClearStoredText(MyCubeBlock block)
        {
            if (block != null && block.Storage != null)
                block.Storage.RemoveValue(TextStorageKey);
        }

        static bool TryGetStoredText(MyCubeBlock block, out string text)
        {
            text = null;
            if (block == null || block.Storage == null || !block.Storage.TryGetValue(TextStorageKey, out text) ||
                string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = SignTextRules.Normalize(text);
            if (!string.Equals(text, normalized, StringComparison.Ordinal) || normalized.Length == 0)
            {
                block.Storage.RemoveValue(TextStorageKey);
                text = null;
                return false;
            }

            text = normalized;
            return true;
        }

        public static string GetCurrentLabel(MyCubeBlock block)
        {
            string text;
            if (TryGetStoredText(block, out text))
                return text;

            if (block != null && block.BlockDefinition != null &&
                VanillaSignCatalog.TryGetLabel(block.BlockDefinition.Id.SubtypeName, out text))
                return text;

            return string.Empty;
        }
    }
}
