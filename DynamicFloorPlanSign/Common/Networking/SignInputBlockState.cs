using Generated;
using ProtoBuf;

namespace DynamicFloorPlanSign.Common.Networking
{
    [ProtoContract]
    [NetworkPayload(2)]
    internal partial class SignInputBlockState
    {
        [ProtoMember(1)] public bool Enabled;
    }
}
