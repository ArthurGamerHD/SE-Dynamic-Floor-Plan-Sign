using System.Collections.Generic;
using DynamicFloorPlanSign.Common.Networking;
using VRageMath;

namespace DynamicFloorPlanSign
{
    public partial class DynamicFloorPlanSignSession
    {
        const int SIGN_UPDATE_RETRY_FRAMES = 600;

        readonly List<PendingSignUpdate> _pendingSignUpdates = new List<PendingSignUpdate>();

        sealed class PendingSignUpdate
        {
            public SignTextUpdate Update;
            public int FramesRemaining = SIGN_UPDATE_RETRY_FRAMES;
        }

        void QueueSignUpdate(SignTextUpdate update)
        {
            for (int i = _pendingSignUpdates.Count - 1; i >= 0; i--)
            {
                SignTextUpdate previous = _pendingSignUpdates[i].Update;
                if (previous.GridId == update.GridId && previous.X == update.X &&
                    previous.Y == update.Y && previous.Z == update.Z)
                    _pendingSignUpdates.RemoveAt(i);
            }
            _pendingSignUpdates.Add(new PendingSignUpdate { Update = update });
        }

        void ApplyPendingSignUpdates()
        {
            for (int i = _pendingSignUpdates.Count - 1; i >= 0; i--)
            {
                PendingSignUpdate pending = _pendingSignUpdates[i];
                SignTextUpdate update = pending.Update;
                if (ApplySignTextLocal(update.GridId, new Vector3I(update.X, update.Y, update.Z), update.Text) ||
                    --pending.FramesRemaining <= 0)
                    _pendingSignUpdates.RemoveAt(i);
            }
        }
    }
}
