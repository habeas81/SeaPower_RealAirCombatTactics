using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // "Stay hot": when the player manually orders an air-to-air attack, the engage task ends as soon as the
    // missile leaves the rail and the aircraft immediately reverts to its last waypoint. This keeps the order alive
    // until the target is destroyed (or the player gives a new order) by re-issuing the engage task, so the
    // fighter keeps pursuing (with the mod's lead pursuit) and can shoot again if the first missile is defeated.
    // Re-fire timing is still governed by the shoot-look-shoot doctrine, so this never causes a double-launch.
    // It also hands speed control back to the mod on a fresh attack order, so the jet goes to military power/AB.
    public static class HotPursuit
    {
        // ---- Tunables ----
        public const double PollIntervalSec = 2.0;      // how often tasks are checked per aircraft
        public const double RetaskCooldownSec = 4.0;    // min time between re-issued tasks
        public const float MaxRetaskRangeNm = 120f;     // stop chasing beyond this range (nm)
        public const float MinFuelRangeNm = 80f;        // stop chasing below this remaining fuel range (nm)
        public const bool ReleasePlayerSpeedOnAttack = true; // fresh attack order => mod may manage speed again
        private const float NmToUnity = 27.559496f;

        public class Record
        {
            public ObjectBase Target;
            public string AmmoId;
            public int Priority;
            public double NextCheck;
            public double LastRetask;
        }

        public static readonly Dictionary<Aircraft, Record> Records = new Dictionary<Aircraft, Record>();

        // Set while we insert a task ourselves so the registration hook ignores it.
        public static bool Inserting;

        public static void Cancel(Aircraft ac, string why)
        {
            if (Records.Remove(ac))
            {
                ModLog.Event($"t={GameTime.missionElapsedTime:0} HOT-PURSUIT ended: {ac.getUIDAndName()} ({why}).");
            }
        }

        // True when the call originates from player UI input (not mission setup or automation).
        public static bool FromPlayerUi()
        {
            foreach (StackFrame f in new StackTrace(3, false).GetFrames() ?? new StackFrame[0])
            {
                string n = f.GetMethod()?.DeclaringType?.Name ?? "";
                if (n.Contains("UnitSelectedState") || n.Contains("GetContextMenuItems") || n.Contains("DisplayClass139") || n.Contains("MouseAction"))
                {
                    return true;
                }
            }

            return false;
        }

        // Does the aircraft still have work on this target (queued engage task or active alignment)?
        public static bool HasWorkOn(Aircraft ac, ObjectBase target)
        {
            foreach (EngageTask t in ac._currentEngageTasks)
            {
                if (t != null && t._targetObject == target)
                {
                    return true;
                }
            }

            foreach (AlignUnitToTarget a in ac._alignmentTasks)
            {
                if (a != null && a._targetObject == target)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Register a manual (non-auto) air-to-air missile attack order.
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.AddEngageTask))]
    public static class HotPursuitRegisterPatch
    {
        private static void Postfix(ObjectBase __instance, EngageTask engageTask)
        {
            try
            {
                Aircraft ac = __instance as Aircraft;
                if (ac == null || HotPursuit.Inserting || engageTask == null || engageTask._isAutoAttack || engageTask._priority < 0
                    || engageTask._targetObject == null || !engageTask._targetObject.isAirTarget() || engageTask._targetObject.isWeapon())
                {
                    return;
                }

                Ammunition ammo = ac.getAmmunitionByName(engageTask._ammoId);
                if (ammo == null || ammo._ap._type != Ammunition.Type.Missile)
                {
                    return;
                }

                HotPursuit.Records[ac] = new HotPursuit.Record
                {
                    Target = engageTask._targetObject,
                    AmmoId = engageTask._ammoId,
                    Priority = engageTask._priority,
                    NextCheck = GameTime.missionElapsedTime + HotPursuit.PollIntervalSec,
                    LastRetask = GameTime.missionElapsedTime
                };

                // A fresh attack order is a new command: let the mod manage speed again (player can override afterwards).
                if (HotPursuit.ReleasePlayerSpeedOnAttack && ac._speedSetByPlayer)
                {
                    ac._speedSetByPlayer = false;
                }

                ModLog.Event($"t={GameTime.missionElapsedTime:0} HOT-PURSUIT armed: {ac.getUIDAndName()} -> {engageTask._targetObject.getUIDAndName()} with {engageTask._ammoId}.");
            }
            catch (Exception ex)
            {
                ModLog.Error("HotPursuitRegisterPatch.Postfix", ex);
            }
        }
    }

    // New player orders cancel the hot pursuit: any real order, or a waypoint edit made from the UI.
    [HarmonyPatch]
    public static class HotPursuitCancelPatch
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

        private static void Postfix(ObjectBase __instance, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                Aircraft ac = __instance as Aircraft;
                if (ac == null || !HotPursuit.Records.ContainsKey(ac))
                {
                    return;
                }

                if (__originalMethod.Name == "setOrder")
                {
                    // Order.Type.None is just "clear" (the attack click itself ends with one).
                    if (__args != null && __args.Length > 0 && __args[0] != null && __args[0].ToString() != "None")
                    {
                        HotPursuit.Cancel(ac, "new order " + __args[0]);
                    }
                }
                else if (HotPursuit.FromPlayerUi())
                {
                    HotPursuit.Cancel(ac, "player " + __originalMethod.Name);
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("HotPursuitCancelPatch.Postfix", ex);
            }
        }
    }

    // Poll: when the engage task has finished but the target lives, re-issue it.
    [HarmonyPatch(typeof(AI), nameof(AI.OnUpdate))]
    public static class HotPursuitPollPatch
    {
        private static void Postfix(ObjectBase ____baseObject)
        {
            try
            {
                Aircraft ac = ____baseObject as Aircraft;
                if (ac == null || HotPursuit.Records.Count == 0 || !HotPursuit.Records.TryGetValue(ac, out HotPursuit.Record r))
                {
                    return;
                }

                double now = GameTime.missionElapsedTime;
                if (now < r.NextCheck)
                {
                    return;
                }

                r.NextCheck = now + HotPursuit.PollIntervalSec;

                if (ac.IsDestroyed || !ac.HasControl)
                {
                    HotPursuit.Records.Remove(ac);
                    return;
                }

                if (r.Target == null || r.Target.IsDestroyed || r.Target._externalDestructionNotified)
                {
                    HotPursuit.Cancel(ac, "target destroyed");
                    return;
                }

                // Returning home / landing / bingo: don't fight the fuel logic.
                if (ac.IsLanding || ac.RangeOnMap.Value < HotPursuit.MinFuelRangeNm * 27.559496f)
                {
                    HotPursuit.Cancel(ac, "fuel/landing");
                    return;
                }

                float rangeNm = (r.Target.transform.position - ac.transform.position).magnitude / 27.559496f;
                if (rangeNm > HotPursuit.MaxRetaskRangeNm)
                {
                    HotPursuit.Cancel(ac, "target out of range");
                    return;
                }

                if (HotPursuit.HasWorkOn(ac, r.Target) || now - r.LastRetask < HotPursuit.RetaskCooldownSec)
                {
                    return;
                }

                // Same weapon if we still have one; otherwise any loaded missile that fits the target.
                string ammo = ac.getAmmunitionAmountByName(r.AmmoId) > 0 ? r.AmmoId : null;
                if (ammo == null && ac._ai != null)
                {
                    foreach (Ammunition a in ac._ai.AmmunitionForTarget(r.Target, Ammunition.Type.Missile))
                    {
                        if (a?._ap != null)
                        {
                            ammo = a._ap._ammunitionFileName;
                            break;
                        }
                    }
                }

                if (ammo == null)
                {
                    HotPursuit.Cancel(ac, "no weapons left");
                    return;
                }

                r.LastRetask = now;
                r.AmmoId = ammo;
                HotPursuit.Inserting = true;
                try
                {
                    ac.InsertEngageTask(ammo, r.Target, r.Target.transform.position, 1, r.Priority);
                }
                finally
                {
                    HotPursuit.Inserting = false;
                }

                ModLog.Event($"t={now:0} HOT-PURSUIT re-tasked: {ac.getUIDAndName()} -> {r.Target.getUIDAndName()} with {ammo} ({rangeNm:0.0} nm).");
            }
            catch (Exception ex)
            {
                ModLog.Error("HotPursuitPollPatch.Postfix", ex);
            }
        }
    }
}
