using System;
using System.Collections.Generic;

namespace DynamicFloorPlanSign.Common
{
    internal static class VanillaSignCatalog
    {
        static readonly Dictionary<string, string> LabelToSubtype = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "AIRLOCK", "LargeBlockFloorPlanSign1" },
            { "BRIDGE", "LargeBlockFloorPlanSign2" },
            { "CAFETERIA", "LargeBlockFloorPlanSign3" },
            { "CRYOGENICS", "LargeBlockFloorPlanSign4" },
            { "ENGINEERING", "LargeBlockFloorPlanSign5" },
            { "EXIT", "LargeBlockFloorPlanSign6" },
            { "FARM", "LargeBlockFloorPlanSign7" },
            { "HANGAR", "LargeBlockFloorPlanSign8" },
            { "LABORATORY", "LargeBlockFloorPlanSign9" },
            { "LOBBY", "LargeBlockFloorPlanSign10" },
            { "LOGISTICS", "LargeBlockFloorPlanSign11" },
            { "MAINTENANCE", "LargeBlockFloorPlanSign12" },
            { "MARKET", "LargeBlockFloorPlanSign13" },
            { "MEDICAL", "LargeBlockFloorPlanSign14" },
            { "PRODUCTION", "LargeBlockFloorPlanSign15" },
            { "QUARTERS", "LargeBlockFloorPlanSign16" },
            { "SECURITY", "LargeBlockFloorPlanSign17" },
            { "SERVER", "LargeBlockFloorPlanSign18" },
            { "STAIRWELL", "LargeBlockFloorPlanSign19" },
            { "STORAGE", "LargeBlockFloorPlanSign20" },
            { "WAREHOUSE", "LargeBlockFloorPlanSign21" }
        };

        static readonly Dictionary<string, string> SubtypeToLabel = BuildReverse();

        public static bool TryGetSubtype(string label, out string subtype)
        {
            return LabelToSubtype.TryGetValue(label, out subtype);
        }

        public static bool TryGetBestSubtype(string label, out string subtype)
        {
            subtype = null;
            if (string.IsNullOrEmpty(label))
                return false;

            if (TryGetSubtype(label, out subtype))
                return true;

            int bestLength = 0;
            int bestPosition = int.MaxValue;
            foreach (KeyValuePair<string, string> candidate in LabelToSubtype)
            {
                int position = label.IndexOf(candidate.Key, StringComparison.Ordinal);
                while (position >= 0)
                {
                    int end = position + candidate.Key.Length;
                    // Match complete words: EXIT should not match EXITING, for example.
                    if ((position == 0 || label[position - 1] == ' ') &&
                        (end == label.Length || label[end] == ' '))
                    {
                        // Prefer the most specific label, then its earliest occurrence.
                        if (candidate.Key.Length > bestLength ||
                            (candidate.Key.Length == bestLength && position < bestPosition))
                        {
                            subtype = candidate.Value;
                            bestLength = candidate.Key.Length;
                            bestPosition = position;
                        }
                        break;
                    }

                    position = label.IndexOf(candidate.Key, position + 1, StringComparison.Ordinal);
                }
            }

            return subtype != null;
        }

        public static bool TryGetLabel(string subtype, out string label)
        {
            return SubtypeToLabel.TryGetValue(subtype, out label);
        }

        static Dictionary<string, string> BuildReverse()
        {
            Dictionary<string, string> reverse = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in LabelToSubtype)
                reverse[pair.Value] = pair.Key;
            return reverse;
        }
    }
}
