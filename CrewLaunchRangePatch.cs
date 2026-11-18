using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Crew-skill air-to-air launch range.
    //
    // Since Sea Power 0.9.0 the game's own kinematic range (ObjectBase.GetKinematicRange) uses the target's real velocity
    // and assumes an evasive target (AmmunitionParameters.AssumeEvasiveTarget), so it is a realistic launch range. Crew
    // skill moves it: green crews fire early, toward the missile's published maximum (the label range), so their shots are
    // easy to outrun or out-turn; veterans hold for a closer, higher-probability shot (see CrewSkillModel). A pressure
    // shot (fired to force a shooter defensive) always goes at the label range.
    //
    // Applies to air-to-air missiles fired by aircraft at aircraft, for every caller that decides when to fire or how far
    // to close (AI auto-engage, the hardpoint launch check, radar on/off timing, pursuit). The UI range preview calls with
    // usePreciseKinematics=true and is left untouched.
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.GetMaxEngagementRanges))]
    public static class CrewLaunchRangePatch
    {
        private const float NmToUnity = 27.559496f;
        private const float VicinityFactor = 1.5f;   // targets beyond this multiple of the label range are left alone
        private const double LogEverySec = 20.0;     // per shooter/target pair
        private const int MaxFailures = 25;

        private static int _failures;
        private static bool _disabled;
        private static readonly Dictionary<string, double> LastLog = new Dictionary<string, double>();

        public static void Postfix(ObjectBase __instance, Ammunition ammo, ObjectBase targetObject,
            bool usePreciseKinematics, ref ObjectBase.EngagementRange __result)
        {
            float kinematic = __result.KinematicRange;
            if (_disabled || usePreciseKinematics || kinematic <= 0f || kinematic >= float.MaxValue / 2f)
            {
                return;
            }

            try
            {
                if (targetObject == null || ammo?._ap == null || __instance == null || ammo._ap._type != Ammunition.Type.Missile
                    || !__instance.IsAirUnit || !targetObject.IsAirUnit || !(targetObject is Aircraft))
                {
                    return;
                }

                float label = ammo._ap._launchRangesInUnity.y;
                float vicinity = Math.Max(kinematic, label) * VicinityFactor;
                if ((targetObject.transform.position - __instance.transform.position).sqrMagnitude > vicinity * vicinity)
                {
                    return;
                }

                CrewSkillModel.Profile p = CrewSkillModel.For(__instance);
                bool pressure = PressureShot.IsActive(__instance, targetObject, out ObjectBase victim);
                float early = pressure ? 1f : p.EarlyToLabel;
                float range = kinematic;
                if (early > 0f && label > kinematic)
                {
                    range = Mathf.Lerp(kinematic, label, early);
                }
                else if (early <= 0f && p.HoldFraction < 1f)
                {
                    range = kinematic * p.HoldFraction;
                }

                if (Mathf.Abs(range - kinematic) < 0.01f * kinematic)
                {
                    return;
                }

                __result.KinematicRange = range;
                if (ModLog.Verbose)
                {
                    string why = pressure
                        ? "pressure shot (defending " + victim.getUIDAndName() + "): fire at label range"
                        : range > kinematic
                            ? $"crew {CrewSkillModel.SkillName(__instance)}: fires early toward label range {label / NmToUnity:0.0} nm"
                            : $"crew {CrewSkillModel.SkillName(__instance)}: holds for a closer shot";
                    Log(__instance, targetObject, ammo, kinematic, range, why);
                }
            }
            catch (Exception ex)
            {
                if (++_failures >= MaxFailures)
                {
                    _disabled = true;
                    ModLog.Error("CrewLaunchRangePatch disabled after repeated failures.", ex);
                }
                else
                {
                    ModLog.Error("CrewLaunchRangePatch.Postfix failed.", ex);
                }
            }
        }

        private static void Log(ObjectBase shooter, ObjectBase target, Ammunition ammo, float kinematic, float range, string why)
        {
            string key = shooter.GetInstanceID() + ":" + target.GetInstanceID();
            double now = GameTime.missionElapsedTime;
            if (LastLog.TryGetValue(key, out double last) && now - last < LogEverySec)
            {
                return;
            }

            LastLog[key] = now;
            ModLog.Event($"t={now:0} LAUNCH RANGE: {shooter.getUIDAndName()} vs {target.getUIDAndName()} {ammo._ap._ammunitionFileName}: " +
                         $"game range {kinematic / NmToUnity:0.0} nm -> {range / NmToUnity:0.0} nm ({range / kinematic:P0}), {why}.");
        }
    }
}
