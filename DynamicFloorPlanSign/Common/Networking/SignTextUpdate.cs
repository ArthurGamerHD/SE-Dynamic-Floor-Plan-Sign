using Generated;
using ProtoBuf;

namespace DynamicFloorPlanSign.Common.Networking
{
    // Both directions carry only intent; every peer builds its own sign locally.
    [ProtoContract]
    [NetworkPayload(1)]
    internal partial class SignTextUpdate
    {
        [ProtoMember(1)] public long GridId;
        [ProtoMember(2)] public int X;
        [ProtoMember(3)] public int Y;
        [ProtoMember(4)] public int Z;
        [ProtoMember(5)] public string Text;
    }
}
