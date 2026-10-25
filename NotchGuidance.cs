using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Notch-maneuver guidance: flies an aircraft to the heading (and a low enough altitude) that puts a chosen
    // emitter's radar in its Doppler notch - see NotchModel in RadarNotchPatch.cs for the rule being satisfied:
    //     | v_self . los_unit | < half-width     and     self at least LookDownMarginFt below the emitter.
    // The radial component of our velocity along the line of sight is zero when we fly exactly perpendicular to it
    // ("beam"), independent of speed, so the solution is: heading = LOS +/- 90 deg, speed held (energy is kept), and
    // descend below the emitter. The LOS rotates as we turn, so the beam heading is re-solved every tick.
    //
    // Started from the map right-click "Orders > Notch" menu or the bottom-bar "NOTCH" button (see NotchUi.cs).
    // Ends when the emitter dies or stops radiating, or the player issues a new waypoint / cease-fire / order (like an
    // ordered engagement); the jet then resumes its waypoints.
    public static class NotchGuidance
    {
        private const double LogSeconds = 2.0;           // NOTCH-GUIDE log throttle
        private const float BeamOffsetDeg = 90f;         // maximum heading offset from the line of sight (pure beam)
        private const float MinOffsetDeg = 45f;          // never point closer to the emitter than this
        private const float ClosureFraction = 0.7f;      // offensive: aim for this fraction of the notch half-width as residual closure
        private const float DefensiveOpenFraction = 0.25f; // defensive: aim for this fraction of the half-width as residual opening (near-perfect notch)
        private const float MinEmitterSpeedKt = 100f;    // below this the emitter's direction of flight is not used
        private const float AimDistanceNm = 20f;         // how far along the beam heading the steering point is placed
        private const float AltitudeBufferFt = 400f;     // extra depth below the emitter beyond the notch margin
        private const float MinAltitudeFt = 500f;        // never descend below this for a notch

        private class Entry
        {
            public ObjectBase Emitter;
            public float Side;               // +1 = beam to the right of the LOS, -1 = left; fixed once chosen
            public double NextLog;
            public bool OverrideTaken;       // previous motion-controller state captured
            public bool PrevOverride;
            public float PrevAltitude;
            public float HoldAltitude = -1f;
        }

        private static readonly Dictionary<Aircraft, Entry> Active = new Dictionary<Aircraft, Entry>();

        public static bool IsActive(Aircraft ac) => ac != null && Active.ContainsKey(ac);

        // Strictest (smallest) half-width among the emitter's active look-down radars, or 0 if it cannot be notched.
        public static float EmitterHalfWidthKt(ObjectBase emitter)
        {
            if (emitter == null || emitter.IsDestroyed)
            {
                return 0f;
            }

            if (emitter is WeaponBase)
            {
                return NotchModel.Seeker.HalfWidthKt;
            }

            if (!(emitter is Aircraft) || emitter._obp == null)
            {
                return 0f;
            }

            float best = 0f;
            Consider(emitter._obp._searchRadarSystems, ref best);
            Consider(emitter._obp._targetingRadarSystems, ref best);
            return best;
        }

        private static void Consider(List<SensorSystemRadar> radars, ref float best)
        {
            if (radars == null)
            {
                return;
            }

            foreach (SensorSystemRadar r in radars)
            {
                if (r == null || r._radar == null || r.Inoperable.Value || !r.IsOn.Value)
                {
                    continue;
                }

                float hw = NotchModel.Classify(r._radar._rp?._systemName).HalfWidthKt;
                if (hw > 0f && (best <= 0f || hw < best))
                {
                    best = hw;
                }
            }
        }

        // Beam side to fly (fixed by the caller once chosen so the jet never reverses):
        //  - offensive notch of a moving aircraft: the side running the same way the emitter flies, so the slight
        //    closure angle in BeamAim keeps us gaining on it instead of falling behind;
        //  - defensive notch (or a near-stationary emitter): the side needing the smaller turn.
        public static float ChooseSide(Aircraft ac, ObjectBase emitter, bool preferEmitterDirection)
        {
            Vector3 los = emitter.transform.position - ac.transform.position;
            Vector3 beamR = Quaternion.AngleAxis(BeamOffsetDeg, Vector3.up) * new Vector3(los.x, 0f, los.z);
            Vector3 emitterVel = emitter.UnityVelocityVector;
            emitterVel.y = 0f;
            if (preferEmitterDirection && emitterVel.magnitude * NotchModel.KtPerUnitSpeed > MinEmitterSpeedKt)
            {
                return Vector3.Dot(beamR, emitterVel) >= 0f ? 1f : -1f;
            }

            Vector3 velH = ac.UnityVelocityVector;
            velH.y = 0f;
            if (velH.sqrMagnitude < 1e-6f)
            {
                velH = ac.transform.forward;
                velH.y = 0f;
            }

            float toRight = Mathf.Abs(Vector3.SignedAngle(velH, beamR, Vector3.up));
            float toLeft = Mathf.Abs(Vector3.SignedAngle(velH, -beamR, Vector3.up));
            return toRight <= toLeft ? 1f : -1f;
        }

        // Steering point for the notch heading, from an offset angle to the line of sight (LOS):
        //  - offensive: radial speed = V*cos(offset), so aim for ClosureFraction of the (chaff-widened) half-width of
        //    residual CLOSURE: inside the notch but still gaining on the target (offset 45-90 deg);
        //  - defensive: a near-perfect notch, slightly OPENING (offset just past 90 deg) so range grows, never closes.
        public static Vector3 BeamAim(Aircraft ac, ObjectBase emitter, float side, float halfWidthKt, bool defensive, out float heading)
        {
            Vector3 me = ac.transform.position;
            Vector3 los = emitter.transform.position - me;
            Vector3 losH = new Vector3(los.x, 0f, los.z);
            if (losH.sqrMagnitude < 1e-4f)
            {
                losH = ac.transform.forward;
            }

            Vector3 velH = ac.UnityVelocityVector;
            velH.y = 0f;
            float speedKt = velH.magnitude * NotchModel.KtPerUnitSpeed;
            float offsetDeg;
            if (defensive)
            {
                float openKt = DefensiveOpenFraction * halfWidthKt;
                offsetDeg = BeamOffsetDeg + (speedKt > openKt ? Mathf.Asin(openKt / speedKt) * Mathf.Rad2Deg : 0f);
            }
            else
            {
                float wantRadialKt = ClosureFraction * halfWidthKt * NotchModel.ChaffBloomFactor(ac);
                offsetDeg = speedKt > wantRadialKt ? Mathf.Acos(wantRadialKt / speedKt) * Mathf.Rad2Deg : 0f;
                offsetDeg = Mathf.Clamp(offsetDeg, MinOffsetDeg, BeamOffsetDeg);
            }

            Vector3 beam = Quaternion.AngleAxis(side * offsetDeg, Vector3.up) * losH;
            heading = Mathf.Repeat(Mathf.Atan2(beam.x, beam.z) * Mathf.Rad2Deg, 360f);
            return me + beam.normalized * AimDistanceNm * NotchModel.NmToUnity;
        }

        // Altitude (unity y) to fly: the current one if already far enough below the emitter, else below it by the
        // notch margin plus a buffer (never lower than the floor).
        public static float NotchAltitude(Aircraft ac, ObjectBase emitter)
        {
            float y = ac.transform.position.y;
            float marginUnity = NotchModel.LookDownMarginFt / NotchModel.UnityToFt;
            if (emitter.transform.position.y - y >= marginUnity + AltitudeBufferFt / NotchModel.UnityToFt * 0.5f)
            {
                return y;
            }

            float wanted = emitter.transform.position.y - (NotchModel.LookDownMarginFt + AltitudeBufferFt) / NotchModel.UnityToFt;
            return Mathf.Max(wanted, MinAltitudeFt / NotchModel.UnityToFt);
        }

        // Same test as NotchModel.IsNotched, minus the dwell time and the chaff bloom unless the caller widens halfWidthKt.
        public static bool InNotch(Aircraft ac, ObjectBase emitter, float halfWidthKt, out float radialKt, out float belowFt)
        {
            Vector3 los = emitter.transform.position - ac.transform.position;
            radialKt = Mathf.Abs(Vector3.Dot(ac.UnityVelocityVector, los.normalized)) * NotchModel.KtPerUnitSpeed;
            belowFt = los.y * NotchModel.UnityToFt;   // positive when the emitter is above us (we are in its look-down)
            return radialKt < halfWidthKt * NotchModel.ChaffBloomFactor(ac) && belowFt >= NotchModel.LookDownMarginFt;
        }

        public static void Start(Aircraft ac, ObjectBase emitter)
        {
            if (ac == null || ac.IsDestroyed || emitter == null || ac == emitter)
            {
                return;
            }

            Active[ac] = new Entry { Emitter = emitter };
            ModLog.Event($"NOTCH-GUIDE start {ac.getUIDAndName()} -> emitter {emitter.getUIDAndName()} halfWidth={EmitterHalfWidthKt(emitter):F0}kt");
            ac.ShowOrderFeedback("Notching " + emitter.getUIDAndName());
        }

        public static void Stop(Aircraft ac, string why)
        {
            if (ac != null && Active.TryGetValue(ac, out Entry e))
            {
                Active.Remove(ac);
                if (e.OverrideTaken && ac.Motioncontroller != null)
                {
                    ac.Motioncontroller.commandPositionOverride = e.PrevOverride;
                    ac.DesiredAltitude.Value = e.PrevAltitude;
                }

                ModLog.Event($"NOTCH-GUIDE end {ac.getUIDAndName()}: {why}");
            }
        }

        public static void Tick(Aircraft ac)
        {
            if (!Active.TryGetValue(ac, out Entry e))
            {
                return;
            }

            double now = GameTime.missionElapsedTime;

            try
            {
                if (ac.IsDestroyed)
                {
                    Stop(ac, "aircraft destroyed");
                    return;
                }

                if (e.Emitter == null || e.Emitter.IsDestroyed)
                {
                    Stop(ac, "emitter gone");
                    return;
                }

                float halfWidth = EmitterHalfWidthKt(e.Emitter);
                if (halfWidth <= 0f)
                {
                    Stop(ac, "emitter no longer radiating");
                    return;
                }

                bool defensive = e.Emitter is WeaponBase;

                Vector3 me = ac.transform.position;
                Vector3 los = e.Emitter.transform.position - me;
                Vector3 losH = new Vector3(los.x, 0f, los.z);
                if (losH.sqrMagnitude < 1e-4f)
                {
                    return;
                }

                Vector3 vel = ac.UnityVelocityVector;
                Vector3 velH = new Vector3(vel.x, 0f, vel.z);
                if (velH.sqrMagnitude < 1e-6f)
                {
                    velH = ac.transform.forward;
                    velH.y = 0f;
                }

                if (e.Side == 0f)
                {
                    e.Side = ChooseSide(ac, e.Emitter, !defensive);
                }

                Vector3 aim = BeamAim(ac, e.Emitter, e.Side, halfWidth, defensive, out float heading);

                // Same mechanism the game's missile-evasion state uses: with the override set the flight model steers
                // at Motioncontroller.CommandPosition and ignores the waypoint list (left untouched, so the waypoints
                // resume when the maneuver ends). Re-asserted every physics tick because other states may clear it.
                MotionController mc = ac.Motioncontroller;
                if (mc == null)
                {
                    return;
                }

                if (!e.OverrideTaken)
                {
                    e.PrevOverride = mc.commandPositionOverride;
                    e.PrevAltitude = ac.DesiredAltitude.Value;
                    e.OverrideTaken = true;
                }

                mc.commandPositionOverride = true;
                mc.CommandPosition = aim;

                // Hold altitude (waypoint tasks would otherwise keep climbing/descending), going below the emitter if needed
                // so its radar is looking down at us (clutter exists only for look-down).
                if (e.HoldAltitude < 0f)
                {
                    e.HoldAltitude = NotchAltitude(ac, e.Emitter);
                }

                ac.DesiredAltitude.Value = e.HoldAltitude;

                if (now >= e.NextLog)
                {
                    e.NextLog = now + LogSeconds;
                    float rangeNm = los.magnitude / NotchModel.NmToUnity;
                    bool inNotch = InNotch(ac, e.Emitter, halfWidth, out float radialKt, out float belowFt) && rangeNm > NotchModel.MinNotchRangeNm;
                    ModLog.Info($"NOTCH-GUIDE {ac.getUIDAndName()} vs {e.Emitter.getUIDAndName()} range={rangeNm:F1}nm radial={radialKt:F0}kt (limit {halfWidth:F0}) below={belowFt:F0}ft hdg={heading:F0} inNotch={inNotch}");
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("NotchGuidance.Tick", ex);
                Stop(ac, "error");
            }
        }
    }

    // Player orders that end a notch (new waypoint, waypoint removal, cease-fire / any setOrder).
    [HarmonyPatch]
    public static class NotchCancelPatch
    {
        private static readonly string[] Names = { "setOrder", "setWaypointTask", "SetWaypointTask", "RemoveWaypoints" };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in Names)
            {
                foreach (MethodInfo m in typeof(ObjectBase).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name)
                    {
                        yield return m;
                    }
                }
            }
        }

        private static void Postfix(ObjectBase __instance, MethodBase __originalMethod)
        {
            if (__instance is Aircraft ac && NotchGuidance.IsActive(ac))
            {
                NotchGuidance.Stop(ac, "player order " + __originalMethod.Name);
            }
        }
    }

    [HarmonyPatch(typeof(Aircraft), "OnFixedUpdate")]
    public static class NotchGuidanceTickPatch
    {
        private static void Postfix(Aircraft __instance)
        {
            NotchGuidance.Tick(__instance);
        }
    }
}
