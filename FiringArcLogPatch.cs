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

        // Diagnostic only (runs on every firing-constraint check): installed only when diagnostics are enabled.
        private static bool Prepare() => ModLog.Diagnostics;

        private static readonly Dictionary<int, double> LastLog = new Dictionary<int, double>();

        private static float BearingOff(Transform t, Vector3 point)
        {
            Vector3 v = point - t.position;
            float a = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg - t.eulerAngles.y;
            return Mathf.DeltaAngle(0f, a);
        }

        private static readonly Dictionary<int, WeaponSystem.EngageState> LastState = new Dictionary<int, WeaponSystem.EngageState>();
        private static readonly Dictionary<int, double> LastOtherLog = new Dictionary<int, double>();

        private static void LogOtherBlock(WeaponSystemHardpoint hp, ObjectBase me, ObjectBase target)
        {
            if (hp._ammoForEngage?._ap == null || hp._ammoForEngage._ap._type != Ammunition.Type.Missile || !target.isAirTarget())
            {
                return;
            }

            int key = hp.GetHashCode();
            double now = GameTime.missionElapsedTime;
            bool changed = !LastState.TryGetValue(key, out var prev) || prev != hp._engageState;
            if (!changed && LastOtherLog.TryGetValue(key, out double last) && now >= last && now - last < 5.0)
            {
                return;
            }

            LastState[key] = hp._engageState;
            LastOtherLog[key] = now;
            float rangeNm = Vector3.Distance(me.transform.position, target.transform.position) / 27.559496f;
            float pitch = -Utils.calculatePitchToTarget(me.getTransform().forward, me.transform.position, me._alignmentPosition);
            var ap = hp._ammoForEngage._ap;
            string weaponStatus = hp.GetStatus();
            string unitStatus = me.CurrentOrderText?.Value ?? "?";
            ModLog.Event($"t={now:0} FIRE-BLOCK: {me.getUIDAndName()} {ap._ammunitionFileName} -> {target.getUIDAndName()} {rangeNm:0.0}nm: state={hp._engageState}, weapon status='{weaponStatus}', unit status='{unitStatus}', pitch-to-aim {pitch:0.0}deg, vertReq={hp._verticalAlignmentRequired}, launch range {ap._launchRangesInUnity.x / 27.559496f:0.0}-{ap._launchRangesInUnity.y / 27.559496f:0.0}nm, tgtSpd {target._velocityInKnots:0}kt (max {(ap._maxAttackVelocity < float.MaxValue ? ap._maxAttackVelocity : 0f):0}), radar {(me.IsAirSearchRadarsOn.Value ? "ON" : "OFF")}.");
        }

        public static void Postfix(WeaponSystemHardpoint __instance, bool __result)
        {
            try
            {
                if (__result)
                {
                    return;
                }

                ObjectBase me = __instance._baseObject;
                ObjectBase target = __instance._targetObject;
                if (me == null || target == null)
                {
                    return;
                }

                // Any other blocker on an air-to-air missile shot is logged with its state name, so a late launch
                // can be traced to the exact vanilla gate (vertical arc, FCR detect, can't reach, altitude, ...).
                if (__instance._engageState != WeaponSystem.EngageState.TargetIsNotInFiringArcs)
                {
                    LogOtherBlock(__instance, me, target);
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
                ModLog.Event($"t={now:0} ARC-BLOCK: {me.getUIDAndName()} {ammo} -> {target.getUIDAndName()} {rangeNm:0.0}nm: weapon status='{__instance.GetStatus()}', unit status='{me.CurrentOrderText?.Value}', aimPoint off nose {toAim:0.0}deg, target off nose {toTgt:0.0}deg, arc(raw) ({arc.x:0.0},{arc.y:0.0}).");
            }
            catch (System.Exception ex)
            {
                ModLog.Error("FiringArcLogPatch", ex);
            }
        }
    }
}
