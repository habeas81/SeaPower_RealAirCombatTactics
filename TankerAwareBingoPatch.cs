using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Tanker-aware Bingo.
    // Vanilla fires the BingoFuel (force-RTB) state when remaining range (net of the trip home) hits ~0. Just
    // before that it calls Aircraft.TryStartEmergencyRefuel(); if that returns true the RTB is suppressed and the
    // aircraft flies a Refuel order to the tanker instead. Vanilla limits this to PLAYER aircraft and to tankers in
    // the SAME taskforce. This patch replaces it so that:
    //   - AI aircraft on every side get the same find-a-tanker-and-fuel-up behaviour, and
    //   - friendly tankers (Player + Ally sides share tankers) are candidates, not just same-taskforce ones.
    // If no tanker is reachable we return false and vanilla BingoFuel RTB proceeds unchanged.
    [HarmonyPatch(typeof(Aircraft), "TryStartEmergencyRefuel")]
    public static class TankerAwareBingoPatch
    {
        // ---- Tunables ----
        private static readonly bool AllowAlliedTankers = true;     // Player<->Ally tankers may serve each other
        private const int MinWeaponsToStayOnStation = 1; // <= this many non-gun weapons left -> RTB to rearm instead
        private const float InterceptSpeedKt = 450f;     // assumed receiver speed while closing on the tanker
        private const float BaseCloserFactor = 1.0f;     // tanker is used only if intercept distance <= base distance * this
        private const int MaxRouteLegs = 12;             // tanker waypoints considered
        private const float StraightLineHorizonUnits = 55000f; // ~2000 nm: "keeps going straight" fallback leg
        private const float KtToUnitsPerSec = 0.0076554087f;
        private const float ReachFraction = 0.9f;         // vanilla: tanker must be within 90% of remaining range
        private const float ReachMarginKm = 20f;          // extra slack so the rendezvous itself is not the last drop of fuel
        private const double LogIntervalSec = 20.0;       // per-aircraft log throttle

        private const float UnityToKm = 67.200066f / 1000f; // world units -> km (same constant vanilla uses)

        private static readonly Dictionary<int, double> LastLog = new Dictionary<int, double>();

        // Prefix: take over completely. Returning false skips vanilla; __result is what the state machine sees.
        private static bool Prefix(Aircraft __instance, ref bool __result)
        {
            try
            {
                __result = TryStart(__instance);
            }
            catch (System.Exception e)
            {
                ModLog.Warn("TankerBingo: exception " + e.Message);
                __result = false;
            }

            return false;
        }

        private static bool TryStart(Aircraft ac)
        {
            if (ac._taskforce == null)
            {
                return false;
            }

            // Winchester / nearly so: the jet needs a rearm, not just gas, so let vanilla RTB proceed.
            int weapons = CountNonGunWeapons(ac);
            if (weapons <= MinWeaponsToStayOnStation)
            {
                Throttled(ac, "TankerBingo: " + ac.getUIDAndName() + " at bingo with " + weapons
                    + " non-gun weapon(s) -> RTB to rearm");
                return false;
            }

            // Already refuelling from a tanker with a live task: keep going, don't re-order every frame.
            Order cur = ac.CurrentOrder.Value;
            if (cur != null && cur.OrderType == Order.Type.Refuel && cur.TargetUnit is Aircraft curTanker
                && ac.HasTemporaryFormationReference && ac.ActiveFormationLeader == curTanker)
            {
                return true;
            }

            Aircraft best = null;
            float bestKm = float.MaxValue;
            int considered = 0;
            float baseKm = ac._homeBase != null
                ? (ac._homeBase.transform.position - ac.transform.position).magnitude * UnityToKm
                : float.MaxValue;

            foreach (Taskforce tf in Singleton<TaskforceManager>.Instance._taskForces)
            {
                if (tf == null || !SameCoalition(ac._taskforce, tf))
                {
                    continue;
                }

                foreach (ObjectBase o in tf._taskforceAircraft)
                {
                    Aircraft tanker = o as Aircraft;
                    if (tanker == null)
                    {
                        continue;
                    }

                    considered++;
                    if (!IsUsableTanker(ac, tanker, out float km))
                    {
                        continue;
                    }

                    if (km < bestKm)
                    {
                        bestKm = km;
                        best = tanker;
                    }
                }
            }

            if (best == null)
            {
                Throttled(ac, "TankerBingo: " + ac.getUIDAndName() + " at bingo, no reachable tanker (" + considered
                    + " aircraft checked) -> vanilla RTB");
                return false;
            }

            // The tanker moves, so compare the flight to its intercept point with the straight run home.
            if (bestKm > baseKm * BaseCloserFactor)
            {
                Throttled(ac, "TankerBingo: " + ac.getUIDAndName() + " base is closer (" + baseKm.ToString("F0")
                    + "km) than tanker intercept (" + bestKm.ToString("F0") + "km) -> RTB");
                return false;
            }

            ac.setOrder(Order.Type.Refuel, best, true);
            // GetRefuelTask() returned null even though the order took (log showed orderNow=Refuel, tmpRef=True),
            // so judge success by the order and the temporary tanker formation link instead.
            Order after = ac.CurrentOrder.Value;
            bool ok = after != null && after.OrderType == Order.Type.Refuel && after.TargetUnit == best
                && ac.HasTemporaryFormationReference && ac.ActiveFormationLeader == best;
            if (!ok)
            {
                bool sys = false;
                foreach (SupplySystem s in best._obp._supplySystems)
                {
                    sys |= s.IsAerialRefuelSystem;
                }

                ModLog.Info("TankerBingo-DIAG: " + ac.getUIDAndName() + " controllable=" + ac.IsControllable
                    + " userRoot=" + (ac._userRoot != null) + " orderNow=" + ac.CurrentOrder.Value?.OrderType
                    + " leader=" + ac.IsFormationLeader + " tmpRef=" + ac.HasTemporaryFormationReference
                    + " tankerAerialSys=" + sys + " tankerFormationLeaderOf=" + (ac.ActiveFormationLeader != null));
            }
            ModLog.Info("TankerBingo: " + ac.getUIDAndName() + " at bingo -> refuel from " + best.getUIDAndName()
                + " intercept=" + bestKm.ToString("F0") + "km base=" + baseKm.ToString("F0") + "km taskAccepted=" + ok);
            return ok;
        }

        private static int CountNonGunWeapons(Aircraft ac)
        {
            int total = 0;
            foreach (KeyValuePair<string, int> kv in ac.AmmunitionAmountDictionary)
            {
                if (kv.Value < 1 || !ac.AmmunitionNameToAmmunitionDictionary.TryGetValue(kv.Key, out Ammunition am) || am?._ap == null)
                {
                    continue;
                }

                switch (am._ap._type)
                {
                    case Ammunition.Type.Missile:
                    case Ammunition.Type.Bomb:
                    case Ammunition.Type.Cluster:
                    case Ammunition.Type.AerialRocket:
                    case Ammunition.Type.Torpedo:
                    case Ammunition.Type.ASROC:
                        total += kv.Value;
                        break;
                }
            }

            return total;
        }

        private static bool SameCoalition(Taskforce a, Taskforce b)
        {
            if (a == b || a.Side == b.Side)
            {
                return true;
            }

            if (!AllowAlliedTankers)
            {
                return false;
            }

            return IsFriendly(a.Side) && IsFriendly(b.Side);
        }

        private static bool IsFriendly(Taskforce.TfType s)
        {
            return s == Taskforce.TfType.Player || s == Taskforce.TfType.Ally;
        }

        // Mirrors vanilla CanReachEmergencyRefuelTanker minus the same-taskforce rule, plus a small reach margin.
        private static bool IsUsableTanker(Aircraft rx, Aircraft tanker, out float interceptKm)
        {
            interceptKm = float.MaxValue;
            if (tanker == rx || tanker.IsDestroyed || !tanker._isInFlight || tanker._obp == null)
            {
                return false;
            }

            if (!AerialRefuelingEquipment.TryGetCompatibleSystem(tanker, rx, out _))
            {
                return false;
            }

            bool hasSystem = false;
            foreach (SupplySystem s in tanker._obp._supplySystems)
            {
                if (s.IsAerialRefuelSystem && s.CanRefuel && tanker.SupplyAllowed.Value && s.IsOn.Value)
                {
                    hasSystem = true;
                    break;
                }
            }

            if (!hasSystem)
            {
                return false;
            }

            // Remaining range in km, converted from "range at cruise" the same way vanilla does.
            AircraftParameters ap = rx.Ap;
            int idx = Mathf.Min(2, ap._speedValuesInMach.Length - 1);
            float k = ap._rangeMultiplierCurve.Evaluate(ap._speedValuesInMach[idx]);
            k /= ap._altitudeEfficiencyCurve.Evaluate(rx.getHeightInFeet());
            k *= 1f + ap._loadoutMass / ap._emptyMass;
            float reachKm = rx.RangeInKm.Value / Mathf.Max(1f, k) * ReachFraction - ReachMarginKm;
            interceptKm = InterceptDistanceKm(rx, tanker);
            return interceptKm <= reachKm;
        }

        // Distance (km) the receiver must fly, at InterceptSpeedKt, to meet the tanker along its PLANNED ROUTE.
        // The tanker is modelled as flying its remaining waypoints in order at its current speed, then holding
        // at the last waypoint. For each leg we solve |D + u*tau| = s*(t0 + tau) for the earliest tau inside the
        // leg (t0 = time the tanker reaches the leg start), so planned turns are respected. With no absolute
        // waypoints the tanker is assumed to continue straight on its current heading.
        private static float InterceptDistanceKm(Aircraft rx, Aircraft tanker)
        {
            Vector2 rxPos = new Vector2(rx.transform.position.x, rx.transform.position.z);
            Vector2 tkPos = new Vector2(tanker.transform.position.x, tanker.transform.position.z);
            float s = InterceptSpeedKt * KtToUnitsPerSec;
            float vt = Mathf.Max(1f, tanker._velocityInKnots) * KtToUnitsPerSec;

            List<Vector2> route = BuildRoute(tanker, tkPos);
            float t0 = 0f;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                Vector2 a = route[i];
                Vector2 seg = route[i + 1] - a;
                float len = seg.magnitude;
                if (len < 1e-3f)
                {
                    continue;
                }

                Vector2 u = seg / len * vt;
                float legTime = len / vt;
                Vector2 d = a - rxPos;
                float qa = vt * vt - s * s;
                float qb = 2f * Vector2.Dot(d, u) - 2f * s * s * t0;
                float qc = Vector2.Dot(d, d) - s * s * t0 * t0;
                float tau = SmallestRoot(qa, qb, qc, 0f, legTime);
                if (tau >= 0f)
                {
                    return s * (t0 + tau) * UnityToKm;
                }

                t0 += legTime;
            }

            // Past the end of the route the tanker holds at its last point.
            Vector2 last = route[route.Count - 1];
            float tHold = Mathf.Max(t0, (last - rxPos).magnitude / s);
            return s * tHold * UnityToKm;
        }

        // Tanker position followed by its remaining absolute waypoints (world xz, unity units). Waypoints are
        // geo positions, so each is placed relative to the tanker's own geo position (heading 0 = +z north).
        private static List<Vector2> BuildRoute(Aircraft tanker, Vector2 tkPos)
        {
            List<Vector2> route = new List<Vector2> { tkPos };
            try
            {
                var wps = tanker.GetAllWaypointsGeoPositions(false);
                int n = Mathf.Min(wps.Count, MaxRouteLegs);
                for (int i = 0; i < n; i++)
                {
                    var hr = tanker._geoPosition.HeadingRangeUnity(wps[i].wpGP);
                    float h = hr.heading * Mathf.Deg2Rad;
                    route.Add(tkPos + new Vector2(Mathf.Sin(h), Mathf.Cos(h)) * hr.range);
                }
            }
            catch (System.Exception)
            {
            }

            if (route.Count == 1)
            {
                Vector3 f = tanker.transform.forward;
                Vector2 dir = new Vector2(f.x, f.z).normalized;
                route.Add(tkPos + dir * StraightLineHorizonUnits);
            }

            return route;
        }

        // Smallest root of qa*x^2 + qb*x + qc = 0 within [lo, hi], or -1 if none.
        private static float SmallestRoot(float qa, float qb, float qc, float lo, float hi)
        {
            float best = -1f;
            if (Mathf.Abs(qa) < 1e-9f)
            {
                if (Mathf.Abs(qb) > 1e-9f)
                {
                    float x = -qc / qb;
                    if (x >= lo && x <= hi)
                    {
                        best = x;
                    }
                }

                return best;
            }

            float disc = qb * qb - 4f * qa * qc;
            if (disc < 0f)
            {
                return -1f;
            }

            float sq = Mathf.Sqrt(disc);
            float r1 = (-qb - sq) / (2f * qa);
            float r2 = (-qb + sq) / (2f * qa);
            if (r1 > r2)
            {
                float tmp = r1;
                r1 = r2;
                r2 = tmp;
            }

            if (r1 >= lo && r1 <= hi)
            {
                return r1;
            }

            if (r2 >= lo && r2 <= hi)
            {
                return r2;
            }

            return -1f;
        }

        private static void Throttled(Aircraft ac, string msg)
        {
            double now = GameTime.missionElapsedTime;
            int id = ac.GetInstanceID();
            if (LastLog.TryGetValue(id, out double last) && now - last < LogIntervalSec)
            {
                return;
            }

            LastLog[id] = now;
            ModLog.Info(msg);
        }
    }
}
