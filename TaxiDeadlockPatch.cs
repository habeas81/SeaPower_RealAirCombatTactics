using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Fixes taxi deadlock on small airbases/decks. Vanilla GroundTaxiPhysics.OnFixedUpdate stops an aircraft
    // whenever another aircraft is within _taxiInterval and inside a 45 deg cone ahead of it. When two aircraft
    // merge toward each other each one sees the other in its cone, both hold at 0 speed forever, and the base is blocked.
    // After a mutual standoff persists, the aircraft with the lower instance id ignores the traffic check until it clears.
    [HarmonyPatch]
    public static class TaxiDeadlockPatch
    {
        private const float StandoffSeconds = 6f;
        private const float ConeDegrees = 45f;
        private const float MovingSpeed = 0.05f;
        private const int MaxFailures = 25;

        private class Entry
        {
            public float BlockedFor;
            public bool Overriding;
        }

        private class Restore
        {
            public ObjectBaseParameters A, B;
            public float ValA, ValB;
        }

        private static readonly ConditionalWeakTable<MotionController, Entry> Entries = new ConditionalWeakTable<MotionController, Entry>();
        private static int _failures;
        private static bool _disabled;

        public static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("SeaPower.GroundTaxiPhysics"), "OnFixedUpdate");
        }

        public static void Prefix(MotionController __instance, bool ___holdPosition, bool ___useCommandPosition,
            bool ____allowMovement, bool ___backTaxi, ref object __state)
        {
            __state = null;
            if (_disabled || !____allowMovement || ___holdPosition || !___useCommandPosition)
            {
                return;
            }

            try
            {
                ObjectBase me = __instance._ac;
                if (me == null || !me.IsAirUnit || __instance.CommandVelocity < 0.01f)
                {
                    return;
                }

                Entry entry = Entries.GetValue(__instance, _ => new Entry());
                ObjectBase blocker = FindBlocker(__instance, me, ___backTaxi);
                if (blocker == null || __instance.Velocity > MovingSpeed && !entry.Overriding)
                {
                    entry.BlockedFor = 0f;
                    entry.Overriding = false;
                    return;
                }

                entry.BlockedFor += GameTime.fixedDeltaTime;
                if (entry.BlockedFor < StandoffSeconds)
                {
                    return;
                }

                // A queue is not a deadlock: only act when the blocker is also held by us.
                if (!IsBlockedBy(blocker, me) || me.GetInstanceID() > blocker.GetInstanceID())
                {
                    return;
                }

                if (!entry.Overriding)
                {
                    entry.Overriding = true;
                    ModLog.Event($"t={GameTime.missionElapsedTime:0} TAXI DEADLOCK: {me.getUIDAndName()} and {blocker.getUIDAndName()} " +
                                 "were holding for each other; releasing the first.");
                }

                var restore = new Restore { A = me._obp, B = blocker._obp };
                restore.ValA = restore.A._taxiInterval;
                restore.ValB = restore.B._taxiInterval;
                restore.A._taxiInterval = 0f;
                restore.B._taxiInterval = 0f;
                __state = restore;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        public static void Postfix(object __state)
        {
            if (__state is Restore r)
            {
                r.A._taxiInterval = r.ValA;
                r.B._taxiInterval = r.ValB;
            }
        }

        // GetComponentsInChildren allocates and walks the whole hierarchy, so the sibling list is cached per
        // parent (carrier/airfield) and refreshed periodically; taxiing aircraft come and go slowly.
        private const float SiblingRefreshSec = 2f;

        private class SiblingCache
        {
            public float RefreshAt = -1f;
            public ObjectBase[] Items = new ObjectBase[0];
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ObjectBase, SiblingCache> Siblings =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ObjectBase, SiblingCache>();

        private static ObjectBase FindBlocker(MotionController mc, ObjectBase me, bool backTaxi)
        {
            ObjectBase parent = mc.transform.parent != null ? mc.transform.parent.GetComponent<ObjectBase>() : null;
            if (parent == null)
            {
                return null;
            }

            SiblingCache cache = Siblings.GetOrCreateValue(parent);
            if (Time.unscaledTime >= cache.RefreshAt)
            {
                cache.RefreshAt = Time.unscaledTime + SiblingRefreshSec;
                cache.Items = parent.GetComponentsInChildren<ObjectBase>();
            }

            foreach (ObjectBase other in cache.Items)
            {
                if (other == null || other == me || !other.IsAirUnit)
                {
                    continue;
                }

                if (InCone(me, other, backTaxi))
                {
                    return other;
                }
            }

            return null;
        }

        private static bool IsBlockedBy(ObjectBase self, ObjectBase other)
        {
            return InCone(self, other, false);
        }

        private static bool InCone(ObjectBase from, ObjectBase to, bool backTaxi)
        {
            float interval = Math.Max(from._obp._taxiInterval, to._obp._taxiInterval);
            Vector3 delta = to.transform.position - from.transform.position;
            if (delta.magnitude > interval)
            {
                return false;
            }

            Vector3 forward = backTaxi ? -from.transform.forward : from.transform.forward;
            return Math.Abs(Utils.AngleOffAroundAxis(delta, forward, from.transform.up)) <= ConeDegrees;
        }

        private static void Fail(Exception ex)
        {
            ModLog.Error("TaxiDeadlockPatch", ex);
            if (++_failures >= MaxFailures)
            {
                _disabled = true;
                ModLog.Warn("TaxiDeadlockPatch disabled after repeated failures; vanilla taxi behavior restored.");
            }
        }
    }
}
