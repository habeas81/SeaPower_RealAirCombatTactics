using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Phase 1: shoot-look-shoot. A fighter (or its taskforce mates) may not treat an air target as
    // "unhandled" while an earlier AAM from the same side can still plausibly hit it.
    [HarmonyPatch(typeof(AI), "CheckIfAlreadyHandled")]
    public static class MissileDoctrinePatch
    {
        // knots -> Unity units per second (1 unit ~= 67.2 m)
        private const float KnotsToUnityPerSec = 0.0076554087f;

        // Per-shot hold time = (distance / max missile speed) * (1 + TofMarginFraction) + TofBufferSeconds
        private const float TofMarginFraction = 0.30f;
        private const float TofBufferSeconds = 15f;

        // Delay after the last viable missile disappears (covers missile/target destruction ordering).
        private const double GraceSeconds = 3.0;

        // A condition must persist this long before a missile is declared unable to hit.
        private const double HopelessDebounceSeconds = 3.0;

        // _targetLost must stay set this long (no re-acquisition, e.g. after chaff/flares) to count.
        private const double TargetLostSeconds = 2.5;

        // After this many exceptions the patch disables itself and the game falls back to vanilla behavior.
        private const int MaxFailures = 25;

        private static int _failures;
        private static bool _disabled;
        private static bool _loggedFirstCall;

        private class MissileTrack
        {
            public double Deadline;
            public double Born;
            public double PassedSince = -1.0;
            public double BlindSince = -1.0;
            public double LostSince = -1.0;
            public bool ReleaseLogged;
            public bool EndLogged;
            public double LastTelemetry = -100.0;
            public bool SeenState;
            public bool LastLost;
            public bool LastSeekerOn;
            public bool LastLocked;
            public bool LastBurning;
            public bool LastInFov;
        }

        private const double TelemetryIntervalSeconds = 2.0;

        // No "cannot hit" verdicts until the missile has flown this long.
        private const double SettleSeconds = 2.0;

        private class TargetState
        {
            public double LastViable;
            public bool Holding;
            public double LastHoldLog = -100.0;
            public double LastAllowLog = -100.0;
        }

        private static readonly ConditionalWeakTable<Missile, MissileTrack> Tracks = new ConditionalWeakTable<Missile, MissileTrack>();
        private static readonly ConditionalWeakTable<ObjectBase, TargetState> Targets = new ConditionalWeakTable<ObjectBase, TargetState>();

        // ____baseObject = Harmony's three underscores + the private field name "_baseObject".
        private static void Postfix(ObjectBase targetObject, ref bool __result, ObjectBase ____baseObject)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                if (!_loggedFirstCall)
                {
                    _loggedFirstCall = true;
                    ModLog.Info("MissileDoctrinePatch is live: CheckIfAlreadyHandled hook was called by the game.");
                }

                bool vanillaHandled = __result;
                // Only fighters and surface/land SAM shooters, and only against aircraft/helicopter targets.
                // Torpedoes, ASW and anti-ship engagements never reach the logic below.
                bool isAircraft = ____baseObject is Aircraft;
                bool isSurface = ____baseObject is Vessel || ____baseObject is LandUnit;
                if (targetObject == null || targetObject.IsDestroyed || !(isAircraft || isSurface)
                    || !ShootLookShootDoctrine.IsEnabledFor(____baseObject))
                {
                    return;
                }

                Stats.AircraftCalls++;
                if (!targetObject.isAirTarget() || targetObject.isWeapon() || targetObject._incomingWeapons == null)
                {
                    return;
                }

                Stats.AirTargetCalls++;
                if (vanillaHandled)
                {
                    Stats.VanillaHandled++;
                }

                if (isSurface)
                {
                    EvaluateSurfaceShooter(targetObject, ____baseObject, ref __result);
                }
                else
                {
                    // Always evaluate (even if vanilla already says "handled") so missiles are tracked and logged.
                    EvaluateHold(targetObject, ____baseObject, vanillaHandled, ref __result);
                }

                if (!__result && DomainArbitration.ShouldDefer(____baseObject, targetObject))
                {
                    __result = true;
                }

                Stats.Heartbeat();
            }
            catch (Exception ex)
            {
                // Never let a mod error propagate into the game's AI loop.
                ModLog.Error("MissileDoctrinePatch.Postfix", ex);
                if (++_failures >= MaxFailures)
                {
                    _disabled = true;
                    ModLog.Warn($"MissileDoctrinePatch disabled after {MaxFailures} exceptions; vanilla behavior restored.");
                }
            }
        }

        // Vanilla ignores fighter missiles when a ship decides whether to shoot at an air target.
        // A ship holds while a friendly aircraft's missile is still viable; its own salvo logic is untouched.
        private static void EvaluateSurfaceShooter(ObjectBase target, ObjectBase shooter, ref bool result)
        {
            double now = GameTime.missionElapsedTime;
            foreach (WeaponBase weapon in target._incomingWeapons)
            {
                if (weapon is Missile m && m._launchPlatform != null && m._launchPlatform.IsAirUnit
                    && IsViableFriendlyAam(weapon, target, shooter, now))
                {
                    result = true;
                    TargetState state = Targets.GetValue(target, _ => new TargetState());
                    if (now - state.LastHoldLog > 10.0)
                    {
                        state.LastHoldLog = now;
                        ModLog.Event($"t={now:0} HOLD FIRE: {Name(shooter)} defers to friendly aircraft missile on {Name(target)}.");
                    }

                    return;
                }
            }
        }

        private static void EvaluateHold(ObjectBase target, ObjectBase shooter, bool vanillaHandled, ref bool result)
        {
            double now = GameTime.missionElapsedTime;
            TargetState state = Targets.GetValue(target, _ => new TargetState());

            // A wingman may take a "pressure" shot to force the attacker defensive, even while a
            // squadron-mate's missile is already in flight at this target. Its own missile still holds it.
            if (PressureShot.IsActive(shooter, target, out ObjectBase victim) && !HasOwnMissileInFlight(target, shooter))
            {
                if (now - state.LastAllowLog > 5.0)
                {
                    state.LastAllowLog = now;
                    ModLog.Event($"t={now:0} PRESSURE SHOT: {Name(shooter)} cleared to fire at {Name(target)} (it has a missile in flight at {Name(victim)}); forcing it defensive.");
                }

                result = false;
                return;
            }

            int viable = 0;
            int friendly = 0;
            foreach (WeaponBase weapon in target._incomingWeapons)
            {
                if (weapon is Missile m && m._launchPlatform != null && m._launchPlatform._taskforce == shooter._taskforce)
                {
                    friendly++;
                }

                if (IsViableFriendlyAam(weapon, target, shooter, now))
                {
                    viable++;
                }
            }

            if (viable > 0)
            {
                state.LastViable = now;
                if (!vanillaHandled)
                {
                    // Vanilla would have allowed another launch here; this is the case the mod fixes.
                    Stats.OverriddenToHold++;
                    ModLog.Event($"t={now:0} OVERRIDE: vanilla would allow {Name(shooter)} to fire at {Name(target)}; {viable} friendly AAM(s) still viable, holding.");
                }

                result = true;
                if (!state.Holding || now - state.LastHoldLog > 10.0)
                {
                    ModLog.Event($"t={now:0} HOLD FIRE: {Name(shooter)} blocked from re-engaging {Name(target)}; {viable} friendly AAM(s) still viable (vanilla handled={vanillaHandled}).");
                    state.LastHoldLog = now;
                }

                state.Holding = true;
                return;
            }

            if (now - state.LastViable < GraceSeconds && state.LastViable > 0.0)
            {
                result = true;
                return;
            }

            if (!vanillaHandled && friendly > 0 && now - state.LastAllowLog > 5.0)
            {
                state.LastAllowLog = now;
                if (ModLog.Verbose) ModLog.Event($"t={now:0} ALLOW: {Name(shooter)} may fire at {Name(target)}; {friendly} friendly missile(s) present but none viable.");
            }

            if (state.Holding)
            {
                state.Holding = false;
                string status = target.IsDestroyed ? "target destroyed" : "target still alive";
                if (ModLog.Verbose) ModLog.Event($"t={now:0} CLEAR TO FIRE: {Name(shooter)} may engage {Name(target)} again ({status}).");
            }
        }

        // Counters and a 30s heartbeat so the log shows the hook is working even when no missile is in flight.
        private static class Stats
        {
            public static int AircraftCalls, AirTargetCalls, VanillaHandled, OverriddenToHold;
            private static double _next = -1.0;

            public static void Heartbeat()
            {
                double now = GameTime.missionElapsedTime;
                if (_next < 0.0 || now < _next - 30.0)
                {
                    _next = now + 30.0;
                    return;
                }

                if (now >= _next)
                {
                    _next = now + 30.0;
                    if (ModLog.Verbose) ModLog.Event($"t={now:0} HEARTBEAT: aircraft checks={AircraftCalls}, vs air targets={AirTargetCalls}, vanilla already-handled={VanillaHandled}, overridden to hold={OverriddenToHold}.");
                }
            }

            public static void Reset()
            {
                AircraftCalls = AirTargetCalls = VanillaHandled = OverriddenToHold = 0;
                _next = -1.0;
            }
        }

        // True if another unit of the shooter's domain (air vs surface) has a viable missile on the target.
        internal static bool WingmanHasViableMissile(ObjectBase target, ObjectBase shooter, double now)
        {
            if (target._incomingWeapons == null)
            {
                return false;
            }

            foreach (WeaponBase weapon in target._incomingWeapons)
            {
                if (weapon is Missile m && m._launchPlatform != null && m._launchPlatform != shooter
                    && m._launchPlatform.IsAirUnit == shooter.IsAirUnit
                    && IsViableFriendlyAam(weapon, target, shooter, now))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasOwnMissileInFlight(ObjectBase target, ObjectBase shooter)
        {
            foreach (WeaponBase weapon in target._incomingWeapons)
            {
                if (weapon is Missile m && !m.IsDestroyed && m._launchPlatform == shooter)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsViableFriendlyAam(WeaponBase weapon, ObjectBase target, ObjectBase shooter, double now)
        {
            if (!(weapon is Missile missile) || missile._ap == null)
            {
                return false;
            }

            if (missile.IsDestroyed)
            {
                if (Tracks.TryGetValue(missile, out MissileTrack ended) && !ended.EndLogged)
                {
                    ended.EndLogged = true;
                    if (ModLog.Verbose) ModLog.Event($"t={now:0} MISSILE END: {missile._ap._ammunitionFileName} destroyed (target {Name(target)} {(target.IsDestroyed ? "destroyed" : "still alive")}).");
                }

                return false;
            }

            ObjectBase launcher = missile._launchPlatform;
            if (launcher == null || launcher._taskforce != shooter._taskforce)
            {
                return false;
            }

            // A surface shooter's own salvo is governed by vanilla saturation logic.
            if (!shooter.IsAirUnit && launcher == shooter)
            {
                return false;
            }

            MissileTrack track = Tracks.GetValue(missile, m =>
            {
                double hold = ComputeHoldSeconds(m, target);
                if (ModLog.Verbose) ModLog.Event($"t={now:0} TRACK: {Name(launcher)} fired {m._ap._ammunitionFileName} at {Name(target)}; hold budget {hold:0}s (max speed {m._ap._maxVelocityInKnots:0} kt).");
                return new MissileTrack { Deadline = now + hold, Born = now };
            });

            if (ModLog.Verbose)
            {
                LogMissileState(missile, target, track, now);
            }

            // Telemetry like _remainingRangePercent is not initialised in the first frames after launch.
            bool settled = now - track.Born >= SettleSeconds;

            string reason = null;
            if (now > track.Deadline)
            {
                reason = "time budget expired";
            }
            else if (!settled)
            {
                reason = null;
            }
            else if (missile.IsUnguidableOrphan)
            {
                reason = "launch platform lost; missile unguided";
            }
            else if (missile._remainingRangePercent <= 0f)
            {
                reason = "out of range";
            }
            else
            {
                reason = CannotHit(missile, target, track, now);
            }

            if (reason != null)
            {
                LogRelease(missile, track, target, reason);
                return false;
            }

            return true;
        }

        // Logs state changes immediately and a status line every TelemetryIntervalSeconds.
        private static void LogMissileState(Missile missile, ObjectBase target, MissileTrack track, double now)
        {
            SeekerBase seeker = missile._seeker;
            bool seekerOn = seeker != null && seeker.isActive();
            bool locked = seeker != null && seeker._lockedOnTarget != null;
            bool lost = missile._targetLost;
            bool burning = missile.IsMotorBurning;

            Vector3 toTarget = target.transform.position - missile.transform.position;
            float rangeUnity = toTarget.magnitude;
            float rangeNm = rangeUnity / 27.559496f;
            float closingKt = rangeUnity > 1f
                ? Vector3.Dot(missile._velocityVecInUnity - target._velocityVecInUnity, toTarget / rangeUnity) / KnotsToUnityPerSec
                : 0f;
            float offBoresight = Vector3.Angle(missile.transform.forward, toTarget);
            bool inFov = seeker != null && seeker.isInSensorFOV(missile.transform.position, missile.transform.forward, target);

            string ammo = missile._ap._ammunitionFileName;
            if (track.SeenState)
            {
                if (lost != track.LastLost)
                {
                    ModLog.Event($"t={now:0} MISSILE {ammo}: {(lost ? "LOST TARGET" : "RE-ACQUIRED target")} (range {rangeNm:0.0} nm, off-boresight {offBoresight:0} deg).");
                }

                if (seekerOn != track.LastSeekerOn)
                {
                    ModLog.Event($"t={now:0} MISSILE {ammo}: seeker {(seekerOn ? "ACTIVE (terminal)" : "OFF")} at {rangeNm:0.0} nm.");
                }

                if (locked != track.LastLocked)
                {
                    ModLog.Event($"t={now:0} MISSILE {ammo}: seeker {(locked ? "LOCKED ON" : "lock dropped")}.");
                }

                if (burning != track.LastBurning && !burning)
                {
                    ModLog.Event($"t={now:0} MISSILE {ammo}: motor burnout at {rangeNm:0.0} nm, speed {missile._velocityInKnots:0} kt.");
                }

                if (inFov != track.LastInFov)
                {
                    ModLog.Event($"t={now:0} MISSILE {ammo}: target {(inFov ? "entered" : "LEFT")} seeker FOV (off-boresight {offBoresight:0} deg).");
                }
            }

            track.SeenState = true;
            track.LastLost = lost;
            track.LastSeekerOn = seekerOn;
            track.LastLocked = locked;
            track.LastBurning = burning;
            track.LastInFov = inFov;

            if (now - track.LastTelemetry >= TelemetryIntervalSeconds)
            {
                track.LastTelemetry = now;
                ModLog.Event($"t={now:0} MISSILE {ammo} -> {Name(target)}: range {rangeNm:0.0} nm, closing {closingKt:0} kt, speed {missile._velocityInKnots:0} kt, "
                             + $"off-boresight {offBoresight:0} deg, seeker {(seekerOn ? "on" : "off")}/{(locked ? "locked" : "unlocked")}/{(inFov ? "inFOV" : "outFOV")}, "
                             + $"{(burning ? "motor burning" : "coasting")}, targetLost={lost}, rangeLeft={missile._remainingRangePercent:0}%, "
                             + $"budget left {track.Deadline - now:0}s.");
            }
        }

        private static void LogRelease(Missile missile, MissileTrack track, ObjectBase target, string reason)
        {
            if (track != null)
            {
                if (track.ReleaseLogged)
                {
                    return;
                }

                track.ReleaseLogged = true;
            }
            else if (!Tracks.TryGetValue(missile, out track) || track.ReleaseLogged)
            {
                return;
            }
            else
            {
                track.ReleaseLogged = true;
            }

            if (ModLog.Verbose) ModLog.Event($"t={GameTime.missionElapsedTime:0} RELEASE: {missile._ap._ammunitionFileName} no longer counted against {Name(target)}: {reason}.");
        }

        // Per-shot time budget from the missile's top speed and its distance to the target.
        private static double ComputeHoldSeconds(Missile missile, ObjectBase target)
        {
            float maxSpeed = missile._ap._maxVelocityInKnots * KnotsToUnityPerSec;
            if (maxSpeed < 0.01f)
            {
                maxSpeed = Mathf.Max(missile._velocityInUnity, 0.01f);
            }

            float distance = (target.transform.position - missile.transform.position).magnitude;
            return (distance / maxSpeed) * (1f + TofMarginFraction) + TofBufferSeconds;
        }

        // Returns a reason string when a live missile can no longer reach the target, else null.
        private static string CannotHit(Missile missile, ObjectBase target, MissileTrack track, double now)
        {
            // The game clears _targetLost when the seeker re-acquires, which resets this timer.
            if (missile._targetLost)
            {
                if (track.LostSince < 0.0) track.LostSince = now;
                if (now - track.LostSince > TargetLostSeconds) return $"target lost for {now - track.LostSince:0.0}s without re-acquisition";
            }
            else
            {
                track.LostSince = -1.0;
            }

            Vector3 toTarget = target.transform.position - missile.transform.position;
            float range = toTarget.magnitude;

            // Flew past: range is opening (negative closing speed) beyond the initial boost phase.
            bool opening = range > 1f && Vector3.Dot(missile._velocityVecInUnity - target._velocityVecInUnity, toTarget / range) < 0f;
            if (opening && !missile.IsMotorBurning)
            {
                if (track.PassedSince < 0.0) track.PassedSince = now;
                if (now - track.PassedSince > HopelessDebounceSeconds) return "missile flew past / range opening";
            }
            else
            {
                track.PassedSince = -1.0;
            }

            // Seeker is on but holds no lock and the target is outside its field of view.
            SeekerBase seeker = missile._seeker;
            bool blind = seeker != null && seeker.isActive() && seeker._lockedOnTarget == null
                         && !seeker.isInSensorFOV(missile.transform.position, missile.transform.forward, target);
            if (blind)
            {
                if (track.BlindSince < 0.0) track.BlindSince = now;
                if (now - track.BlindSince > HopelessDebounceSeconds) return "target outside seeker FOV with no lock";
            }
            else
            {
                track.BlindSince = -1.0;
            }

            return null;
        }

        private static string Name(ObjectBase obj)
        {
            try
            {
                return obj == null ? "null" : obj.getUIDAndName();
            }
            catch
            {
                return "?";
            }
        }
    }
}







