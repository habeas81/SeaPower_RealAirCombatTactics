using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Off-boresight launch fix.
    //
    // Vanilla checks the missile's firing arc against the unit's _alignmentPosition (its aim/lead point) rather than the
    // target itself. For a radar-guided AAM (AIM-120) the seeker only needs the TARGET inside the radar/launch cone;
    // the missile then flies its own lead. When the game's aim point sat 8-14 deg off the nose while the target was
    // within 2-3 deg, the fighter reported "Target is not in firing arcs" and withheld a valid shot.
    //
    // This postfix re-tests the arc against the real target position for air-to-air missiles and lets the shot go if
    // the target is inside the arc. Vanilla's other checks (range, vertical arc, MissileCanTurnToReachAirTarget,
    // sensor pre-launch) still apply afterwards.
    [HarmonyPatch(typeof(WeaponSystem), nameof(WeaponSystem.targetIsInFiringArc))]
    public static class OffBoresightArcPatch
    {
        private const double LogIntervalSec = 5.0; // per weapon system

        private static readonly Dictionary<int, double> LastLog = new Dictionary<int, double>();

        private static bool IsInArc(WeaponSystem ws, Vector3 point)
        {
            Vector3 v = point - ws._baseObject.transform.position;
            float off = Mathf.DeltaAngle(0f, Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg - ws._baseObject.transform.eulerAngles.y);
            foreach (Vector2 arc in ws._vwp._firingArcs)
            {
                if (arc == Vector2.zero)
                {
                    continue;
                }

                if (arc.x < arc.y ? (arc.x < off && off < arc.y) : (arc.y >= off || off >= arc.x))
                {
                    return true;
                }
            }

            return false;
        }

        public static void Postfix(WeaponSystem __instance, Vector3 interceptPosition, ref bool __result)
        {
            try
            {
                if (__result || !(__instance._baseObject is Aircraft))
                {
                    return;
                }

                ObjectBase target = __instance._targetObject;
                AmmunitionParameters ap = __instance._ammoForEngage?._ap;
                if (target == null || ap == null || ap._type != Ammunition.Type.Missile || !target.isAirTarget() || target.isWeapon())
                {
                    return;
                }

                if (!IsInArc(__instance, target.transform.position))
                {
                    return;
                }

                __result = true;
                int key = __instance.GetHashCode();
                double now = GameTime.missionElapsedTime;
                if (!LastLog.TryGetValue(key, out double last) || now < last || now - last >= LogIntervalSec)
                {
                    LastLog[key] = now;
                    ModLog.Event($"t={now:0} OFF-BORESIGHT: {__instance._baseObject.getUIDAndName()} {ap._ammunitionFileName} -> {target.getUIDAndName()}: target inside firing arc, ignoring aim-point offset.");
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Error("OffBoresightArcPatch", ex);
            }
        }
    }
}
