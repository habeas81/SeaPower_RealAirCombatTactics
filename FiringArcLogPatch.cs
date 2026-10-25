using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Diagnostic: logs why a missile hardpoint reports "Target is not in firing arcs". The game checks the arc
    // against the unit's _alignmentPosition (its aim point), so we log the angle to BOTH the aim point and the
    // real target, plus the weapon's half-arc, to show which one is out of the cone.
    [HarmonyPatch(typeof(WeaponSystemHardpoint), "CheckFiringConstraints")]
    public static class FiringArcLogPatch
    {
        private const double LogIntervalSec = 3.0; // per hardpoint

        private static readonly Dictionary<int, double> LastLog = new Dictionary<int, double>();

        private static float BearingOff(Transform t, Vector3 point)
        {
            Vector3 v = point - t.position;
            float a = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg - t.eulerAngles.y;
            return Mathf.DeltaAngle(0f, a);
        }

        public static void Postfix(WeaponSystemHardpoint __instance, bool __result)
        {
            try
            {
                if (__result || __instance._engageState != WeaponSystem.EngageState.TargetIsNotInFiringArcs)
                {
                    return;
                }

                ObjectBase me = __instance._baseObject;
                ObjectBase target = __instance._targetObject;
                if (me == null || target == null)
                {
                    return;
                }

                int key = __instance.GetHashCode();
                double now = GameTime.missionElapsedTime;
                if (LastLog.TryGetValue(key, out double last) && now >= last && now - last < LogIntervalSec)
                {
                    return;
                }

                LastLog[key] = now;
                Vector2 arc = __instance._vwp._firingArcs.Count > 0 ? __instance._vwp._firingArcs[0] : Vector2.zero;
                float toAim = BearingOff(me.transform, me._alignmentPosition);
                float toTgt = BearingOff(me.transform, target.transform.position);
                float rangeNm = Vector3.Distance(me.transform.position, target.transform.position) / 27.559496f;
                string ammo = __instance._ammoForEngage?._ap?._ammunitionFileName ?? "?";
                ModLog.Event($"t={now:0} ARC-BLOCK: {me.getUIDAndName()} {ammo} -> {target.getUIDAndName()} {rangeNm:0.0}nm: aimPoint off nose {toAim:0.0}deg, target off nose {toTgt:0.0}deg, arc(raw) ({arc.x:0.0},{arc.y:0.0}).");
            }
            catch (System.Exception ex)
            {
                ModLog.Error("FiringArcLogPatch", ex);
            }
        }
    }
}
