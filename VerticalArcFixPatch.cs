using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Fixes the missile "Target is not in vertical firing arcs" hold at east/west headings.
    // Vanilla Utils.calculatePitchToTarget zeroes the WORLD x component of both the line of sight and the nose vector,
    // which is only valid when flying along the world z axis (north/south). Heading east or west, the nose collapses
    // to a near-zero vector and the "pitch error" becomes garbage (logged: -60 to -107 deg at level flight), so a
    // radar missile (AIM-7, AA-7, ...) is held until the target is almost on top of the shooter. Its only caller is the
    // hardpoint's vertical-alignment check, which needs: angle of the target above the nose (positive = above).
    [HarmonyPatch(typeof(Utils), nameof(Utils.calculatePitchToTarget))]
    public static class VerticalArcFixPatch
    {
        private static bool Prefix(Vector3 sourceForward, Vector3 sourcePosition, Vector3 targetPosition, ref float __result)
        {
            Vector3 los = targetPosition - sourcePosition;
            float losHoriz = Mathf.Sqrt(los.x * los.x + los.z * los.z);
            float noseHoriz = Mathf.Sqrt(sourceForward.x * sourceForward.x + sourceForward.z * sourceForward.z);
            float losElev = Mathf.Atan2(los.y, losHoriz) * Mathf.Rad2Deg;
            float noseElev = Mathf.Atan2(sourceForward.y, noseHoriz) * Mathf.Rad2Deg;
            __result = losElev - noseElev;
            return false;
        }
    }
}
