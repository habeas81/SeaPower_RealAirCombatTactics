using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Keep fighter search radars silent while closing for an air engagement if datalink-quality track exists.
    [HarmonyPatch(typeof(AI), "AutoAttackOpponentInRange")]
    public static class RadarDisciplinePatch
    {
        private static readonly Dictionary<int, double> LastLog = new Dictionary<int, double>();
        private static readonly Dictionary<int, double> LastToggle = new Dictionary<int, double>();
        private const double MinToggleSec = 20.0;      // minimum time between radar on/off changes per aircraft
        private const float GateReleaseFactor = 1.25f; // radar stays on until the target is this far beyond the gate
        private const float RadarOnMargin = 1.3f;      // radar comes on this multiple of the missile range out
        private const float LockTimeSec = 15f;         // assumed radar detect-and-lock time before a semi-active shot
        private const float DetectionBufferSec = 60f;  // extra seconds of radar-on time before the launch point

        private static void Postfix(AI __instance, ObjectBase ____baseObject)
        {
            if (!(____baseObject is Aircraft aircraft) || aircraft._obp == null || aircraft._obp._airSearchRadars.Count == 0)
            {
                return;
            }

            ObjectBase target = __instance._objectToDestroy;
            if (target == null || target.IsDestroyed || !target.isAirTarget() || target.isWeapon())
            {
                return;
            }

            Vehicle track = target.GetMapVehicle(aircraft._taskforce);
            if (track == null || !track.KnownUnityPosition.HasValue)
            {
                return;
            }

            if (IsGuidingWeapon(aircraft) || IsDefendingMissile(aircraft))
            {
                return;
            }

            int id = aircraft.GetInstanceID();
            double now = GameTime.missionElapsedTime;

            // The silent approach is only valid while a FRESH datalink/AWACS track exists. If the track has gone
            // stale (e.g. the target is notching the AWACS) the radar is needed to find it again, so hand control
            // back to vanilla (which turns it on); extrapolated positions do not count as a track.
            if (!track.HasLiveTrack)
            {
                if (!aircraft.IsAirSearchRadarsOn.Value)
                {
                    aircraft.EnableAirSearchRadars();
                    LastToggle[id] = now;
                    Log(aircraft, $"EMCON: air-search radar ON - no live offboard track on {target.getUIDAndName()}.");
                }

                return;
            }

            float engageRange = RadarOnRange(aircraft, __instance, target, out float sensorRange);
            if (engageRange <= 0f)
            {
                return;
            }

            float distance = (target.transform.position - aircraft.transform.position).magnitude;

            // Hysteresis: radar comes on inside the gate, and only goes back off well outside it, and never more
            // often than MinToggleSec - otherwise the emitter flickers at the gate boundary.
            bool withinRange = distance <= engageRange;
            bool farOutside = distance > engageRange * GateReleaseFactor;
            bool settled = !LastToggle.TryGetValue(id, out double lastToggle) || now - lastToggle >= MinToggleSec;

            if (withinRange && ModLog.Verbose)
            {
                Log(aircraft, $"EMCON-STATE: radar {(aircraft.IsAirSearchRadarsOn.Value ? "ON" : "OFF")} vs {target.getUIDAndName()} dist {Nm(distance):0.0} nm, radar-on range {Nm(engageRange):0.0} nm, sensor range {Nm(sensorRange):0.0} nm.");
            }

            if (farOutside && settled && aircraft.IsAirSearchRadarsOn.Value)
            {
                aircraft.DisableAirSearchRadars();
                LastToggle[id] = now;
                Log(aircraft, $"EMCON: air-search radar OFF while closing on {target.getUIDAndName()} ({Nm(distance):0.0} nm > gate {Nm(engageRange):0.0} nm).");
                return;
            }

            if (withinRange && !aircraft.IsAirSearchRadarsOn.Value)
            {
                aircraft.EnableAirSearchRadars();
                LastToggle[id] = now;
                Log(aircraft, $"EMCON: air-search radar ON for engagement vs {target.getUIDAndName()} ({Nm(distance):0.0} nm <= gate {Nm(engageRange):0.0} nm).");
            }
        }

        // Range inside which the radar must be on: the longest missile kinematic range against this target, with margin
        // so the radar has time to acquire and lock before the launch point. The vanilla engagement range is min(kinematic,
        // sensor, terrain) and collapses while the radar is off, which would keep a silent shooter silent until too late.
        private static float RadarOnRange(Aircraft aircraft, AI ai, ObjectBase target, out float sensorRange)
        {
            float best = -1f;
            bool hasActiveSeeker = false;
            sensorRange = -1f;
            foreach (string name in ai.AmmoForTarget(target, includeSecondary: false))
            {
                if (!aircraft.AmmunitionAmountDictionary.TryGetValue(name, out int count) || count < 1
                    || !aircraft.AmmunitionNameToAmmunitionDictionary.TryGetValue(name, out Ammunition ammo))
                {
                    continue;
                }

                if (ammo._ap.GuidanceTypeForTarget(target) == AmmunitionParameters.GuidanceType.ActiveRadarHoming)
                {
                    hasActiveSeeker = true;
                }

                ObjectBase.EngagementRange r = aircraft.GetMaxEngagementRanges(target, ammo, false);
                if (r.KinematicRange > best)
                {
                    best = r.KinematicRange;
                    sensorRange = r.SensorRange;
                }
            }

            if (best <= 0f)
            {
                return best;
            }

            float onRange = best * RadarOnMargin;
            if (!hasActiveSeeker)
            {
                // No fire-and-forget missile: the shooter's own radar must detect, track and illuminate the target
                // before the shot, so it has to be on early enough to cover lock time plus a detection buffer,
                // even when the target's small RCS means the radar only sees it late.
                Vector3 toTarget = target.transform.position - aircraft.transform.position;
                float dist = toTarget.magnitude;
                if (dist > 1f)
                {
                    float closing = Vector3.Dot(aircraft.UnityVelocityVector - target.UnityVelocityVector, toTarget / dist);
                    float lead = Mathf.Max(closing, 0f) * (LockTimeSec + DetectionBufferSec);
                    onRange = Mathf.Max(onRange, best + lead);
                }
            }

            return onRange;
        }

        private static bool IsGuidingWeapon(Aircraft aircraft)
        {
            return aircraft._alignmentTasks != null && aircraft._alignmentTasks.Any(a => a != null && a._isGuidingWeapon);
        }

        private static bool IsDefendingMissile(ObjectBase aircraft)
        {
            if (aircraft._incomingWeapons == null)
            {
                return false;
            }

            foreach (WeaponBase w in aircraft._incomingWeapons)
            {
                Missile m = w as Missile;
                if (m == null || m.IsDestroyed || m.CurrentIntendedTargetObject != aircraft || m._targetLost || m.IsUnguidableOrphan)
                {
                    continue;
                }

                if (m._launchPlatform != null && m._launchPlatform._taskforce != aircraft._taskforce)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Log(ObjectBase unit, string message)
        {
            double now = GameTime.missionElapsedTime;
            int id = unit.GetInstanceID();
            if (LastLog.TryGetValue(id, out double last) && now - last < 8.0)
            {
                return;
            }

            LastLog[id] = now;
            ModLog.Event($"t={now:0} {message}");
        }

        private static float Nm(float unity)
        {
            return unity / 27.559496f;
        }
    }
}
