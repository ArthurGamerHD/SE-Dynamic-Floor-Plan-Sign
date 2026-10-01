using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace DynamicFloorPlanSign.Client.UI
{
    // Text input helper, cloned from my LCD mod https://github.com/ArthurGamerHD/Arthur-s-Lcd-Mod/blob/main/Arthur-s-Lcd-Mod/Client/Helpers/TextInputHelper.cs
    internal static class TextInputHelper
    {
        static bool _wasOpened;

        static readonly MyDefinitionId CockpitId =
            new MyDefinitionId(typeof(MyObjectBuilder_Cockpit), "SmallBlockCockpit");

        static MyCockpitDefinition _cockpitDefinition;
        static string _originalLcdDisplayName;
        static TextInputModel _clientTextInput;

        static Action<string> _currentCallback;
        static bool _currentReadonly;
        static string _currentTitle = string.Empty;
        static string _currentSubTitle = string.Empty;
        static string _initialText = string.Empty;

        public static void SpawnForLocalPlayer(
            string title,
            Action<string> callback,
            string initialText = "",
            string subtitle = "",
            bool readOnly = false)
        {
            _currentTitle = title ?? string.Empty;
            _currentSubTitle = subtitle ?? string.Empty;
            _initialText = initialText ?? string.Empty;
            _currentCallback = callback;
            _currentReadonly = readOnly;

            if (_clientTextInput != null)
                _clientTextInput.Close();

            SpawnInternal(OpenTextBox);
        }

        public static void Shutdown()
        {
            if (_clientTextInput != null)
                _clientTextInput.Close();
            _clientTextInput = null;
            _currentCallback = null;
            _wasOpened = false;
            RestoreLcdName();
        }

        static string GetSerializedText(int surfaceIndex = 0)
        {
            if (_clientTextInput == null || _clientTextInput.Grid == null)
                return string.Empty;

            IMySlimBlock slimBlock = _clientTextInput.Grid.GetCubeBlock(Vector3I.Zero);
            IMyCubeBlock fatBlock = slimBlock?.FatBlock;
            if (fatBlock == null)
                return string.Empty;

            MyObjectBuilder_CubeBlock blockBuilder = fatBlock.GetObjectBuilderCubeBlock(true);
            if (blockBuilder == null || blockBuilder.ComponentContainer == null || blockBuilder.ComponentContainer.Components == null)
                return string.Empty;

            foreach (MyObjectBuilder_ComponentContainer.ComponentData componentData in blockBuilder.ComponentContainer.Components)
            {
                if (componentData.TypeId != "MyMultiTextPanelComponent")
                    continue;

                MyObjectBuilder_MultiTextPanelComponent multiTextBuilder =
                    componentData.Component as MyObjectBuilder_MultiTextPanelComponent;

                if (multiTextBuilder == null || multiTextBuilder.TextPanelsContents == null)
                    return string.Empty;

                if (surfaceIndex < 0 || surfaceIndex >= multiTextBuilder.TextPanelsContents.Count)
                    return string.Empty;

                return multiTextBuilder.TextPanelsContents[surfaceIndex].Text ?? string.Empty;
            }

            return string.Empty;
        }

        static void OpenTextBox(TextInputModel textInputModel)
        {
            if (_clientTextInput != null)
                _clientTextInput.Close();
            _clientTextInput = textInputModel;

            if (_clientTextInput == null || _clientTextInput.Cockpit == null || _clientTextInput.Lcd == null)
                return;

            _clientTextInput.Cockpit.OpenWindow(!_currentReadonly, false, true);
            DynamicFloorPlanSignSession.QueueNextFrame(CheckIfIsOpened);
        }

        static void RestoreLcdName()
        {
            if (_cockpitDefinition != null && _cockpitDefinition.ScreenAreas != null && _cockpitDefinition.ScreenAreas.Count > 0)
                _cockpitDefinition.ScreenAreas[0].DisplayName = _originalLcdDisplayName;
        }

        static void CheckIfIsOpened()
        {
            bool isOpen = MyAPIGateway.Gui != null && MyAPIGateway.Gui.IsCursorVisible;

            if (isOpen || !_wasOpened)
            {
                _wasOpened = isOpen;
                DynamicFloorPlanSignSession.QueueNextFrame(CheckIfIsOpened);
                return;
            }

            _wasOpened = false;
            string text = GetSerializedText();
            RestoreLcdName();

            Action<string> callback = _currentCallback;
            _currentCallback = null;
            if (callback != null)
                callback(text);

            if (_clientTextInput != null)
                _clientTextInput.Close();
            _clientTextInput = null;
        }

        static void SpawnInternal(Action<TextInputModel> onSpawned)
        {
            if (_cockpitDefinition == null)
            {
                _cockpitDefinition = MyDefinitionManager.Static.GetCubeBlockDefinition(CockpitId) as MyCockpitDefinition;
                if (_cockpitDefinition == null || _cockpitDefinition.ScreenAreas == null || _cockpitDefinition.ScreenAreas.Count == 0)
                {
                    DynamicFloorPlanSignSession.Notify("Could not open sign text editor.");
                    return;
                }

                _originalLcdDisplayName = _cockpitDefinition.ScreenAreas[0].DisplayName;
            }

            _cockpitDefinition.ScreenAreas[0].DisplayName = _currentSubTitle;

            MyObjectBuilder_Cockpit blockBuilder =
                MyObjectBuilderSerializer.CreateNewObject(_cockpitDefinition.Id) as MyObjectBuilder_Cockpit;
            if (blockBuilder == null)
            {
                RestoreLcdName();
                return;
            }

            MyObjectBuilder_MultiTextPanelComponent multiTextBuilder =
                MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_MultiTextPanelComponent>();

            multiTextBuilder.TextPanelsContents = new List<MySerializedTextPanelData>
            {
                new MySerializedTextPanelData { Text = _initialText ?? string.Empty }
            };

            blockBuilder.BuildPercent = 1f;
            blockBuilder.IntegrityPercent = 1f;
            blockBuilder.Min = Vector3I.Zero;
            blockBuilder.BlockOrientation = MyBlockOrientation.Identity;
            blockBuilder.CustomName = string.IsNullOrEmpty(_currentTitle) ? "Sign text" : _currentTitle;

            blockBuilder.ComponentContainer = new MyObjectBuilder_ComponentContainer();
            blockBuilder.ComponentContainer.Components.Add(
                new MyObjectBuilder_ComponentContainer.ComponentData
                {
                    TypeId = "MyMultiTextPanelComponent",
                    Component = multiTextBuilder
                });

            MyObjectBuilder_CubeGrid gridBuilder = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_CubeGrid>();
            gridBuilder.GridSizeEnum = _cockpitDefinition.CubeSize;
            gridBuilder.IsStatic = false;
            gridBuilder.Editable = false;
            gridBuilder.DestructibleBlocks = false;
            gridBuilder.CubeBlocks.Add(blockBuilder);

            MatrixD? matrix = MyAPIGateway.Session != null && MyAPIGateway.Session.LocalHumanPlayer != null &&
                              MyAPIGateway.Session.LocalHumanPlayer.Character != null
                ? (MatrixD?)MyAPIGateway.Session.LocalHumanPlayer.Character.PositionComp.WorldMatrixRef
                : null;
            gridBuilder.PositionAndOrientation = new MyPositionAndOrientation(matrix ?? MatrixD.Identity);

            MyAPIGateway.Utilities.InvokeOnGameThread(delegate
            {
                IMyEntity entity = MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(gridBuilder);
                if (entity == null)
                {
                    RestoreLcdName();
                    DynamicFloorPlanSignSession.Notify("Could not create sign text editor.");
                    return;
                }

                entity.Synchronized = false;
                entity.StopPhysicsActivation = true;
                entity.Save = false;
                entity.Render.Visible = false;

                IMyCubeGrid grid = entity as IMyCubeGrid;
                if (grid == null)
                {
                    entity.Close();
                    RestoreLcdName();
                    return;
                }

                grid.CustomName = "DynamicFloorPlanSign_TextInputGrid";
                MyCockpit cockpit = grid.GetCubeBlock(Vector3I.Zero) != null
                    ? grid.GetCubeBlock(Vector3I.Zero).FatBlock as MyCockpit
                    : null;
                onSpawned(new TextInputModel(grid, cockpit));
            });
        }

        internal sealed class TextInputModel
        {
            public IMyCubeGrid Grid { get; private set; }
            public MyCockpit Cockpit { get; private set; }
            public IMyTextSurface Lcd { get; private set; }

            public TextInputModel(IMyCubeGrid grid, MyCockpit cockpit)
            {
                Grid = grid;
                Cockpit = cockpit;
                Lcd = ((IMyCockpit)Cockpit)?.GetSurface(0);
                if (Grid != null)
                    Grid.OnMarkForClose += OnGridMarkedForClose;
                Update();
            }

            public void Update()
            {
                if (Grid == null)
                    return;

                DynamicFloorPlanSignSession.QueueNextFrame(Update);
                if (MyAPIGateway.Session != null && MyAPIGateway.Session.LocalHumanPlayer != null)
                    Grid.SetPosition(MyAPIGateway.Session.LocalHumanPlayer.GetPosition());
            }

            public void Close()
            {
                if (Grid == null)
                    return;

                Grid.OnMarkForClose -= OnGridMarkedForClose;
                if (!Grid.MarkedForClose)
                    Grid.Close();

                Grid = null;
                Cockpit = null;
                Lcd = null;
            }

            void OnGridMarkedForClose(IMyEntity entity)
            {
                if (Grid == null)
                    return;

                Grid.OnMarkForClose -= OnGridMarkedForClose;
                Grid = null;
                Cockpit = null;
                Lcd = null;
            }
        }
    }
}
