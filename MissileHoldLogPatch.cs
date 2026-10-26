using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Diagnostic: logs why an air-to-air missile hardpoint is holding its shot. CheckFiringConstraints is only reached
    // after CheckMaxRange passes, so the earlier gates (range components, system opening, prelaunch alignment) were
    // invisible. This runs after every hardpoint update and logs the final engage state, the weapon's own tooltip status
    // and the range components (kinematic / sensor / terrain) that decide when the shot is allowed.
    [HarmonyPatch(typeof(WeaponSystemHardpoint), "OnUpdate")]
    public static class MissileHoldLogPatch
    {
        private const double RepeatSec = 5.0; // re-log an unchanged state this often
        private const float UnityPerNm = 27.559496f;

        // Runs after every hardpoint update, so it is only installed when diagnostics are enabled.
        private static bool Prepare() => ModLog.Diagnostics;

        private static readonly Dictionary<int, WeaponSystem.EngageState> LastState = new Dictionary<int, WeaponSystem.EngageState>();
        private static readonly Dictionary<int, double> LastTime = new Dictionary<int, double>();

        public static void Postfix(WeaponSystemHardpoint __instance)
        {
            try
            {
                if (!__instance._executingEngageTask || __instance._ammoForEngage?._ap == null
                    || __instance._ammoForEngage._ap._type != Ammunition.Type.Missile)
                {
                    return;
                }

                ObjectBase me = __instance._baseObject;
                ObjectBase target = __instance._targetObject;
                if (me == null || target == null || !target.isAirTarget())
                {
                    return;
                }

                int key = __instance.GetHashCode();
                double now = GameTime.missionElapsedTime;
                WeaponSystem.EngageState state = __instance._engageState;
                bool changed = !LastState.TryGetValue(key, out var prev) || prev != state;
                if (!changed && LastTime.TryGetValue(key, out double last) && now >= last && now - last < RepeatSec)
                {
                    return;
                }

                LastState[key] = state;
                LastTime[key] = now;

                var r = __instance._calculatedRange;
                var ap = __instance._ammoForEngage._ap;
                float rangeNm = Vector3.Distance(me.transform.position, target.transform.position) / UnityPerNm;
                float pitch = -Utils.calculatePitchToTarget(me.getTransform().forward, me.transform.position, me._alignmentPosition);
                ModLog.Event($"t={now:0} MISSILE-HOLD: {me.getUIDAndName()} {ap._ammunitionFileName} -> {target.getUIDAndName()} {rangeNm:0.0}nm: state={state}, weapon status='{__instance.GetStatus()}', unit status='{me.CurrentOrderText?.Value}', ranges kin {r.KinematicRange / UnityPerNm:0.0} / sensor {r.SensorRange / UnityPerNm:0.0} / terrain {(r.TerrainRange > 1e6f ? 999f : r.TerrainRange / UnityPerNm):0.0} nm, launch band {ap._launchRangesInUnity.x / UnityPerNm:0.0}-{ap._launchRangesInUnity.y / UnityPerNm:0.0} nm, pitch {pitch:0.0}deg, vertReq={__instance._verticalAlignmentRequired}, prelaunchAligned={__instance._sensorPreLaunchAligned}, tgtSpd {target._velocityInKnots:0}kt, radar {(me.IsAirSearchRadarsOn.Value ? "ON" : "OFF")}.");
            }
            catch (System.Exception ex)
            {
                ModLog.Error("MissileHoldLogPatch", ex);
            }
        }
    }
}
