using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender.Import;

namespace DynamicFloorPlanSign.Client.UI
{
    [MyUseObject("dynsign")]
    public class DynamicSignUseObject : MyUseObjectBase
    {
        readonly MyCubeBlock _block;
        readonly Matrix _localMatrix;

        static string _clipboardLabel = string.Empty;

        static bool _rightWasDown;
        static bool _middleWasDown;

        public DynamicSignUseObject(
            IMyEntity owner,
            string dummyName,
            IMyModelDummy dummyData,
            uint key)
            : base(owner, dummyData)
        {
            _block = owner as MyCubeBlock;
            _localMatrix = dummyData.Matrix;
        }

        public override float InteractiveDistance => 3f;

        public override MatrixD ActivationMatrix =>
            _block != null
                ? (MatrixD)_localMatrix * _block.WorldMatrix
                : MatrixD.Identity;

        public override MatrixD WorldMatrix =>
            _block?.WorldMatrix ?? MatrixD.Identity;

        public override uint RenderObjectID
        {
            get
            {
                if (_block == null || _block.Render == null)
                    return uint.MaxValue;

                uint[] ids = _block.Render.RenderObjectIDs;

                return ids != null && ids.Length > 0
                    ? ids[0]
                    : uint.MaxValue;
            }
        }

        public override int InstanceID => -1;

        public override bool ShowOverlay => false;

        public override UseActionEnum SupportedActions =>
            PrimaryAction | SecondaryAction;

        public override UseActionEnum PrimaryAction =>
            UseActionEnum.Manipulate;

        public override UseActionEnum SecondaryAction =>
            UseActionEnum.None;

        public override bool ContinuousUsage => false;

        public override bool PlayIndicatorSound => true;

        public override MyActionDescription GetActionInfo(
            UseActionEnum actionEnum)
        {
            return new MyActionDescription
            {
                Text = MyStringId.GetOrCompute(""),
                IsTextControlHint = false
            };
        }

        public override void Use(
            UseActionEnum actionEnum,
            IMyEntity user)
        {
            if (actionEnum == UseActionEnum.Manipulate &&
                _block != null)
            {
                DynamicFloorPlanSignSession.OpenEditorFromUseObject(
                    _block,
                    user);
            }
        }

        public override bool HandleInput()
        {
            if (_block == null)
            {
                SetMouseInputBlocked(false);
                return false;
            }
            
            SetMouseInputBlocked(true);

            bool rightDown =
                MyAPIGateway.Input.IsRightMousePressed();

            bool middleDown =
                MyAPIGateway.Input.IsMiddleMousePressed();

            bool rightPressed =
                rightDown && !_rightWasDown;

            bool middlePressed =
                middleDown && !_middleWasDown;
            
            _rightWasDown = rightDown;
            _middleWasDown = middleDown;
            
            if (rightPressed)
            {
                if (!DynamicFloorPlanSignSession.CanLocalPlayerEditSign(_block))
                {
                    DynamicFloorPlanSignSession.Notify(
                        "Access denied.",
                        MyFontEnum.Red);

                    return true;
                }

                if (MyAPIGateway.Input.IsAnyShiftKeyPressed())
                {
                    DynamicFloorPlanSignSession.Instance
                        .RestoreVanillaModel(_block);
                }
                else
                {
                    DynamicFloorPlanSignSession.Notify(
                        "Hold Shift to restore original sign",
                        MyFontEnum.Red);
                }

                return true;
            }
            
            if (middlePressed)
            {
                if (MyAPIGateway.Input.IsAnyShiftKeyPressed())
                {
                    string currentLabel =
                        DynamicFloorPlanSignSession.GetCurrentLabel(_block);

                    if (!string.IsNullOrWhiteSpace(currentLabel))
                    {
                        _clipboardLabel = currentLabel;

                        DynamicFloorPlanSignSession.Notify(
                            $"Copied label '{_clipboardLabel}'.");
                    }
                    else
                    {
                        DynamicFloorPlanSignSession.Notify(
                            "No label to copy from this floor plan sign.",
                            MyFontEnum.Red);
                    }

                    return true;
                }

                if (!DynamicFloorPlanSignSession
                        .CanLocalPlayerEditSign(_block))
                {
                    DynamicFloorPlanSignSession.Notify(
                        "Access denied.",
                        MyFontEnum.Red);

                    return true;
                }

                if (string.IsNullOrEmpty(_clipboardLabel))
                {
                    DynamicFloorPlanSignSession.Notify(
                        "No label to paste.",
                        MyFontEnum.Red);

                    return true;
                }

                DynamicFloorPlanSignSession.Instance.SubmitText(
                    _block.CubeGrid.EntityId,
                    _block.Position,
                    _clipboardLabel);

                DynamicFloorPlanSignSession.Notify(
                    $"Pasted label '{_clipboardLabel}' to floor plan sign.",
                    MyFontEnum.White);

                return true;
            }

            return false;
        }

        public override void OnSelectionLost()
        {
            _rightWasDown =
                MyAPIGateway.Input.IsRightMousePressed();

            _middleWasDown =
                MyAPIGateway.Input.IsMiddleMousePressed();

            SetMouseInputBlocked(false);
        }

        static void SetMouseInputBlocked(bool blocked)
        {
            DynamicFloorPlanSignSession session =
                DynamicFloorPlanSignSession.Instance;

            if (session != null)
                session.SetLocalSignInputBlocked(blocked);
        }
    }
}
