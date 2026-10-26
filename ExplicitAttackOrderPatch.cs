using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Makes a plain right-click attack order on a formation (no weapon selected) actually commit.
    // Vanilla UnitSelectedState.AircraftAttackByClick only adds the target to the aircraft's AI._objectsToDestroyList.
    // On its next tick AI.IsStaleEngageTarget removes it if the target is beyond (max weapon range + ~25 nm), and
    // AI.CloserEligibleShooterExists / ShouldAbandonAirChase can also veto it, so the flight stays on "heading to waypoint".
    // We remember which targets the player explicitly ordered and exempt them from those vetoes.
    internal static class ExplicitOrder
    {
        // Player-ordered targets, keyed by the shooter's instance id.
        private static readonly Dictionary<int, HashSet<ObjectBase>> Ordered = new Dictionary<int, HashSet<ObjectBase>>();

        public static void Record(ObjectBase shooter, ObjectBase target)
        {
            if (shooter == null || target == null) return;
            Prune();
            int id = shooter.GetInstanceID();
            if (!Ordered.TryGetValue(id, out var set)) Ordered[id] = set = new HashSet<ObjectBase>();
            set.Add(target);
        }

        // Orders are rare (player clicks), so pruning on each Record keeps the table from growing over a long
        // mission: drop destroyed/unloaded targets, and shooters with nothing left.
        private static readonly List<int> EmptyIds = new List<int>();

        private static void Prune()
        {
            EmptyIds.Clear();
            foreach (var kv in Ordered)
            {
                kv.Value.RemoveWhere(t => t == null || t.IsDestroyed);
                if (kv.Value.Count == 0) EmptyIds.Add(kv.Key);
            }

            foreach (int id in EmptyIds) Ordered.Remove(id);
        }

        public static bool IsPlayerOrdered(ObjectBase shooter, ObjectBase target)
        {
            if (!(shooter is Aircraft) || target == null || target.IsDestroyed) return false;
            return Ordered.TryGetValue(shooter.GetInstanceID(), out var set) && set.Contains(target);
        }
    }

    [HarmonyPatch]
    public static class ExplicitOrderRecordPatch
    {
        // UnitSelectedState is internal to the game assembly, so resolve it by name.
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("SeapowerUI.ViewModels.UnitSelectedState"), "AircraftAttackByClick");

        private static void Postfix(object mvm, ObjectBase obj)
        {
            // mvm is a MapUnitViewModel; its Unit.BaseObject is the clicked target.
            var unit = Traverse.Create(mvm).Property("Unit").GetValue();
            var target = unit != null ? Traverse.Create(unit).Property("BaseObject").GetValue<ObjectBase>() : null;
            ExplicitOrder.Record(obj, target);
            // Wingmen follow the lead's order, so exempt them too.
            if (obj.Formation != null)
                foreach (var st in obj.Formation.Stations)
                    if (st.UnitObject is Aircraft) ExplicitOrder.Record(st.UnitObject, target);
            ModLog.Event($"CLICK-ORDER recorded: {obj.getUIDAndName()} -> {(target != null ? target.getUIDAndName() : "null")}");

            // Vanilla leaves the target in _objectToDestroy and waits for the auto-engage scan to build a task, but that
            // scan never produced one at long range (logged: objectToDestroy set, engageTasks=0, forever). Do what the
            // weapon-first click does: queue the engage task now so the flight starts MoveToEngage pursuit.
            if (target != null && target.isAirTarget() && !target.isWeapon() && obj is Aircraft)
                CommitEngage(obj, target);
        }

        private static void CommitEngage(ObjectBase shooter, ObjectBase target)
        {
            if (shooter._currentEngageTasks.Count > 0 || shooter._ai == null) return;
            string best = null;
            float bestRange = -1f;
            foreach (string name in shooter._ai.AmmoForTarget(target, false))
            {
                var am = shooter.getAmmunitionByName(name);
                if (am == null || am._ap == null || am._ap._type != Ammunition.Type.Missile) continue;
                if (shooter.getAmmunitionAmountByName(name) < 1) continue;
                if (am._ap._launchRangesInUnity.y > bestRange) { bestRange = am._ap._launchRangesInUnity.y; best = name; }
            }
            if (best == null) return;
            shooter.InsertEngageTask(best, target, UnityEngine.Vector3.zero, 1, 66, false);
            ModLog.Event($"CLICK-ORDER engage task queued: {shooter.getUIDAndName()} {best} -> {target.getUIDAndName()}");
        }
    }

    [HarmonyPatch(typeof(AI), "IsStaleEngageTarget")]
    public static class ExplicitOrderStalePatch
    {
        // ____baseObject = Harmony's three underscores + the private field name "_baseObject".
        private static void Postfix(ObjectBase target, ref bool __result, ObjectBase ____baseObject)
        {
            if (__result && ExplicitOrder.IsPlayerOrdered(____baseObject, target))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(AI), "ShouldAbandonAirChase")]
    public static class ExplicitOrderAbandonPatch
    {
        private static void Postfix(ObjectBase target, ref bool __result, ObjectBase ____baseObject)
        {
            if (__result && ExplicitOrder.IsPlayerOrdered(____baseObject, target))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(AI), "CloserEligibleShooterExists")]
    public static class ExplicitOrderShooterPatch
    {
        // Don't let a closer wingman veto a target the player ordered this aircraft to attack.
        private static void Postfix(ObjectBase target, ref bool __result, ObjectBase ____baseObject)
        {
            if (__result && ExplicitOrder.IsPlayerOrdered(____baseObject, target))
                __result = false;
        }
    }
}
