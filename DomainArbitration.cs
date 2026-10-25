using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // When both a fighter and a surface/land SAM unit of the same taskforce can engage an air target,
    // the unit that can get a missile onto the target soonest takes the shot and the other defers.
    internal static class DomainArbitration
    {
        private const float KnotsToUnity = 0.0076554087f;
        private const float MsToKnots = 1.94384f;
        private const float UnityUnitMeters = 67.2f;
        private const float MissileAvgSpeedFraction = 0.70f;

        // The rival must be at least this much faster (ratio of times) to claim the shot.
        private const float PriorityMargin = 0.85f;

        // A rival that cannot reach firing range + flight within this window does not block anyone.
        private const float PriorityHorizonSeconds = 90f;

        // Units farther than this multiple of their own weapon range skip arbitration entirely.
        private const float VicinityFactor = 1.5f;

        private const double CacheSeconds = 1.0;
        private const double LogIntervalSeconds = 20.0;

        private struct Entry
        {
            public double Time;
            public bool Defer;
        }

        private static readonly Dictionary<long, Entry> Cache = new Dictionary<long, Entry>();
        private static readonly Dictionary<long, double> LastLog = new Dictionary<long, double>();

        public static bool ShouldDefer(ObjectBase self, ObjectBase target)
        {
            if (self?._ai == null || target == null || self._taskforce == null)
            {
                return false;
            }

            long key = ((long)self.GetInstanceID() << 32) ^ (uint)target.GetInstanceID();
            double now = GameTime.missionElapsedTime;
            if (Cache.TryGetValue(key, out Entry cached) && now - cached.Time < CacheSeconds)
            {
                return cached.Defer;
            }

            bool defer = Compute(self, target, key, now);
            if (Cache.Count > 4000)
            {
                Cache.Clear();
            }

            Cache[key] = new Entry { Time = now, Defer = defer };
            return defer;
        }

        private static bool Compute(ObjectBase self, ObjectBase target, long key, double now)
        {
            float selfRange = self._ai.MaxRangeForObject(target);
            float dist = (target.transform.position - self.transform.position).magnitude;
            if (selfRange <= 0f || dist > selfRange * VicinityFactor)
            {
                return false;
            }

            float selfTime = TimeToImpact(self, target, dist);
            if (float.IsInfinity(selfTime))
            {
                return false;
            }

            bool selfIsAir = self.IsAirUnit;
            bool rivalDefensive = false;
            ObjectBase bestRival = null;
            float bestTime = selfTime * PriorityMargin;
            foreach (ObjectBase rival in self._taskforce._taskforceUnits)
            {
                if (rival == null || rival == self || rival.IsDestroyed || rival.transform == null
                    || rival.IsAirUnit == selfIsAir || rival._ai == null
                    || !(rival is Aircraft || rival is Vessel || rival is LandUnit))
                {
                    continue;
                }

                if (!IsEligible(rival, target))
                {
                    continue;
                }

                // A better-placed shooter that is itself under missile attack is busy defending.
                if (IsUnderMissileAttack(rival))
                {
                    rivalDefensive = true;
                    continue;
                }

                float rivalTime = TimeToImpact(rival, target, (target.transform.position - rival.transform.position).magnitude);
                if (rivalTime <= PriorityHorizonSeconds && rivalTime < bestTime)
                {
                    bestTime = rivalTime;
                    bestRival = rival;
                }
            }

            if (bestRival == null)
            {
                // The better shooter is defending, so we take over - unless a wingman of ours already has a
                // missile on the target, in which case that shot is already pressuring it.
                return rivalDefensive && MissileDoctrinePatch.WingmanHasViableMissile(target, self, now);
            }

            if (!LastLog.TryGetValue(key, out double last) || now - last > LogIntervalSeconds)
            {
                if (LastLog.Count > 4000)
                {
                    LastLog.Clear();
                }

                LastLog[key] = now;
                ModLog.Event($"t={now:0} DEFER: {self.getUIDAndName()} holds fire on {target.getUIDAndName()}; "
                             + $"{bestRival.getUIDAndName()} can hit in {bestTime:0}s vs {selfTime:0}s.");
            }

            return true;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WeaponBase, object> FirstSeen =
            new System.Runtime.CompilerServices.ConditionalWeakTable<WeaponBase, object>();

        // An enemy missile counts only while it is still guiding on this unit and has energy left.
        private static bool IsUnderMissileAttack(ObjectBase unit)
        {
            if (unit._incomingWeapons == null)
            {
                return false;
            }

            double now = GameTime.missionElapsedTime;
            foreach (WeaponBase weapon in unit._incomingWeapons)
            {
                if (!(weapon is Missile m) || m.IsDestroyed || m._launchPlatform == null
                    || m._launchPlatform._taskforce == unit._taskforce
                    || m.CurrentIntendedTargetObject != unit || m._targetLost || m.IsUnguidableOrphan)
                {
                    continue;
                }

                if (!FirstSeen.TryGetValue(m, out object boxed))
                {
                    boxed = now;
                    FirstSeen.Add(m, boxed);
                }

                // _remainingRangePercent is not initialised for the first moments after launch.
                if (now - (double)boxed >= 4.0 && m._remainingRangePercent <= 0f)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool IsEligible(ObjectBase rival, ObjectBase target)
        {
            if (rival is Aircraft && !rival._currentLoadoutCapabilities.HasAAM)
            {
                return false;
            }

            if (rival is Aircraft && rival.CurrentOrder.Value.OrderType == Order.Type.ReturnToBase)
            {
                return false;
            }

            AI ai = rival._ai;
            if (ai._objectToDestroy != null && ai._objectToDestroy != target)
            {
                return false;
            }

            if (rival.GetEngagePriority(target) >= 100)
            {
                return false;
            }

            if (rival._weaponStatus == ObjectBase.WeaponStatus.Free)
            {
                return true;
            }

            return rival._weaponStatus == ObjectBase.WeaponStatus.Tight
                   && (target.transform.position - rival.transform.position).magnitude <= ai.EngagementRangeForObject(target);
        }

        // Seconds until the shooter's best available missile could hit: travel to firing range plus flight time.
        private static float TimeToImpact(ObjectBase shooter, ObjectBase target, float dist)
        {
            float best = float.PositiveInfinity;
            foreach (Ammunition ammo in shooter._ai.AmmunitionForTarget(target, Ammunition.Type.Missile))
            {
                if (ammo?._ap == null)
                {
                    continue;
                }

                float avg = ammo._ap._maxVelocityInKnots * MissileAvgSpeedFraction * KnotsToUnity;
                float range = shooter.GetMaxRangeForAmmo(ammo, target);
                if (avg <= 0.0001f || range <= 0f)
                {
                    continue;
                }

                float time = Math.Min(dist, range) / avg;
                if (dist > range)
                {
                    float closing = ClosingSpeed(shooter, target);
                    if (closing <= 0.001f)
                    {
                        continue;
                    }

                    time += (dist - range) / closing;
                }

                best = Math.Min(best, time);
            }

            return best;
        }

        // Aircraft are assumed able to close at high speed rather than at their current cruise.
        private static float ClosingSpeed(ObjectBase shooter, ObjectBase target)
        {
            Vector3 los = (target.transform.position - shooter.transform.position).normalized;
            Vector3 ownVelocity = shooter._velocityVecInUnity;
            if (shooter is Aircraft aircraft && aircraft.Ap?._speedValuesInMach != null && aircraft.Ap._speedValuesInMach.Length > 0)
            {
                float[] mach = aircraft.Ap._speedValuesInMach;
                float altMeters = Math.Max(0f, aircraft.transform.position.y * UnityUnitMeters);
                float topUnits = mach[mach.Length - 1] * Atmosphere.SpeedOfSound(altMeters) * MsToKnots * KnotsToUnity;
                ownVelocity = los * Math.Max(shooter._velocityInUnity, 0.8f * topUnits);
            }

            return Vector3.Dot(ownVelocity - target._velocityVecInUnity, los);
        }
    }
}
