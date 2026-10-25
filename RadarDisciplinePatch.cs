using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SeaPower;

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

            float engageRange = __instance.EngagementRangeForObject(target);
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
