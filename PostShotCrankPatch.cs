using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Post-shot behaviour for a fighter that has radar missiles in flight (EngageAirContact.Pursuit state).
    //
    // Who is in charge:
    //   * ANY guided missile that still needs the launcher (semi-active Fox 1, or a Fox 3 that has not gone pitbull yet)
    //     -> vanilla keeps control. Its crank already keeps every supported target inside the radar gimbal limits,
    //        and leaving it alone is what stops us from spoiling the guidance of the missiles still in flight.
    //   * ALL guided missiles are active (pitbull) -> we take over, because the launcher is no longer needed:
    //       1. A missile is inbound on us             -> DEFENSIVE (beam, stay fast).
    //       2. Any nearby hostile is hot and closing  -> DEFENSIVE (beam off the nearest hot threat).
    //       3. Everyone has turned cold               -> PRESS: lead-pursue the next target we can actually engage
    //          (weapons + fuel), otherwise the closest current target, ready for a follow-up shot.
    //   * Fox 2 (IR) never needs the launcher, so it is untouched.
    [HarmonyPatch(typeof(AircraftStates.EngageAirContact.Pursuit), "onFixedUpdate")]
    public static class PostShotCrankPatch
    {
        // ---- Tunables -------------------------------------------------------------------------------------------
        // Heading offset from the threat's bearing while defending (90 = beam).
        private const float DefensiveOffsetDeg = 90f;
        // Inside this range (4 nm, unity units) vanilla logic is left in charge, like vanilla's own crank.
        private const float MinCrankRangeUnity = 110.23798f;
        // Offsets smaller than this from the direct bearing are not treated as a deliberate vanilla crank side.
        private const float MinVanillaOffsetDeg = 1f;
        // A contact within this range (nm) can threaten us.
        private const float ThreatRangeNm = 45f;
        // A contact is "hot" when its velocity points at us within this angle (deg)...
        private const float HotAspectDeg = 60f;
        // ...and it is faster than this (knots), so slow helicopters / drifting targets do not trigger a defence.
        private const float MinThreatKnots = 150f;
        // Minimum time (s) a mode is held, to stop the fighter flip-flopping (an inbound missile overrides it).
        private const double MinModeHoldSeconds = 6.0;
        // A next target must be inside this fraction of the assigned weapon's max launch range.
        private const float NextTargetRangeFraction = 1.0f;
        // Do not press toward a NEW target below this fuel level (same scale as Aircraft.RangeOnMap.Value).
        private const float NmToUnity = 27.559496f;
        // Remaining fuel range (nm) required before committing to a new target (RangeOnMap is in unity units).
        private const float MinFuelForNewTarget = 120f * NmToUnity;
        private const float KnotsToUnity = 0.0076554087f;

        private enum Mode { None, Defensive, Press }

        // Per-aircraft memory: the defensive turn side (never flips mid-turn), the current mode and when it began.
        private class CrankState
        {
            public float Side = 1f;
            public Mode Mode = Mode.None;
            public ObjectBase ModeTarget;
            public double ModeSince;
            public bool Logged;
        }

        private static readonly ConditionalWeakTable<Aircraft, CrankState> States = new ConditionalWeakTable<Aircraft, CrankState>();

        // Scans every guided missile. Returns the count of live guided missiles; needsSupport is true when any
        // of them still depends on our radar. guidedTargets receives their targets.
        private static int ScanGuided(Aircraft ac, List<ObjectBase> guidedTargets, out bool needsSupport)
        {
            needsSupport = false;
            int count = 0;
            foreach (AlignUnitToTarget task in ac._alignmentTasks)
            {
                if (!task._isGuidingWeapon || task._alignmentEndRequested || !(task._guidedWeapon is Missile m) || m.IsDestroyed)
                {
                    continue;
                }

                count++;
                if (task._targetObject != null && !guidedTargets.Contains(task._targetObject))
                {
                    guidedTargets.Add(task._targetObject);
                }

                bool pitbull = m._seeker != null
                    && m._seeker._guidanceType == AmmunitionParameters.GuidanceType.ActiveRadarHoming
                    && m._seeker.isActive();
                if (!pitbull)
                {
                    needsSupport = true;
                }
            }

            return count;
        }

        // True when a missile is threatening this aircraft (the game's own threat list / evade trigger).
        internal static bool MissileInbound(Aircraft ac)
        {
            if (ac._missileToEvade != null && !ac._missileToEvade.IsDestroyed)
            {
                return true;
            }

            foreach (Missile m in ac._dangerousMissiles)
            {
                if (m != null && !m.IsDestroyed)
                {
                    return true;
                }
            }

            return false;
        }

        // True when the contact is close, fast, and flying toward us (i.e. maneuvering to engage us).
        internal static bool IsHotThreat(Aircraft ac, ObjectBase c)
        {
            Vector3 toMe = ac.transform.position - c.transform.position;
            toMe.y = 0f;
            if (toMe.magnitude > ThreatRangeNm * NmToUnity || toMe.sqrMagnitude < 1e-4f)
            {
                return false;
            }

            Vector3 v = LeadCore.FilteredVelocity(c);
            v.y = 0f;
            if (v.magnitude < MinThreatKnots * KnotsToUnity)
            {
                return false;
            }

            return Vector3.Angle(v, toMe) < HotAspectDeg;
        }

        // Hostile air contacts known to our side (not weapons, not destroyed). The scan walks every plotted
        // contact, and several per-frame callers use it, so the list is cached per plotting table and rebuilt
        // at most every ContactCacheSec of mission time. Destroyed contacts are filtered on the way out.
        private const double ContactCacheSec = 0.5;

        private class ContactCache
        {
            public double BuiltAt = -100.0;
            public readonly List<ObjectBase> List = new List<ObjectBase>();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlottingTable, ContactCache> ContactCaches =
            new System.Runtime.CompilerServices.ConditionalWeakTable<PlottingTable, ContactCache>();

        internal static IEnumerable<ObjectBase> HostileAirContacts(Aircraft ac)
        {
            var table = ac._taskforce?.PlottingTable;
            if (table?.LocalVehicles == null)
            {
                yield break;
            }

            ContactCache cache = ContactCaches.GetOrCreateValue(table);
            double now = GameTime.missionElapsedTime;
            if (now - cache.BuiltAt >= ContactCacheSec || now < cache.BuiltAt)
            {
                cache.BuiltAt = now;
                cache.List.Clear();
                foreach (KeyValuePair<ObjectBase, Vehicle> kv in table.LocalVehicles)
                {
                    ObjectBase c = kv.Key;
                    if (c != null && !c.IsDestroyed && c.isAirTarget() && !c.isWeapon()
                        && kv.Value != null && kv.Value.CurrentRelationship() == RelationsState.Hostile)
                    {
                        cache.List.Add(c);
                    }
                }
            }

            // Index loop: callers may trigger nothing that mutates the cache mid-iteration (same frame, same thread).
            for (int i = 0; i < cache.List.Count; i++)
            {
                ObjectBase c = cache.List[i];
                if (c != null && !c.IsDestroyed)
                {
                    yield return c;
                }
            }
        }

        // Nearest hostile (not already guided on) that we have a usable weapon for, are in range of, and have the fuel to chase.
        private static ObjectBase FindNextTarget(Aircraft ac, List<ObjectBase> guidedTargets)
        {
            if (ac.RangeOnMap.Value < MinFuelForNewTarget || ac._ai == null)
            {
                return null;
            }

            ObjectBase best = null;
            float bestRange = float.MaxValue;
            foreach (ObjectBase c in HostileAirContacts(ac))
            {
                if (guidedTargets.Contains(c))
                {
                    continue;
                }

                // AmmunitionForTarget only returns weapons that are loaded (count > 0) and match this target type.
                float range = (c.transform.position - ac.transform.position).magnitude;
                bool armed = false;
                foreach (Ammunition ammo in ac._ai.AmmunitionForTarget(c, Ammunition.Type.Missile))
                {
                    if (ammo?._ap != null && range < ammo._ap._launchRangesInUnity.y * NextTargetRangeFraction)
                    {
                        armed = true;
                        break;
                    }
                }

                if (armed && range < bestRange)
                {
                    best = c;
                    bestRange = range;
                }
            }

            return best;
        }

        private static ObjectBase Nearest(Aircraft ac, List<ObjectBase> list)
        {
            ObjectBase best = null;
            float bestRange = float.MaxValue;
            foreach (ObjectBase c in list)
            {
                float range = (c.transform.position - ac.transform.position).magnitude;
                if (range < bestRange)
                {
                    best = c;
                    bestRange = range;
                }
            }

            return best;
        }

        private static void Postfix(AircraftStates.EngageAirContact ____parentState, Aircraft ____aircraft)
        {
            if (____parentState?.currentAlignment?._targetObject == null)
            {
                return;
            }

            var guidedTargets = new List<ObjectBase>();
            int guided = ScanGuided(____aircraft, guidedTargets, out bool needsSupport);
            CrankState state = States.GetOrCreateValue(____aircraft);

            // Nothing in flight, or a missile still needs our radar: vanilla owns the maneuver.
            if (guided == 0 || needsSupport || guidedTargets.Count == 0)
            {
                state.Mode = Mode.None;
                state.Logged = false;
                return;
            }

            Vector3 me = ____aircraft.transform.position;
            ObjectBase nearestGuided = Nearest(____aircraft, guidedTargets);
            Vector3 toGuided = nearestGuided.transform.position - me;
            toGuided.y = 0f;
            if (toGuided.magnitude < MinCrankRangeUnity)
            {
                return;
            }

            // Remember vanilla's crank side while it is the one steering (the sign of the offset from the direct bearing).
            if (state.Mode == Mode.None)
            {
                Vector3 vanilla = ____aircraft.Motioncontroller.CommandPosition - me;
                vanilla.y = 0f;
                if (vanilla.sqrMagnitude > 1e-4f)
                {
                    float angle = Vector3.SignedAngle(toGuided, vanilla, Vector3.up);
                    if (Mathf.Abs(angle) > MinVanillaOffsetDeg)
                    {
                        state.Side = Mathf.Sign(angle);
                    }
                }
            }

            // Threat picture: nearest hot contact (guided targets and any other hostile nearby).
            bool inbound = MissileInbound(____aircraft);
            ObjectBase hotThreat = null;
            float hotRange = float.MaxValue;
            int hotCount = 0;
            foreach (ObjectBase c in HostileAirContacts(____aircraft))
            {
                if (!IsHotThreat(____aircraft, c))
                {
                    continue;
                }

                hotCount++;
                float range = (c.transform.position - me).magnitude;
                if (range < hotRange)
                {
                    hotThreat = c;
                    hotRange = range;
                }
            }

            Mode wanted;
            ObjectBase wantedTarget;
            if (inbound || hotThreat != null)
            {
                wanted = Mode.Defensive;
                wantedTarget = hotThreat ?? nearestGuided;
            }
            else
            {
                wanted = Mode.Press;
                wantedTarget = FindNextTarget(____aircraft, guidedTargets) ?? nearestGuided;
            }

            // Hold the previous mode briefly to avoid flip-flopping (an inbound missile always overrides the hold).
            double now = GameTime.missionElapsedTime;
            bool holding = state.Mode != Mode.None && now - state.ModeSince < MinModeHoldSeconds && !inbound
                && state.ModeTarget != null && !state.ModeTarget.IsDestroyed;
            if (holding)
            {
                wanted = state.Mode;
                wantedTarget = state.ModeTarget;
            }
            else if (wanted != state.Mode || wantedTarget != state.ModeTarget)
            {
                state.Mode = wanted;
                state.ModeTarget = wantedTarget;
                state.ModeSince = now;
                ModLog.Event($"POST-PITBULL {____aircraft.getUIDAndName()} mode={wanted} on {wantedTarget.getUIDAndName()} guidedMissiles={guided} guidedTargets={guidedTargets.Count} hotThreats={hotCount} inbound={inbound}");
            }

            if (wanted == Mode.Defensive)
            {
                // Beam off the threat. Same rotation helper vanilla uses, so the sign convention matches.
                ____aircraft.Motioncontroller.CommandPosition =
                    Utils.RotatePointAroundPivot(wantedTarget.transform.position, me, new Vector3(0f, state.Side * DefensiveOffsetDeg, 0f));
            }
            else
            {
                // Turn hot with lead on the chosen target (direct point inside the lead module's minimum range).
                if (!LeadCore.TryCompute(____aircraft, wantedTarget, ____parentState.currentAlignment._ap, out Vector3 point))
                {
                    point = wantedTarget.transform.position;
                }

                ____aircraft.Motioncontroller.CommandPosition = point;
                PursuitTelemetry.Arm(____aircraft, wantedTarget, point);
            }

            // Vanilla crank drops to telegraph 3; hold military power instead so the fighter keeps its energy.
            if (____aircraft.AllowCombatSpeedControl)
            {
                int mil = LeadCore.MilitaryIndex(____aircraft);
                if (____aircraft.getTelegraph() != mil)
                {
                    ____aircraft.setTelegraph(mil + 1);
                }
            }
        }
    }
}

