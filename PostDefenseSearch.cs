using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // After surviving a missile, an aircraft with nothing of its own to re-engage does not simply fly back onto its
    // route as if nothing happened. For a few minutes it searches for the shooter: a weave of crossing legs either side
    // of the direction the missile came from (each turn clears its own and its wingman's six), air-search radar on,
    // altitude held. The search ends when it acquires a target or sees a hostile aircraft, is shot at again (missile
    // defence takes over), is ordered home, or the time runs out; then the game's own AI takes over again.
    public static class PostDefenseSearch
    {
        // Settings (config file, [PostDefenseSearch]).
        public static bool Enabled = true;
        public static float SearchMinutes = 3f;
        public static bool PlayerAircraft;

        private const float LegSec = 25f;      // length of each weave leg
        private const float WeaveDeg = 60f;    // legs alternate this far either side of the threat axis
        private const float AimNm = 15f;       // steering point distance ahead on the leg heading
        private const float SightNm = 40f;     // a hostile aircraft held this close ends the search
        private const float NmToUnity = 27.559496f;
        private const float UnityToFt = 220.47266f;

        private class State
        {
            public float AxisHdg;
            public double Until;
            public double LegStart;
            public int Leg;
            public bool PrevPosOverride;
            public float PrevAltitude;
            public float WantAlt;
            public Vector3 WantPos;
        }

        private static readonly Dictionary<Aircraft, State> States = new Dictionary<Aircraft, State>();

        public static bool IsActive(Aircraft ac) => ac != null && States.ContainsKey(ac);

        // Map status tag, in the same family as missile defence's DEF-NOTCH / DEF-REV / DEF-LDM.
        public static string Label(Aircraft ac) => IsActive(ac) ? "DEF-SEARCH" : null;

        public static void Start(Aircraft ac, Vector3 threatFrom, ObjectBase shooter, string reason = "survived a missile")
        {
            if (!Enabled || ac == null || ac.IsDestroyed || States.ContainsKey(ac) || !(ac.Motioncontroller is FixedWingFlightPhysics mc))
            {
                return;
            }

            if ((ac.IsPlayerObject && (!PlayerAircraft || ac._playerCommandOverride)) || HasTarget(ac) || ac.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase)
            {
                return;
            }

            double now = GameTime.missionElapsedTime;
            Vector3 f = threatFrom;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f)
            {
                f = ac.transform.forward;
            }

            var s = new State
            {
                AxisHdg = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg,
                Until = now + SearchMinutes * 60f,
                LegStart = now,
                Leg = ac.GetInstanceID() & 1, // pair members start on opposite legs, so their turns cross
                PrevPosOverride = mc.commandPositionOverride,
                PrevAltitude = ac.DesiredAltitude.Value,
                WantAlt = ac.transform.position.y // DesiredAltitude is in Unity units, not feet
            };
            States[ac] = s;
            Aim(ac, s);
            if (ac._obp != null && ac._obp._airSearchRadars.Count > 0 && !ac.IsAirSearchRadarsOn.Value)
            {
                ac.EnableAirSearchRadars();
            }

            ModLog.Event($"DEFENSE search start {ac.getUIDAndName()}: [Defending: lost track - searching for recent threat] {reason}{(shooter != null ? " (shooter " + shooter.getUIDAndName() + ")" : "")}; weaving toward {Norm(s.AxisHdg):0} deg for {SearchMinutes:0.#} min, radar on.");
            Reassert(ac);
        }

        // Mutual support: when a formation member is shot at (or shot down), its wingmen do not fly on as if nothing
        // happened. Each one that is free (not defending, searching or attacking) starts the same search toward the point
        // the missile came from; under Weapons Tight/Free the game's AI then engages the shooter once it is seen.
        public static void SupportWingmen(Aircraft ac, Vector3 threatPoint, ObjectBase shooter, string reason)
        {
            if (!Enabled || ac?.Formation?.Stations == null)
            {
                return;
            }

            foreach (Station st in ac.Formation.Stations)
            {
                if (!(st?.UnitObject is Aircraft mate) || mate == ac || mate.IsDestroyed || MissileDefense.IsActive(mate) || IsActive(mate))
                {
                    continue;
                }

                Start(mate, threatPoint - mate.transform.position, shooter, $"{reason}: {ac.getUIDAndName()}");
            }
        }

        public static void Stop(Aircraft ac, string why)
        {
            if (ac == null || !States.TryGetValue(ac, out State s))
            {
                return;
            }

            States.Remove(ac);
            if (ac.Motioncontroller is FixedWingFlightPhysics mc)
            {
                mc.commandPositionOverride = Handback.PositionOverride(ac, s.PrevPosOverride);
            }

            if (!ac.IsDestroyed)
            {
                ac.DesiredAltitude.Value = s.PrevAltitude;
            }

            ModLog.Event($"DEFENSE search end {ac.getUIDAndName()}: {why}.");
        }

        public static void Tick(Aircraft ac)
        {
            if (!States.TryGetValue(ac, out State s))
            {
                return;
            }

            double now = GameTime.missionElapsedTime;
            string why = null;
            ObjectBase seen;
            if (ac.IsDestroyed) why = "destroyed";
            else if (MissileDefense.IsActive(ac)) why = "missile inbound";
            else if (ac.IsPlayerObject && ac._playerCommandOverride) why = "player order (not a threat to life: obeyed)";
            else if (now >= s.Until) why = "search time over, back to own navigation";
            else if (HasTarget(ac)) why = "target acquired";
            else if (ac.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase) why = "ordered home";
            else if (!(ac.Motioncontroller is FixedWingFlightPhysics)) why = "flight model changed";
            else if ((seen = HostileInSight(ac)) != null) why = "hostile in sight: " + seen.getUIDAndName();

            if (why != null)
            {
                Stop(ac, why);
                return;
            }

            if (now - s.LegStart >= LegSec)
            {
                s.Leg++;
                s.LegStart = now;
                Aim(ac, s);
            }
        }

        // Before the flight model reads its commands: keep the search's demands over the game's own navigation.
        public static void Reassert(Aircraft ac)
        {
            if (!States.TryGetValue(ac, out State s) || ac.Motioncontroller == null || MissileDefense.IsActive(ac) || WingmanExtend.IsExtending(ac))
            {
                return;
            }

            ac.Motioncontroller.commandPositionOverride = true;
            ac.Motioncontroller.CommandPosition = s.WantPos;
            ac.DesiredAltitude.Value = s.WantAlt;
        }

        private static void Aim(Aircraft ac, State s)
        {
            float hdg = s.AxisHdg + ((s.Leg & 1) == 0 ? WeaveDeg : -WeaveDeg);
            float rad = hdg * Mathf.Deg2Rad;
            s.WantPos = ac.transform.position + new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * (AimNm * NmToUnity);
            if (ModLog.Verbose)
            {
                ModLog.Event($"DEFENSE search {ac.getUIDAndName()}: leg {s.Leg}, heading {Norm(hdg):0} deg.");
            }
        }

        private static bool HasTarget(Aircraft ac) =>
            ac._ai != null && (ac._ai._objectToDestroy != null || ac._ai._objectsToDestroyList.Count > 0);

        // A hostile aircraft this side currently holds (a live sensor track, not an old plot) within SightNm.
        private static ObjectBase HostileInSight(Aircraft ac)
        {
            Dictionary<ObjectBase, Vehicle> plot = ac._taskforce?.PlottingTable?.LocalVehicles;
            if (plot == null)
            {
                return null;
            }

            foreach (KeyValuePair<ObjectBase, Vehicle> kv in plot)
            {
                ObjectBase o = kv.Key;
                Vehicle v = kv.Value;
                if (o == null || o.IsDestroyed || v == null || !v.HasLiveTrack || !o.isAirTarget() || o.isWeapon()
                    || v.CurrentRelationship() != RelationsState.Hostile)
                {
                    continue;
                }

                if ((o.transform.position - ac.transform.position).magnitude <= SightNm * NmToUnity)
                {
                    return o;
                }
            }

            return null;
        }

        private static float Norm(float h) => (h % 360f + 360f) % 360f;
    }

    [HarmonyPatch(typeof(Aircraft), "OnFixedUpdate")]
    public static class PostDefenseSearchTickPatch
    {
        private static void Postfix(Aircraft __instance)
        {
            PostDefenseSearch.Tick(__instance);
        }
    }

    [HarmonyPatch(typeof(FixedWingFlightPhysics), "CalculatePositionAndAltitudeErrors")]
    public static class PostDefenseSearchReassertPatch
    {
        private static void Prefix(FixedWingFlightPhysics __instance)
        {
            if (__instance._ac is Aircraft ac)
            {
                PostDefenseSearch.Reassert(ac);
            }
        }
    }
}
