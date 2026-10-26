using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Shared lead-pursuit math. Uses the engager's actual speed and the target's actual velocity,
    // so it is correct for slow targets (helicopters) and for cruising fighters alike.
    public static class LeadCore
    {
        public const float ThreeNm = 82.67849f;
        private const float NmToUnity = 27.559496f;
        // Remaining fuel range (nm; RangeOnMap is in unity units) needed to allow afterburner / any speed management.
        private const float AfterburnerMinRangeNm = 150f;
        private const float MinRangeNm = 40f;
        // Keep the nose this many degrees inside the weapon's firing-arc edge when leading close to the shot.
        private static readonly System.Collections.Generic.HashSet<string> LoggedArcs = new System.Collections.Generic.HashSet<string>();
        private const float ArcMarginDeg = 5f;
        // The lead limit ramps in over this fraction of range beyond the weapon's max launch range (10% = last ~9 nm of a 95 nm shot).
        private const float ArcRampFraction = 0.10f;
        private const float MinSpeed = 20f * 0.0076554087f;
        private const float MaxClimbTan = 0.1405f; // ~8 deg: keeps the lead point from dragging the jet up and down

        // Rear-aspect-only (old Fox 2) weapons: lead on the way in, then vanilla stern placement near the envelope.
        public static bool ShouldHandOverToVanilla(AmmunitionParameters ap, float distance)
        {
            return ap != null && ap._isRearAspectOnly && distance < Mathf.Max(ap._launchRangesInUnity.y * 2.5f, ThreeNm * 2f);
        }

        // A collision course against a near-equal-speed target moving away needs a huge lead and never closes
        // in practice, so the lead is capped; beyond the cap the fighter closes with speed instead.
        private const float MaxLeadDeg = 35f;
        private const float DefenderFilterTau = 25f;   // velocity filter (s) used while the target is running its missile defence

        private class Smoothed
        {
            public Vector3 Velocity;
            public double Time = -1.0;
        }

        private static readonly ConditionalWeakTable<ObjectBase, Smoothed> Filters = new ConditionalWeakTable<ObjectBase, Smoothed>();

        // Low-pass of the target velocity (8 s far out, 4 s close in) so jinking / turning targets don't whip the lead point around.
        // Time constant shrinks with time-to-target (8 s far out -> 4 s near launch) so the lead reacts faster when it matters.
        private static float FilterTau(float timeToTarget)
        {
            return Mathf.Lerp(4f, 8f, Mathf.InverseLerp(60f, 300f, timeToTarget));
        }

        private static Vector3 SmoothedVelocity(ObjectBase target, float tau)
        {
            Smoothed s = Filters.GetOrCreateValue(target);
            double now = GameTime.missionElapsedTime;
            Vector3 raw = target._velocityVecInUnity;
            if (s.Time < 0.0 || now < s.Time || now - s.Time > 30.0)
            {
                s.Velocity = raw;
                s.Time = now;
            }
            else if (now - s.Time >= 0.5)
            {
                float alpha = 1f - Mathf.Exp(-(float)(now - s.Time) / tau);
                s.Velocity = Vector3.Lerp(s.Velocity, raw, alpha);
                s.Time = now;
            }

            return s.Velocity;
        }

        // Smoothed target velocity for other modules (e.g. deciding whether a target is running away).
        public static Vector3 FilteredVelocity(ObjectBase target)
        {
            return SmoothedVelocity(target, FilterTau(1000f));
        }

        // Military-power speed index (the last entry is afterburner when the table has more than 4 entries).
        public static int MilitaryIndex(Aircraft ac)
        {
            int n = ac.Ap._speedValuesInMach.Length;
            return n > 4 ? n - 2 : n - 1;
        }

        // Closing speed (horizontal) along the line of sight, unity units/s. Positive = range decreasing.
        private static float ClosingSpeed(Aircraft ac, ObjectBase target)
        {
            Vector3 los = target.transform.position - ac.transform.position;
            los.y = 0f;
            if (los.sqrMagnitude < 1e-4f)
            {
                return 0f;
            }

            Vector3 rel = ac._velocityVecInUnity - target._velocityVecInUnity;
            rel.y = 0f;
            return Vector3.Dot(rel, los.normalized);
        }

        // Desired speed index: military power normally; afterburner (if fitted and fuel allows) when the tail chase
        // is not closing (equal-speed targets running away).
        public static int DesiredIndex(Aircraft ac, ObjectBase target)
        {
            int mil = MilitaryIndex(ac);
            int top = ac.Ap._speedValuesInMach.Length - 1;
            bool hasAfterburner = top > mil;
            bool fuelOk = ac.RangeOnMap.Value > AfterburnerMinRangeNm * NmToUnity;
            float closingKt = ClosingSpeed(ac, target) / 0.0076554087f;
            return hasAfterburner && fuelOk && closingKt < 60f ? top : mil;
        }

        public static void EnsureClosureSpeed(Aircraft ac, ObjectBase target)
        {
            if (!ac.AllowCombatSpeedControl || ac.RangeOnMap.Value < MinRangeNm * NmToUnity)
            {
                return;
            }

            int index = DesiredIndex(ac, target);
            // _telegraph can already equal the target index while the real speed command is the mission's initial one,
            // so compare the actual command as well.
            float wantMach = ac.Ap._speedValuesInMach[index];
            ISpeedCommand cmd = ac.SpeedCommand.Value;
            bool commandMatches = cmd is ConstantMach && Mathf.Abs(cmd.SpeedInMach - wantMach) < 0.001f;
            if (ac.getTelegraph() != index || !commandMatches)
            {
                ac.setTelegraph(index + 1);
            }
        }
        // Minimum length of the arc ramp so the heading can settle at high closure rates.
        private const float RampMinNm = 8f;

        private class RangeCache
        {
            public double Time = -10.0;
            public ObjectBase Target;
            public float Range;
        }

        private static readonly ConditionalWeakTable<Aircraft, RangeCache> RangeCaches = new ConditionalWeakTable<Aircraft, RangeCache>();

        // Speed-aware kinematic launch range (as gated by LaunchRangeGatePatch), cached ~1 s; falls back to catalogue max.
        public static float EffectiveRange(Aircraft ac, ObjectBase target, AmmunitionParameters ap)
        {
            RangeCache c = RangeCaches.GetOrCreateValue(ac);
            double now = GameTime.missionElapsedTime;
            if (c.Target == target && now >= c.Time && now - c.Time < 1.0)
            {
                return c.Range;
            }

            float range = ap._launchRangesInUnity.y;
            try
            {
                Ammunition ammo = ac.getAmmunitionByName(ap._ammunitionFileName);
                if (ammo != null)
                {
                    float k = ac.GetMaxEngagementRanges(target, ammo, false).KinematicRange;
                    if (k > ThreeNm)
                    {
                        range = Mathf.Min(k, range);
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Error("EffectiveRange", ex);
            }

            c.Target = target;
            c.Time = now;
            c.Range = range;
            return range;
        }

        public static bool TryCompute(Aircraft ac, ObjectBase target, AmmunitionParameters ap, out Vector3 point)
        {
            point = default;
            if (ac == null || target == null || !target.isAirTarget() || target.isWeapon())
            {
                return false;
            }

            Vector3 me = ac.transform.position;
            Vector3 toTarget = target.transform.position - me;
            if (toTarget.magnitude < ThreeNm)
            {
                return false;
            }

            float speed = ac._velocityVecInUnity.magnitude;
            if (speed < MinSpeed)
            {
                return false;
            }

            // Plan with the speed we are commanding (not the current one) while the aircraft accelerates to it.
            int idx = DesiredIndex(ac, target);
            float alt = Mathf.Clamp(target.transform.position.y, 4.53571f, ac.Ap._ceiling);
            float mach = Mathf.Min(ac.Ap._speedValuesInMach[idx], ac.Ap._machLimitCurve.Evaluate(alt * 220.47266f));
            float planned = mach * Atmosphere.SpeedOfSound(alt * 67.200066f) * 0.0148809375f;
            speed = Mathf.Max(speed, Mathf.Min(planned, speed * 1.25f));

            float timeToTarget = toTarget.magnitude / Mathf.Max(ClosingSpeed(ac, target), 0.01f);
            // A target that is weaving to defeat a missile would drag the lead point back and forth: use a very long
            // filter so the attacker flies the target's average track instead of mirroring each jink.
            float tau = target is Aircraft defender && MissileDefense.IsActive(defender)
                ? DefenderFilterTau
                : FilterTau(timeToTarget > 0f ? timeToTarget : 1000f);
            Vector3 tv = SmoothedVelocity(target, tau);
            var solution = Utils.CalculateInterceptPosition(me, speed, target.transform.position, tv);
            point = solution.canIntercept ? solution.interceptPosition : target.transform.position;

            // Cap the lead angle (horizontal) relative to the direct bearing.
            Vector3 direct = new Vector3(toTarget.x, 0f, toTarget.z);
            Vector3 lead = new Vector3(point.x - me.x, 0f, point.z - me.z);
            if (direct.sqrMagnitude > 1e-4f && lead.sqrMagnitude > 1e-4f && Vector3.Angle(direct, lead) > MaxLeadDeg)
            {
                Vector3 capped = Vector3.RotateTowards(direct.normalized, lead.normalized, MaxLeadDeg * Mathf.Deg2Rad, 0f) * direct.magnitude;
                point = new Vector3(me.x + capped.x, point.y, me.z + capped.z);
            }

            // Inside the approach window of the assigned weapon, keep the nose within its firing arc: the game checks
            // the arc against the TARGET's bearing (not our lead point), so a big lead would block the shot.
            if (ap != null && direct.sqrMagnitude > 1e-4f)
            {
                float halfArc = FiringHalfArcDeg(ac, ap);
                if (LoggedArcs.Add(ac.getUIDAndName() + ap._ammunitionFileName))
                {
                    ModLog.Event($"ARC: {ac.getUIDAndName()} {ap._ammunitionFileName} firing half-arc {halfArc:0}deg, max launch {ap._launchRangesInUnity.y / NmToUnity:0.0}nm.");
                }
                // Margin is capped at half the arc so a very narrow arc (AIM-120D = 5 deg) still leaves a usable offset.
                float allowed = Mathf.Max(0f, halfArc - Mathf.Min(ArcMarginDeg, halfArc * 0.5f));
                // Ramp the limit in over the last ArcRampFraction before max launch range: full lead outside it,
                // the arc-edge limit at max launch range and inside.
                // The range we actually shoot from is the speed-aware gated kinematic range, not the catalogue max.
                float maxRange = EffectiveRange(ac, target, ap);
                float rampStart = maxRange + Mathf.Max(maxRange * ArcRampFraction, RampMinNm * NmToUnity);
                float ramp = Mathf.InverseLerp(rampStart, maxRange, toTarget.magnitude);
                float limit = Mathf.Lerp(MaxLeadDeg, allowed, ramp);
                Vector3 leadNow = new Vector3(point.x - me.x, 0f, point.z - me.z);
                if (halfArc < 179f && ramp > 0f
                    && leadNow.sqrMagnitude > 1e-4f && Vector3.Angle(direct, leadNow) > limit)
                {
                    Vector3 limited = Vector3.RotateTowards(direct.normalized, leadNow.normalized, limit * Mathf.Deg2Rad, 0f) * direct.magnitude;
                    point = new Vector3(me.x + limited.x, point.y, me.z + limited.z);
                }
            }

            // Altitude is not part of the intercept geometry: hold our own until near the shot, then take the
            // equipment-appropriate altitude relative to the target.
            float desiredY = DesiredAltitudeY(ac, target, ap);
            float horizontal = new Vector2(point.x - me.x, point.z - me.z).magnitude;
            float maxDy = horizontal * MaxClimbTan;
            point.y = me.y + Mathf.Clamp(desiredY - me.y, -maxDy, maxDy);
            ac.DesiredAltitude.Value = desiredY;

            return true;
        }

        private const float FtToUnity = 1f / 220.47266f;

        // Half-width (deg, either side of the nose) of the weapon's firing arc from the game's weapon data; 180 = unrestricted.
        // The arc is a fixed data value, so it is the limit the shot actually needs, whatever the radar gimbal could do.
        public static float FiringHalfArcDeg(Aircraft ac, AmmunitionParameters ap)
        {
            var systems = ac.GetWeaponSystemsForAmmunition(ap._ammunitionFileName);
            float best = 0f;
            if (systems != null)
            {
                foreach (WeaponSystem ws in systems)
                {
                    foreach (Vector2 arc in ws._vwp._firingArcs)
                    {
                        if (arc == Vector2.zero)
                        {
                            return 180f;
                        }

                        // An arc [x, y] that contains the nose direction (0 deg).
                        if (arc.x < 0f && arc.y > 0f)
                        {
                            best = Mathf.Max(best, Mathf.Min(-arc.x, arc.y));
                        }
                    }
                }
            }

            return best > 0f ? best : 180f;
        }

        // Best (highest) look-down multiplier among the aircraft's radars; < 0.6 means no real look-down/shoot-down.
        private static bool LacksLookDown(Aircraft ac)
        {
            float best = -1f;
            var systems = ac._obp?._sensorSystems;
            if (systems == null)
            {
                return false;
            }

            foreach (SensorSystem s in systems)
            {
                if (s is SensorSystemRadar r && r._radar != null && !r.Inoperable.Value)
                {
                    best = Mathf.Max(best, r._lookDownMultiplier);
                }
            }

            return best >= 0f && best < 0.6f;
        }

        public static float DesiredAltitudeY(Aircraft ac, ObjectBase target, AmmunitionParameters ap)
        {
            float own = ac.transform.position.y;
            float horizontal = new Vector2(target.transform.position.x - ac.transform.position.x, target.transform.position.z - ac.transform.position.z).magnitude;
            float shotRange = ap != null ? ap._launchRangesInUnity.y : 10f * 27.559496f;
            if (horizontal > shotRange + 10f * 27.559496f)
            {
                return own;
            }

            float targetFt = target.transform.position.y * 220.47266f;
            float offsetFt;
            if (ap != null && ap._guidanceType == AmmunitionParameters.GuidanceType.IRHoming)
            {
                offsetFt = -300f; // near co-altitude / slightly below: engine plume against cold sky, no ground clutter behind it
            }
            else if (LacksLookDown(ac))
            {
                offsetFt = -1500f; // look up at the target so it is not lost in ground clutter
            }
            else
            {
                offsetFt = targetFt < 1000f ? 500f : 1000f;
            }

            float desiredFt = Mathf.Max(targetFt + offsetFt, 500f);
            return Mathf.Clamp(desiredFt * FtToUnity, 4.53571f * 0.5f, ac.Ap._ceiling);
        }

        public static void Apply(Aircraft ac, ObjectBase target, AlignUnitToTarget alignment)
        {
            // Guiding a weapon: vanilla owns speed (crank / support logic).
            if (alignment != null && alignment._isGuidingWeapon)
            {
                return;
            }

            // Speed is managed on every other path (including vanilla steering handover and "no lead solution"),
            // otherwise vanilla's telegraph-4 pursuit never lights the afterburner in a tail chase.
            if (target.isAirTarget() && !target.isWeapon())
            {
                EnsureClosureSpeed(ac, target);
            }

            if (alignment != null && ShouldHandOverToVanilla(alignment._ap, (target.transform.position - ac.transform.position).magnitude))
            {
                return;
            }

            if (TryCompute(ac, target, alignment?._ap, out Vector3 point))
            {
                ac.Motioncontroller.CommandPosition = point;
                PursuitTelemetry.Arm(ac, target, point);
            }
        }
    }

    // EngageAirContact.Pursuit (AI-selected targets): lead instead of direct pursuit when not guiding.
    [HarmonyPatch(typeof(AircraftStates.EngageAirContact.Pursuit), "onFixedUpdate")]
    public static class LeadPursuitPatch
    {
        private static void Postfix(AircraftStates.EngageAirContact ____parentState, Aircraft ____aircraft)
        {
            AlignUnitToTarget alignment = ____parentState?.currentAlignment;
            if (alignment?._targetObject != null)
            {
                LeadCore.Apply(____aircraft, alignment._targetObject, alignment);
            }
        }
    }

    // Aligning (player- or order-assigned weapon): vanilla steers at the aim position every tick.
    [HarmonyPatch(typeof(AircraftStates.Aligning), "onFixedUpdate")]
    public static class AligningLeadPatch
    {
        private static void Postfix(AircraftStates.Aligning __instance, Aircraft ____aircraft)
        {
            AlignUnitToTarget alignment = __instance.currentAlignment;
            if (alignment?._targetObject != null && ____aircraft.StateMachine.CurrentState == __instance)
            {
                LeadCore.Apply(____aircraft, alignment._targetObject, alignment);
            }
        }
    }

    // MoveToEngage: the long transit to a commanded engagement (e.g. 80-100 nm) pointed straight at the target.
    // Vanilla keeps the aircraft's own altitude and handles dives separately, so we do the same.
    [HarmonyPatch(typeof(AircraftStates.MoveToEngage), "onFixedUpdate")]
    public static class MoveToEngageLeadPatch
    {
        private static void Postfix(Aircraft ____aircraft)
        {
            if (!____aircraft.HasControl)
            {
                return;
            }

            EngageTask task = ____aircraft.GetMoveToEngageTask();
            if (task?._targetObject == null)
            {
                return;
            }

            AmmunitionParameters ap = task._ammunition?._ap;
            if (task._targetObject.isAirTarget() && !task._targetObject.isWeapon())
            {
                LeadCore.EnsureClosureSpeed(____aircraft, task._targetObject);
            }

            if (LeadCore.ShouldHandOverToVanilla(ap, (task._targetObject.transform.position - ____aircraft.transform.position).magnitude))
            {
                return;
            }

            if (LeadCore.TryCompute(____aircraft, task._targetObject, ap, out Vector3 point))
            {
                ____aircraft.Motioncontroller.CommandPosition = point;
                PursuitTelemetry.Arm(____aircraft, task._targetObject, point);
            }
        }
    }

    // Vanilla identify steers at a full-speed intercept with an uncapped lead and sets DesiredAltitude to the
    // intercept altitude, so it bobs up and down. Replace both with the shared lead (capped, smoothed) and altitude policy.
    [HarmonyPatch(typeof(AircraftStates.IdentifyAirContact), "onFixedUpdate")]
    public static class IdentifyLeadPatch
    {
        private static void Postfix(AircraftStates.IdentifyAirContact __instance, Aircraft ____aircraft, ObjectBase ____target)
        {
            if (____target == null || ____aircraft.StateMachine.CurrentState != __instance)
            {
                return;
            }

            if (____target.isAirTarget() && !____target.isWeapon())
            {
                LeadCore.EnsureClosureSpeed(____aircraft, ____target);
            }

            if (LeadCore.TryCompute(____aircraft, ____target, null, out Vector3 point))
            {
                ____aircraft.Motioncontroller.CommandPosition = point;
                PursuitTelemetry.Arm(____aircraft, ____target, point);
            }
        }
    }


    // Trace for every fixed-wing aircraft that has an air target (any side, player or AI):
    // current state, target, and the geometry needed to tell lead from direct pursuit.
    [HarmonyPatch(typeof(FixedWingFlightPhysics), "CalculatePositionAndAltitudeErrors")]
    public static class PursuitTelemetry
    {
        internal const float Nm = 27.559496f;
        internal const float Kt = 0.0076554087f;
        private const double IntervalSeconds = 5.0;

        // The trace hook itself runs every flight-model frame, so only install it when diagnostics are enabled
        // (the static Arm/Describe helpers below are still used by the lead-pursuit logic either way).
        private static bool Prepare() => ModLog.Diagnostics;

        private class Entry
        {
            public ObjectBase Target;
            public Vector3 SetPoint;
            public double Armed = -100.0;
            public double LastLog = -100.0;
        }

        private static readonly ConditionalWeakTable<Aircraft, Entry> Entries = new ConditionalWeakTable<Aircraft, Entry>();

        internal static void Arm(Aircraft ac, ObjectBase target, Vector3 setPoint)
        {
            Entry e = Entries.GetOrCreateValue(ac);
            e.Target = target;
            e.SetPoint = setPoint;
            e.Armed = GameTime.missionElapsedTime;
        }

        private static ObjectBase FindTarget(Aircraft ac)
        {
            if (ac._alignmentTasks != null && ac._alignmentTasks.Count > 0 && ac._alignmentTasks[ac._alignmentTasks.Count - 1]._targetObject != null)
            {
                return ac._alignmentTasks[ac._alignmentTasks.Count - 1]._targetObject;
            }

            ObjectBase moveTarget = ac.GetMoveToEngageTask()?._targetObject;
            if (moveTarget != null)
            {
                return moveTarget;
            }

            return ac._ai != null ? ac._ai._objectToIdentify : null;
        }

        private static void Postfix(FixedWingFlightPhysics __instance)
        {
            if (!ModLog.PursuitTrace)
            {
                return;
            }

            try
            {
                Aircraft ac = __instance._aircraft;
                if (ac == null || ac.IsDestroyed)
                {
                    return;
                }

                Entry e = Entries.GetOrCreateValue(ac);
                double now = GameTime.missionElapsedTime;
                if (now - e.LastLog < IntervalSeconds)
                {
                    return;
                }

                bool modApplied = now - e.Armed <= 1.0 && e.Target != null;
                ObjectBase target = modApplied ? e.Target : FindTarget(ac);
                if (target == null || target.IsDestroyed || !target.isAirTarget())
                {
                    return;
                }

                e.LastLog = now;
                Vector3 flown = __instance.CommandPosition;
                Vector3 set = modApplied ? e.SetPoint : flown;
                string state = ac.StateMachine?.CurrentState?.GetType().Name ?? "?";
                string text = Describe(ac.transform.position, ac._velocityVecInUnity, ac.transform.forward,
                    target.transform.position, target.UnityVelocityVector, set, flown);
                ModLog.Event($"t={now:0} PURSUIT {ac.getUIDAndName()} [{state}] -> {target.getUIDAndName()}: {text} | modLead={(modApplied ? "yes" : "no")} telegraph={ac.getTelegraph() + 1}/{ac.Ap._speedValuesInMach.Length} fuelRange={ac.RangeOnMap.Value:0.00} directPoint={__instance._directPoint}");
            }
            catch (System.Exception ex)
            {
                ModLog.Error("PursuitTelemetry.Postfix", ex);
            }
        }

        // Shared by aircraft and missile traces: positions, headings, speeds and lead geometry.
        // setPoint = point the mod computed; flownPoint = point actually steered at.
        internal static string Describe(Vector3 me, Vector3 mv, Vector3 fwd, Vector3 tp, Vector3 tv, Vector3 setPoint, Vector3 flownPoint)
        {
            Vector3 d = tp - me;
            float directBrg = Brg(d);
            float myHdg = Brg(mv.sqrMagnitude > 1e-6f ? mv : fwd);
            float tgtHdg = Brg(tv);
            float flownBrg = Brg(flownPoint - me);
            float setBrg = Brg(setPoint - me);

            // Ideal collision course at my current speed (horizontal): |d + tv*t| = s*t
            string ideal = "n/a";
            float idealBrg = float.NaN;
            float s = new Vector3(mv.x, 0f, mv.z).magnitude;
            Vector3 d2 = new Vector3(d.x, 0f, d.z);
            Vector3 tv2 = new Vector3(tv.x, 0f, tv.z);
            float a = tv2.sqrMagnitude - s * s;
            float b = 2f * Vector3.Dot(d2, tv2);
            float c = d2.sqrMagnitude;
            float t = -1f;
            if (Mathf.Abs(a) < 1e-6f)
            {
                if (Mathf.Abs(b) > 1e-6f) t = -c / b;
            }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float sq = Mathf.Sqrt(disc);
                    float t1 = (-b - sq) / (2f * a), t2 = (-b + sq) / (2f * a);
                    t = (t1 > 0f && t2 > 0f) ? Mathf.Min(t1, t2) : Mathf.Max(t1, t2);
                }
            }

            if (t > 0f)
            {
                idealBrg = Brg(d2 + tv2 * t);
                ideal = $"{idealBrg:0}deg (lead {Mathf.Abs(Wrap(idealBrg - directBrg)):0}deg, tti {t:0}s)";
            }

            return $"range {d.magnitude / Nm:0.0}nm | "
                   + $"me pos({me.x / Nm:0.0},{me.z / Nm:0.0})nm alt {me.y * 67.2f / 0.3048f:0}ft hdg {myHdg:0} spd {mv.magnitude / Kt:0}kt | "
                   + $"tgt pos({tp.x / Nm:0.0},{tp.z / Nm:0.0})nm alt {tp.y * 67.2f / 0.3048f:0}ft hdg {tgtHdg:0} spd {tv.magnitude / Kt:0}kt | "
                   + $"directBrg {directBrg:0} | hdg-vs-direct {Wrap(myHdg - directBrg):0}deg | "
                   + $"setBrg {setBrg:0} (lead {Wrap(setBrg - directBrg):0}deg) | flownBrg {flownBrg:0} (lead {Wrap(flownBrg - directBrg):0}deg) | "
                   + $"idealBrg {ideal} | hdg-vs-ideal {(float.IsNaN(idealBrg) ? 0f : Wrap(myHdg - idealBrg)):0}deg";
        }

        internal static float Brg(Vector3 v)
        {
            float b = Mathf.Atan2(v.x, v.z) * 57.29578f;
            return b < 0f ? b + 360f : b;
        }

        internal static float Wrap(float a)
        {
            while (a > 180f) a -= 360f;
            while (a < -180f) a += 360f;
            return a;
        }
    }
}





