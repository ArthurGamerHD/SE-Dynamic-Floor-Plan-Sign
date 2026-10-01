using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace DynamicFloorPlanSign
{
    public partial class DynamicFloorPlanSignSession
    {
        readonly Dictionary<long, MyCubeBlock> _tracked = new Dictionary<long, MyCubeBlock>();

        readonly Dictionary<long, IMyCubeGrid> _trackedGrids = new Dictionary<long, IMyCubeGrid>();

        void OnEntityAdd(IMyEntity entity)
        {
            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid != null)
            {
                // Subscribe immediately so we don't miss blocks added during the same tick,
                // but defer all scanning/model manipulation until the next simulation tick.
                TrackGridEvents(grid);
                QueueNextFrame(delegate { ScanGridDeferred(grid); });
                return;
            }

            MyCubeBlock block = entity as MyCubeBlock;
            if (block != null)
                QueueNextFrame(delegate { SetupFloorPlanDeferred(block); });
        }

        void OnEntityRemove(IMyEntity entity)
        {
            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid != null)
            {
                IMyCubeGrid trackedGrid;
                if (_trackedGrids.TryGetValue(grid.EntityId, out trackedGrid))
                {
                    trackedGrid.OnBlockAdded -= OnBlockAdded;
                    trackedGrid.OnBlockRemoved -= OnBlockRemoved;
                    _trackedGrids.Remove(grid.EntityId);
                }
                return;
            }

            MyCubeBlock block = entity as MyCubeBlock;
            if (block != null && _tracked.Remove(block.EntityId))
            {
                block.OnBlockModelChange -= OnBlockModelChange;
                _applying.Remove(block.EntityId);
            }
        }

        void RestoreExistingEntities()
        {
            HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, delegate(IMyEntity e) { return e is IMyCubeGrid; });
            foreach (IMyEntity entity in entities)
            {
                IMyCubeGrid grid = entity as IMyCubeGrid;
                if (grid == null)
                    continue;

                TrackGridEvents(grid);
                QueueNextFrame(delegate { ScanGridDeferred(grid); });
            }
        }

        void TrackGridEvents(IMyCubeGrid grid)
        {
            if (grid == null || _trackedGrids.ContainsKey(grid.EntityId))
                return;

            _trackedGrids[grid.EntityId] = grid;
            grid.OnBlockAdded += OnBlockAdded;
            grid.OnBlockRemoved += OnBlockRemoved;
        }

        void ScanGridDeferred(IMyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose)
                return;

            TrackGridEvents(grid);

            List<IMySlimBlock> blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks, IsFloorPlan);
            for (int i = 0; i < blocks.Count; i++)
            {
                MyCubeBlock fat = blocks[i].FatBlock as MyCubeBlock;
                if (fat != null)
                    SetupFloorPlan(fat);
            }
        }

        void SetupFloorPlanDeferred(MyCubeBlock block)
        {
            if (block == null || block.MarkedForClose || block.CubeGrid == null)
                return;

            if (block.CubeGrid == null)
                return;

            SetupFloorPlan(block);
        }

        void OnBlockAdded(IMySlimBlock slim)
        {
            if (slim == null)
                return;

            // FatBlock/render state can still be incomplete during the block-added callback
            // (notably after creative paste), so only resolve/process it next tick.
            QueueNextFrame(delegate
            {
                if (!IsFloorPlan(slim))
                    return;

                MyCubeBlock fat = slim.FatBlock as MyCubeBlock;
                if (fat != null)
                    SetupFloorPlanDeferred(fat);
                else if (slim.CubeGrid != null)
                    ScanGridDeferred(slim.CubeGrid);
            });
        }

        void OnBlockRemoved(IMySlimBlock slim)
        {
            MyCubeBlock fat = slim?.FatBlock as MyCubeBlock;
            if (fat == null)
                return;

            if (_tracked.Remove(fat.EntityId))
                fat.OnBlockModelChange -= OnBlockModelChange;
            _applying.Remove(fat.EntityId);
        }

        void SetupFloorPlan(MyCubeBlock block)
        {
            if (block == null || !IsFloorPlan(block.SlimBlock))
                return;

            Track(block);

            // During world load, storage/render state may not be fully restored yet.
            // The post-start restoration pass will regenerate the correct model.
            if (!_startupReady)
                return;

            RestoreBlockModel(block);
        }

        void Track(MyCubeBlock block)
        {
            if (block == null || _tracked.ContainsKey(block.EntityId))
                return;

            _tracked[block.EntityId] = block;
            block.OnBlockModelChange += OnBlockModelChange;
        }
    }
}
