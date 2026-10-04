using System;
using System.Collections.Generic;
using Adk.Utils;
using DynamicFloorPlanSign.Client.Rendering;
using DynamicFloorPlanSign.Client.UI;
using DynamicFloorPlanSign.Common.Fonts;
using Generated;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace DynamicFloorPlanSign
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public partial class DynamicFloorPlanSignSession : MySessionComponentBase
    {
        internal static DynamicFloorPlanSignSession Instance;

        const string NATIVE_FONT_XML = @"SignFonts\Native\FontData.xml";

        List<Action> _nextFrame = new List<Action>();
        List<Action> _thisFrame = new List<Action>();
        bool _registered;
        bool _startupReady;
        int _startupFramesRemaining;

        public override void LoadData()
        {
            LogHelper.LogInfo("Init - Version " + Constants.VersionName);

            SignFonts.AddFont(-1, NATIVE_FONT_XML, 0, null, ModContext.ModItem);
            SignFonts.Load();
        }

        public override void BeforeStart()
        {
            Instance = this;

            if (MyAPIGateway.Utilities == null || MyAPIGateway.Session == null)
                return;

            MyAPIGateway.Utilities.MessageEntered += OnMessageEntered;

            if (MyAPIGateway.Multiplayer != null)
            {
                _network = new NetworkManager(new NetworkParameters(NETWORK_CHANNEL, 4096, 16384, 4));
                _network.Init();
            }

            if (MyAPIGateway.Entities != null)
            {
                MyAPIGateway.Entities.OnEntityAdd += OnEntityAdd;
                MyAPIGateway.Entities.OnEntityRemove += OnEntityRemove;
                RestoreExistingEntities();
            }

            // Entity storage/model state can still be settling during BeforeStart.
            // Track everything now, but defer model regeneration until simulation has begun.
            _startupReady = false;
            _startupFramesRemaining = 2;
            _registered = true;
        }

        public override void UpdateBeforeSimulation()
        {
            if (_nextFrame.Count != 0)
            {
                List<Action> temp = _thisFrame;
                _thisFrame = _nextFrame;
                _nextFrame = temp;
                _nextFrame.Clear();

                for (int i = 0; i < _thisFrame.Count; i++)
                {
                    try
                    {
                        Action action = _thisFrame[i];
                        if (action != null)
                            action();
                    }
                    catch (Exception e)
                    {
                        LogHelper.Log(MyLogSeverity.Error, "deferred action failed: " + e);
                    }
                }
                _thisFrame.Clear();
            }

            if (!_startupReady && _startupFramesRemaining > 0)
            {
                _startupFramesRemaining--;
                if (_startupFramesRemaining == 0)
                {
                    _startupReady = true;
                    RestoreTrackedModels();
                }
            }

            if (_startupReady)
                ApplyPendingSignUpdates();
        }

        protected override void UnloadData()
        {
            TextInputHelper.Shutdown();

            SetLocalSignInputBlocked(false);

            if (_registered)
            {
                if (MyAPIGateway.Utilities != null)
                    MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered;

                if (MyAPIGateway.Entities != null)
                {
                    MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdd;
                    MyAPIGateway.Entities.OnEntityRemove -= OnEntityRemove;
                }
            }

            if (_network != null)
            {
                _network.Dispose();
                _network = null;
            }

            foreach (KeyValuePair<long, MyCubeBlock> pair in _tracked)
            {
                if (pair.Value != null)
                    pair.Value.OnBlockModelChange -= OnBlockModelChange;
            }

            foreach (KeyValuePair<long, IMyCubeGrid> pair in _trackedGrids)
            {
                IMyCubeGrid grid = pair.Value;
                if (grid == null)
                    continue;
                grid.OnBlockAdded -= OnBlockAdded;
                grid.OnBlockRemoved -= OnBlockRemoved;
            }

            RuntimeMwmBuilder.ClearSessionCache(typeof(DynamicFloorPlanSignSession));
            SignFonts.Unload();

            _tracked.Clear();
            _trackedGrids.Clear();
            _applying.Clear();
            _pendingSignUpdates.Clear();
            _nextFrame.Clear();
            _thisFrame.Clear();
            _registered = false;
            _startupReady = false;
            _startupFramesRemaining = 0;
            Instance = null;
        }

        internal static void QueueNextFrame(Action action)
        {
            if (Instance != null && action != null)
                Instance._nextFrame.Add(action);
        }
    }
}
