using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Speed-aware launch range: against a fast air target, AI fighters shoot only from a fraction of the
    // missile's kinematic range, so the target cannot simply turn tail and outrun the missile.
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.GetMaxEngagementRanges))]
    public static class LaunchRangeGatePatch
    {
        private const float UnityUnitMeters = 67.2f;
        private const float MsToKnots = 1.94384f;

        // Targets slower than this are not considered able to out-run a missile.
        private const float EscapeSpeedFloorKnots = 400f;

        // Missiles spend time accelerating/coasting, so average speed is below the peak speed.
        private const float MissileAvgSpeedFraction = 0.70f;

        // Never shrink the launch range below this fraction of the vanilla range.
        private const float MinRangeFraction = 0.40f;

        // Targets beyond this multiple of the weapon's vanilla range skip all gate work.
        private const float VicinityFactor = 1.5f;

        // Multipliers on the escape-speed excess for head-on and tail-on aspects.
        private const float HeadOnPenalty = 0.35f;
        private const float TailPenalty = 1.60f;
        private const float KnotsToUnity = 0.0076554087f;

        private const int MaxFailures = 25;
        private static int _failures;
        private static bool _disabled;
        private static readonly Dictionary<string, double> LastLog = new Dictionary<string, double>();

        // Scales KinematicRange for every caller that decides when to fire or how far to close
        // (AI auto-engage, player-ordered engage tasks, the hardpoint launch check). The UI range
        // preview calls with usePreciseKinematics=true and is left untouched.
        public static void Postfix(ObjectBase __instance, Ammunition ammo, ObjectBase targetObject,
            bool usePreciseKinematics, ref ObjectBase.EngagementRange __result)
        {
            float vanilla = __result.KinematicRange;
            if (_disabled || usePreciseKinematics || vanilla <= 0f || vanilla >= float.MaxValue / 2f)
            {
                return;
            }

            try
            {
                if (targetObject == null || ammo?._ap == null || __instance == null
                    || !__instance.IsAirUnit || !targetObject.IsAirUnit || !(targetObject is Aircraft targetAircraft))
                {
                    return;
                }

                if (ammo._ap._type != Ammunition.Type.Missile)
                {
                    return;
                }

                // Cheap pre-filter: a target well outside the weapon's own range needs no gating or logging.
                float vicinity = vanilla * VicinityFactor;
                if ((targetObject.transform.position - __instance.transform.position).sqrMagnitude > vicinity * vicinity)
                {
                    return;
                }

                float targetKnots = MaxTargetSpeedKnots(targetAircraft);
                float missileKnots = ammo._ap._maxVelocityInKnots * MissileAvgSpeedFraction;
                if (missileKnots <= 1f)
                {
                    return;
                }

                if (PressureShot.IsActive(__instance, targetObject, out ObjectBase victim))
                {
                    LogGate(__instance, targetObject, ammo, targetKnots, missileKnots, vanilla, vanilla, 1f,
                        "pressure shot (defending " + victim.getUIDAndName() + ")");
                    return;
                }

                // Fraction of the missile's average speed the target gains by running (above a speed floor).
                float excess = Math.Max(0f, targetKnots - EscapeSpeedFloorKnots) / missileKnots;

                // 0 = target flying directly away from shooter, 1 = head-on.
                Vector3 los = (targetObject.transform.position - __instance.transform.position).normalized;
                float closingUnits = Vector3.Dot(__instance._velocityVecInUnity - targetObject._velocityVecInUnity, los);
                float speedSum = __instance._velocityInUnity + targetObject._velocityInUnity;
                float aspect = speedSum > 0.001f ? Mathf.Clamp01(0.5f + 0.5f * closingUnits / speedSum) : 0.5f;

                // Head-on: the target must first turn through 180 deg, bleeding energy, so only a mild penalty.
                // Tail-on: the target is already running, so the penalty is steep.
                float headOn = 1f - HeadOnPenalty * excess;
                float tail = 1f - TailPenalty * excess;
                float fraction = Mathf.Lerp(tail, headOn, aspect);
                fraction = Math.Max(MinRangeFraction, Math.Min(1f, fraction));

                // Crew skill: less disciplined crews ignore part of the reduction and shoot from further out.
                fraction = Mathf.Lerp(fraction, 1f, CrewSkillModel.For(__instance).GateIgnore);
                if (fraction >= 0.999f)
                {
                    return;
                }

                float original = vanilla;
                __result.KinematicRange = original * fraction;
                LogGate(__instance, targetObject, ammo, targetKnots, missileKnots, original, __result.KinematicRange, fraction,
                    $"closing {closingUnits / KnotsToUnity:0} kt, aspect {aspect:0.00}");
            }
            catch (Exception ex)
            {
                if (++_failures >= MaxFailures)
                {
                    _disabled = true;
                    ModLog.Error("LaunchRangeGatePatch disabled after repeated failures.", ex);
                }
                else
                {
                    ModLog.Error("LaunchRangeGatePatch.Postfix failed.", ex);
                }
            }
        }

        private static float MaxTargetSpeedKnots(Aircraft target)
        {
            float[] mach = target.Ap?._speedValuesInMach;
            if (mach == null || mach.Length == 0)
            {
                return 0f;
            }

            float altitudeMeters = Math.Max(0f, target.transform.position.y * UnityUnitMeters);
            return mach[mach.Length - 1] * Atmosphere.SpeedOfSound(altitudeMeters) * MsToKnots;
        }

        private static void LogGate(ObjectBase shooter, ObjectBase target, Ammunition ammo, float targetKnots,
            float missileAvgKnots, float original, float reduced, float fraction, string detail)
        {
            string key = shooter.GetInstanceID() + ":" + target.GetInstanceID();
            double now = UnityEngine.Time.timeAsDouble;
            if (LastLog.TryGetValue(key, out double last) && now - last < 20.0)
            {
                return;
            }

            LastLog[key] = now;
            const float nm = 27.559496f;
            if (ModLog.Verbose) ModLog.Event($"t={now:0} RANGE GATE: {shooter.getUIDAndName()} vs {target.getUIDAndName()} " +
                         $"(max ~{targetKnots:0} kt, {ammo._ap._ammunitionFileName} avg ~{missileAvgKnots:0} kt; {detail}): " +
                         $"launch range {original / nm:0.0} nm -> {reduced / nm:0.0} nm ({fraction:P0}).");
        }
    }
}

