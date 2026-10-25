using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeaPower.WeaponAllocation;

namespace SeaPowerFighterAI
{
    // Flight target deconfliction (item 3).
    //
    // The taskforce weapon allocator hands each hostile track to the cheapest bidder. Vanilla prices every bid at a flat
    // 100, so the first shooter in the list always wins and a whole flight clusters on one target. We re-price AIRCRAFT
    // bids on AIR targets so that:
    //   * a fighter that is already busy with a target (engage task or missile still guided) bids higher, which sends
    //     the next target to its idle wingman (bracketing / separate targets);
    //   * among equally busy fighters the one that is closer to the target wins, so the shot is taken by the
    //     best-placed shooter.
    // Lowest price wins, so the penalties below are added to the vanilla price.
    [HarmonyPatch(typeof(AI), nameof(AI.BidOnEngagement))]
    public static class FlightTargetDeconflictionPatch
    {
        // ---- Tunables -------------------------------------------------------------------------------------------
        // Price added per distinct target the shooter is already engaged with (engage tasks + guided missiles).
        private const float BusyPenaltyPerTarget = 40f;
        // Price added per nautical mile to the target (closer shooter = cheaper bid).
        private const float PricePerNm = 0.5f;
        private const float NmToUnity = 27.559496f;

        private static void Postfix(ObjectBase ____baseObject, Vehicle toEngage, ref WeaponAllocator.EngagementBid? __result)
        {
            if (!__result.HasValue)
            {
                return;
            }

            ObjectBase shooter = ____baseObject;
            ObjectBase target = toEngage?.BaseObject;
            if (!(shooter is Aircraft) || target == null || !target.isAirTarget() || target.isWeapon())
            {
                return;
            }

            int busy = CountBusyTargets(shooter);
            float distanceNm = (target.transform.position - shooter.transform.position).magnitude / NmToUnity;
            float price = __result.Value.Price + busy * BusyPenaltyPerTarget + distanceNm * PricePerNm;

            // Wrap the winning action so the assignment is visible in the log.
            Action<WeaponAllocator.EngagementHandle> original = __result.Value.Action;
            string shooterName = shooter.getUIDAndName();
            __result = new WeaponAllocator.EngagementBid(price, handle =>
            {
                ModLog.Event($"ALLOC {shooterName} assigned {handle.Target.BaseObject.getUIDAndName()} (busyTargets={busy}, range={distanceNm:0}nm)");
                original(handle);
            });
        }

        // Distinct targets this shooter is currently working: queued/active engage tasks and missiles it is still guiding.
        private static int CountBusyTargets(ObjectBase shooter)
        {
            var targets = new HashSet<ObjectBase>();
            foreach (EngageTask task in shooter._currentEngageTasks)
            {
                if (task?._targetObject != null && !task._targetObject.IsDestroyed)
                {
                    targets.Add(task._targetObject);
                }
            }

            foreach (AlignUnitToTarget align in shooter._alignmentTasks)
            {
                if (align._isGuidingWeapon && align._targetObject != null && !align._alignmentEndRequested)
                {
                    targets.Add(align._targetObject);
                }
            }

            return targets.Count;
        }
    }
}

