using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Notch vs. mid-course radar guidance.
    //
    // The vanilla game only drops a launcher-guided missile's connection when the guiding radar is off, the target
    // is invalid, or the radar is not aimed at it; it never considers the target being lost in the Doppler notch.
    // So a notching target kept receiving updates. Here, while the launching aircraft's own radar cannot see the
    // target because it is notched (NotchModel), we force Missile.ConnectionLost every frame. The missile then
    // dead-reckons from the last track it had (the game's own _lostGuidanceTime extrapolation: last known position +
    // last known velocity * time since loss), i.e. it coasts on a straight line, until the radar sees the target
    // again (the notch is broken), at which point vanilla guidance resumes by itself.
    //
    // Limits:
    //  - Only pre-pitbull missiles (the seeker's own notch is handled by NotchContextSeeker).
    //  - Only the shooter's own radar is evaluated; a third-party datalink source (AWACS) is not modelled.
    //  - Vanilla still sets ConnectionLostForever after 10 s for missiles that cannot finish on their own seeker
    //    (semi-active), which is the realistic outcome for those.
    [HarmonyPatch(typeof(Missile), "OnUpdateEveryFrame")]
    public static class MissileNotchLockPatch
    {
        public static readonly bool Enabled = true;

        // When we first forced the loss (mission seconds), per missile; also the dead-reckoning clock origin.
        private static readonly Dictionary<Missile, double> LostSince = new Dictionary<Missile, double>();
        private static double _nextPrune;
        private const double PruneIntervalSec = 30.0;

        // True while the notch is holding this missile's mid-course link down.
        public static bool IsForcedLost(Missile m) => m != null && LostSince.ContainsKey(m);

        private static void Postfix(Missile __instance)
        {
            try
            {
                Missile m = __instance;
                if (!Enabled || m == null || !m.isLaunched())
                {
                    return;
                }

                double now = GameTime.missionElapsedTime;
                if (now >= _nextPrune)
                {
                    _nextPrune = now + PruneIntervalSec;
                    Prune();
                }

                SensorSystemRadar radar = m._guidingRadarSystem;
                ObjectBase target = m.CurrentIntendedTargetObject;
                bool eligible = !m.IsDestroyed
                    && !m.ConnectionLostForever.Value
                    && !m.GroupWingman.Value
                    && m._seeker != null && !m._seeker.isActive()
                    && m._ap._midCourseCorrection.AllowsLauncherGuidance
                    && radar != null && radar._baseObject != null && target is Aircraft;

                bool notched = false;
                if (eligible)
                {
                    NotchModel.RadarClass cls = NotchModel.Classify(radar._systemName);
                    notched = NotchModel.IsNotched(radar._baseObject, radar._systemName, cls, radar.SensorPosition, target);
                }

                if (notched)
                {
                    if (!LostSince.TryGetValue(m, out double since))
                    {
                        since = now;
                        LostSince[m] = since;
                        ModLog.Event("NOTCH-LOCK: " + m.getUIDAndName() + " loses mid-course track on " + target.getUIDAndName()
                            + " (guide " + radar._baseObject.getUIDAndName() + " [" + radar._systemName + "] notched); coasting on last track.");
                    }

                    // The vanilla update just cleared the flag and refreshed the loss clock; put both back.
                    m.ConnectionLost.Value = true;
                    m._lostGuidanceTime = since;
                }
                else if (LostSince.Remove(m))
                {
                    ModLog.Event("NOTCH-LOCK: " + m.getUIDAndName() + " guidance restored.");
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("MissileNotchLockPatch", ex);
            }
        }

        private static void Prune()
        {
            List<Missile> dead = null;
            foreach (Missile m in LostSince.Keys)
            {
                if (m == null || m.IsDestroyed)
                {
                    (dead ?? (dead = new List<Missile>())).Add(m);
                }
            }

            if (dead != null)
            {
                foreach (Missile m in dead)
                {
                    LostSince.Remove(m);
                }
            }
        }
    }

    // A forced link loss must not silence the RWR: the guiding radar is still emitting, so the target keeps getting
    // the warning as long as that radar is on and the missile is still intended for it.
    [HarmonyPatch(typeof(Missile), nameof(Missile.IsGivingRWRWarning))]
    public static class MissileNotchLockRwrPatch
    {
        private static void Postfix(Missile __instance, ObjectBase target, ref bool __result)
        {
            if (__result || !MissileNotchLockPatch.IsForcedLost(__instance))
            {
                return;
            }

            SensorSystemRadar radar = __instance._guidingRadarSystem;
            if (radar != null && radar.IsOn.Value && __instance.CurrentIntendedTargetObject == target)
            {
                __result = true;
            }
        }
    }
}
