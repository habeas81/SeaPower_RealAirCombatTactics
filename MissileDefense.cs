using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Automated missile defence for fixed-wing aircraft (AI and player, all sides). It replaces the game's
    // AdvancedMissileEvasion state while active. The decision flow, per threatening missile M (taken from the
    // aircraft's _dangerousMissiles, i.e. launches/warnings the aircraft actually knows about):
    //
    //   Radar missile, guided by an aircraft whose radar we can notch (Fox 1 semi-active, or Fox 3 before pitbull):
    //     NOTCH    - afterburner + hard turn to the beam of the guiding radar, below it (look-down clutter geometry).
    //     REVERSAL - once inside the notch: chaff, then a vertical reversal (Split-S, or Immelman if there is no room
    //                to go down) that keeps the velocity perpendicular to the line of sight so we stay notched while
    //                the flight path reverses; chaff also widens our notch for a few seconds (NotchModel bloom).
    //     HOLD     - re-validate the notch and keep the beam in full afterburner.
    //   Fox 3 gone active (pitbull, RWR sees it):
    //     PITBULL  - afterburner + chaff + turn to the beam of the MISSILE's seeker, below it, and hold the notch.
    //   Anything else (IR / Fox 2, SAMs, unnotchable guide), or when the notch has not broken lock after a while:
    //     KINEMATIC - fly away in full afterburner, alternating left/right of the missile's line and climbing in steps,
    //                 re-timed every 5-10 s (faster missile = shorter legs) so the missile has to turn and climb
    //                 and bleeds energy.
    //   Last resort, ANY missile type, still tracking us a few seconds from impact:
    //     LAST-DITCH - roll the lift vector toward the missile (missile directly "up"), chaff/flares, and ~1 s out
    //                  pull maximum elevator so the missile has to pull hard to follow.
    public static class MissileDefense
    {
        public static readonly bool Enabled = true;

        private const float LastDitchStartSec = 3f;      // time-to-impact at which the lift vector is rolled to the missile
        private const float PullStartSec = 1.0f;         // time-to-impact at which max elevator is applied
        private const float LiftAlignedDeg = 20f;        // roll error below which the lift vector counts as on the missile
        private const float KinematicAfterSec = 30f;     // notch held this long and the missile still tracks -> kinematic
        private const float PitbullPrepNm = 12f;         // Fox 3 inside this range is notched as if already active (seeker goes live ~9 nm)
        private const float NotchGiveUpSec = 25f;        // could not reach the notch within this long -> kinematic
        private const float MinKinematicHoldSec = 6f;    // never flip out of kinematic faster than this
        private const float GraceSec = 1.5f;             // keep the controller this long after the last valid threat
        private const float RollGainDeg = 30f;           // roll error (deg) that gives full roll input
        private const float LoopG = 7f;                  // g assumed for sizing the vertical reversal
        private const float ReversalTurnDeg = 165f;      // heading change that completes the half loop
        private const float LevelOutSinPitch = 0.12f;    // |sin(pitch)| below which the loop counts as levelled out (~7 deg)
        private const float MaxReversalSec = 20f;        // safety timeout of the reversal
        private const float MinSplitSFloorFt = 2500f;    // extra height over terrain required beyond the loop diameter
        private const float KinOffsetDeg = 60f;          // kinematic legs fly this far either side of directly away
        private const float KinLegMinSec = 20f;          // weave legs: long enough for the missile to commit to each turn and bleed energy
        private const float KinLegMaxSec = 30f;
        private const float KinClimbStepFt = 4000f;      // altitude gained per step of the weave (3 steps, then back)
        private const float KinMaxAltFt = 42000f;
        private const float AimDistanceNm = 20f;
        private const float MinNotchRangeNm = 2f;
        private const double LogSeconds = 2.0;
        private const float FtPerMetre = 3.28084f;

        // Sign of controlInput.x that pulls the nose up. Derived from the flight model's own pitch-control formula;
        // verified at run time during the first pull and flipped if the measured turn is the wrong way.
        private static float _pullSign = -1f;

        private enum Phase { Notch, Reversal, Hold, Pitbull, Kinematic, LastDitch }

        private enum RevStep { Roll, Pull, Upright, Done }

        private class Ctl
        {
            public Missile M;
            public Phase Phase;
            public double PhaseStart;
            public double NotchStart;       // when the current radar stage (launcher / pitbull) began
            public int Stage = -1;          // 0 = launcher notch stage, 1 = pitbull stage
            public double NotchedSince = -1; // Hold: when we were first (re)confirmed inside the notch, -1 = not yet
            public bool KinFallback;        // this stage failed to break lock: stay kinematic until the stage changes
            public double LastThreat;
            public double NextLog;
            public ObjectBase Emitter;
            public float Side;

            public bool Taken;
            public bool PrevPosOverride;
            public bool PrevAltOverride;
            public bool PrevDirectPoint;
            public double NextClearLog;
            public float PrevAltitude;
            public float PrevSpeedKt;
            public float PrevMach;          // > 0 when the command in force before the defence was a Mach command

            public RevStep Step;
            public bool Immelman;
            public float Heading0;
            public double StepStart;
            public Vector3 PullDir0;
            public bool Learned;
            public bool SawSteep;           // reversal loop has passed through the vertical

            public int Leg;
            public double LegStart;
            public float LegSec;
            public float BaseAlt;

            public bool LdChaff;
            public bool LdPullChaff;

            public bool HasWant;            // WantAlt/WantPos hold the latest demand of the defence
            public float WantAlt;
            public Vector3 WantPos;
            public double NextAimRoll;      // crew skill: next time the aim error is re-rolled
            public float AimErrorDeg;       // crew skill: current heading error applied to the beam aim

            public List<ObjectBase> OffTargets = new List<ObjectBase>(); // what the aircraft was attacking
            public ObjectBase OffPrimary;
        }

        // Crew skill: aircraft that know about a missile but have not yet "recognised" the threat (aircraft -> when
        // they will react).
        private static readonly Dictionary<Aircraft, double> ReactAt = new Dictionary<Aircraft, double>();

        private static readonly Dictionary<Aircraft, Ctl> Active = new Dictionary<Aircraft, Ctl>();

        public static bool IsActive(Aircraft ac) => ac != null && Active.ContainsKey(ac);

        // Short map label of the current defence phase (null when not defending); see MapEmissionsOverlayPatch.
        public static string Label(Aircraft ac)
        {
            if (ac == null || !Active.TryGetValue(ac, out Ctl c))
            {
                return null;
            }

            switch (c.Phase)
            {
                case Phase.Notch: return "DEF-NOTCH";
                case Phase.Reversal: return "DEF-REV";
                case Phase.Hold: return "DEF-HOLD";
                case Phase.Pitbull: return "DEF-PIT";
                case Phase.LastDitch: return "DEF-LDM";
                default: return "DEF-KIN";
            }
        }

        // Called right before the flight model turns the commands into control demands: puts the defence's altitude,
        // steering point and speed back over anything the game's own state machine wrote since our last tick.
        public static void Reassert(Aircraft ac)
        {
            if (!Active.TryGetValue(ac, out Ctl c) || !c.HasWant)
            {
                return;
            }

            if (c.Phase == Phase.Reversal || c.Phase == Phase.LastDitch)
            {
                return;
            }

            ac.DesiredAltitude.Value = c.WantAlt;
            if (ac.Motioncontroller != null)
            {
                // Pursuit states pin the altitude with this flag, which would ignore the defence's DesiredAltitude (dive below the emitter).
                ac.Motioncontroller.commandAltitudeOverride = false;
                // Formation / engage states set this, and the flight model then ignores DesiredAltitude (wingmen never dived below the emitter).
                ac.Motioncontroller._directPoint = false;
                ac.Motioncontroller.CommandPosition = c.WantPos;
            }

            EnsureSpeed(ac, c);
        }

        // Whether this aircraft's missile evasion is ours rather than the game's (mirrors the vanilla conditions).
        public static bool Eligible(Aircraft ac)
        {
            if (!Enabled || ac == null || ac.IsDestroyed || !ac.HasControl || ac.isLanding)
            {
                return false;
            }

            if (!Globals.useAdvancedEvasion || !ac.CanEvadeMissiles())
            {
                return false;
            }

            if (ac.IsPlayerObject && (!Singleton<OptionsManager>.Instance.AircraftMissileEvasion || ac._playerCommandOverride))
            {
                return false;
            }

            MotionController mc = ac.Motioncontroller;
            return mc is FixedWingFlightPhysics || IsFormation(mc);
        }

        private static readonly Dictionary<Aircraft, double> NextIneligibleLog = new Dictionary<Aircraft, double>();

        // A known missile is aimed at an aircraft but no defence will run: say why (once per 10 s), so "the aircraft
        // just flew straight" reports can be traced to a game option, an attack mode or a missing flight model.
        private static void LogIneligible(Aircraft ac, Missile m, double now)
        {
            if (NextIneligibleLog.TryGetValue(ac, out double next) && now < next)
            {
                return;
            }

            NextIneligibleLog[ac] = now + 10.0;
            string why;
            if (!Enabled) why = "defence disabled";
            else if (ac.IsDestroyed || !ac.HasControl) why = "no control";
            else if (ac.isLanding) why = "landing";
            else if (!Globals.useAdvancedEvasion) why = "game option AdvancedAircraftEvasion is off";
            else if (ac._inLevelAttackMode || ac._inDiveAttackMode) why = "in a level/dive bombing attack";
            else if (ac.Ap._isLarge) why = "large aircraft";
            else if (ac.IsPlayerObject && !Singleton<OptionsManager>.Instance.AircraftMissileEvasion) why = "game option AircraftMissileEvasion is off";
            else if (ac.IsPlayerObject && ac._playerCommandOverride) why = "player command override";
            else why = "unsupported flight model " + (ac.Motioncontroller != null ? ac.Motioncontroller.GetType().Name : "none");
            ModLog.Event($"DEFENSE not started {ac.getUIDAndName()} vs {m.getUIDAndName()}: {why}");
        }

        // FormationFlightPhysics is internal to the game assembly, so match it by name.
        private static bool IsFormation(MotionController mc) => mc != null && mc.GetType().Name == "FormationFlightPhysics";

        public static void Tick(Aircraft ac)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                Active.TryGetValue(ac, out Ctl c);
                if (c != null && !Eligible(ac))
                {
                    End(ac, c, "no longer eligible");
                    return;
                }

                Missile m = PickThreat(ac);
                double now = GameTime.missionElapsedTime;
                if (c == null)
                {
                    if (m == null || !Eligible(ac))
                    {
                        if (m == null)
                        {
                            TryResume(ac, now);
                        }

                        ReactAt.Remove(ac);
                        if (m != null)
                        {
                            LogIneligible(ac, m, now);
                        }

                        return;
                    }

                    // Crew skill: lower-skilled crews take a while to recognise and react to the threat.
                    if (!ReactAt.TryGetValue(ac, out double react))
                    {
                        float delay = CrewSkillModel.RollReactionSec(ac);
                        react = now + delay;
                        ReactAt[ac] = react;
                        if (delay > 0.01f)
                        {
                            ModLog.Event($"DEFENSE {ac.getUIDAndName()} threat noticed, reacting in {delay:F1}s (crew {ac._crew?.OverallSkill})");
                        }
                    }

                    if (now < react)
                    {
                        return;
                    }

                    // Long shot: if our own guided missile needs us for far less time than the threat needs to arrive,
                    // keep supporting it and defend afterwards.
                    if (ShouldDeferForOwnMissiles(ac, m, now))
                    {
                        return;
                    }

                    ReactAt.Remove(ac);
                    c = Begin(ac, m);
                    if (c == null)
                    {
                        return;
                    }
                }

                if (m == null)
                {
                    // A reversal in progress runs to completion even if the missile has just been decoyed/lost:
                    // dropping the controls mid-loop leaves the jet inverted or 180 deg off and it starts over.
                    if (c.Phase == Phase.Reversal && now - c.PhaseStart < MaxReversalSec
                        && ac.Motioncontroller is FixedWingFlightPhysics rmc)
                    {
                        AbortOffence(ac, c);
                        rmc.commandPositionOverride = true;
                        RunReversal(ac, c, rmc, now);
                        c.WantAlt = ac.DesiredAltitude.Value;
                        c.WantPos = rmc.CommandPosition;
                        return;
                    }

                    if (now - c.LastThreat > GraceSec)
                    {
                        End(ac, c, "threat resolved: " + WhyNotThreat(ac, c.M));
                    }

                    return;
                }

                c.LastThreat = now;
                c.M = m;
                Run(ac, c, m, now);
            }
            catch (Exception ex)
            {
                ModLog.Error("MissileDefense.Tick", ex);
                if (Active.TryGetValue(ac, out Ctl bad))
                {
                    End(ac, bad, "error");
                }
            }
        }

        // The known, still-guiding missile that will arrive first. The missile we are already defending against stays
        // the threat while it is alive and still aimed at us, even if the aircraft's own warning list drops it (e.g.
        // RWR/plot refresh while manoeuvring hard), so the defence is never abandoned with a live missile inbound.
        private static Missile PickThreat(Aircraft ac)
        {
            Missile best = null;
            float bestRange = float.MaxValue;
            List<Missile> list = ac._dangerousMissiles;
            for (int i = 0; i < list.Count; i++)
            {
                Missile m = list[i];
                if (m == null || m.IsDestroyed || !AimedAt(m, ac) || m._targetLost || LinkDead(m))
                {
                    continue;
                }

                float r = (m.transform.position - ac.transform.position).sqrMagnitude;
                if (r < bestRange)
                {
                    bestRange = r;
                    best = m;
                }
            }

            if (best == null && Active.TryGetValue(ac, out Ctl c) && c.M != null && !c.M.IsDestroyed
                && AimedAt(c.M, ac) && !c.M._targetLost && !LinkDead(c.M))
            {
                best = c.M;
            }

            return best;
        }

        // A missile whose guidance link is gone for good is harmless only if its own seeker has not taken over:
        // an active (pitbull) missile no longer needs the link, and the game sets ConnectionLostForever on it.
        private static bool LinkDead(Missile m) =>
            m.ConnectionLostForever.Value && !(m._seeker != null && m._seeker.isActive());

        // The missile is aimed at us. While the notch has forced its mid-course link down (MissileNotchLockPatch) the
        // game's CurrentTarget is empty for some guidance types, so the intended target is used instead.
        private static bool AimedAt(Missile m, Aircraft ac) =>
            m.CurrentTarget == ac || (MissileNotchLockPatch.IsForcedLost(m) && m.CurrentIntendedTargetObject == ac);

        // Diagnostic for the end of a defence: why the last missile no longer counts as a threat.
        private static string WhyNotThreat(Aircraft ac, Missile m)
        {
            if (m == null)
            {
                return "no missile";
            }

            return $"destroyed={m.IsDestroyed} target={(m.CurrentTarget != null ? m.CurrentTarget.getUIDAndName() : "none")} targetLost={m._targetLost} forever={m.ConnectionLostForever.Value} inList={ac._dangerousMissiles.Contains(m)}";
        }

        private static Ctl Begin(Aircraft ac, Missile m)
        {
            if (IsFormation(ac.Motioncontroller))
            {
                ac.SetFlightPhysicsModel(isStartingAirborne: true);
            }

            if (!(ac.Motioncontroller is FixedWingFlightPhysics mc))
            {
                return null;
            }

            if (NotchGuidance.IsActive(ac))
            {
                NotchGuidance.Stop(ac, "missile defence takes over");
            }

            var c = new Ctl
            {
                Taken = true,
                PrevPosOverride = mc.commandPositionOverride,
                PrevAltOverride = mc.commandAltitudeOverride,
                PrevDirectPoint = mc._directPoint,
                PrevAltitude = ac.DesiredAltitude.Value,
                PrevSpeedKt = ac.SpeedCommand.Value != null ? ac.SpeedCommand.Value.SpeedInKnots : 0f,
                PrevMach = ac.SpeedCommand.Value is ConstantMach ? ac.SpeedCommand.Value.SpeedInMach : 0f,
                LastThreat = GameTime.missionElapsedTime,
                Phase = Phase.Kinematic,
                PhaseStart = GameTime.missionElapsedTime
            };
            Active[ac] = c;
            if (Pending.TryGetValue(ac, out PendingEngage prior))
            {
                // A second threat before we could re-engage: keep what we were attacking.
                Pending.Remove(ac);
                c.OffTargets.AddRange(prior.Targets);
                c.OffPrimary = prior.Primary;
            }

            Describe(ac, m, out string kind, out _, out _, out _, out float rangeNm, out float tti);
            ModLog.Event($"DEFENSE start {ac.getUIDAndName()} vs {m.getUIDAndName()} kind={kind} range={rangeNm:F1}nm tti={tti:F0}s launcher={(m._launchPlatform != null ? m._launchPlatform.getUIDAndName() : "none")} guidingRadarOwner={(m._guidingRadarSystem != null && m._guidingRadarSystem._baseObject != null ? m._guidingRadarSystem._baseObject.getUIDAndName() : "none")}");
            return c;
        }

        private static void End(Aircraft ac, Ctl c, string why)
        {
            Active.Remove(ac);
            if (ac == null)
            {
                return;
            }

            if (ac.Motioncontroller is FixedWingFlightPhysics mc)
            {
                mc.commandPositionOverride = c.PrevPosOverride;
                mc.commandAltitudeOverride = c.PrevAltOverride;
                mc._directPoint = c.PrevDirectPoint;
                mc.controlOverride = false;
                mc.controlInput = Vector3.zero;
            }

            if (!ac.IsDestroyed)
            {
                ac.DesiredAltitude.Value = c.PrevAltitude;
                // Restore the same kind of command (Mach or knots); never a zero-speed one (that stalls the jet).
                if (c.PrevMach > 0.05f)
                {
                    ac.SetSpeedCommand(new ConstantMach(c.PrevMach, ac));
                }
                else
                {
                    float kt = c.PrevSpeedKt > 50f ? c.PrevSpeedKt : Mathf.Max(ac.getVelocityInKnots(), 300f);
                    ac.SetSpeedCommand(new ConstantSpeed(kt, ac));
                }
            }

            ModLog.Event($"DEFENSE end {ac.getUIDAndName()}: {why}");

            // Safe again: remember what the aircraft was attacking so it can turn back in (see TryResume).
            if (c.OffTargets.Count > 0 && !ac.IsDestroyed && why != "error")
            {
                double now = GameTime.missionElapsedTime;
                Pending[ac] = new PendingEngage
                {
                    Targets = c.OffTargets,
                    Primary = c.OffPrimary,
                    At = now + ReengageDelaySec,
                    Expires = now + ReengageWindowSec
                };
            }
        }

        // ---- Offence management during a defence -----------------------------------------------------------------

        private const float DeferMarginSec = 25f;      // a defence needs about this long before impact to be worth starting
        private const float ReengageDelaySec = 6f;     // calm period after the defence ends before turning back in
        private const double ReengageWindowSec = 180.0; // give up re-engaging after this long
        private const float SeekerLiveNm = 9f;         // an active (Fox 3) seeker needs no more support inside this range

        private class PendingEngage
        {
            public List<ObjectBase> Targets;
            public ObjectBase Primary;
            public double At;
            public double Expires;
        }

        private static readonly Dictionary<Aircraft, PendingEngage> Pending = new Dictionary<Aircraft, PendingEngage>();
        private static readonly Dictionary<Aircraft, double> NextDeferLog = new Dictionary<Aircraft, double>();

        // Seconds our own guided missiles still need our radar: semi-active until impact, active ones until their
        // seeker goes live. float.MaxValue when it cannot be estimated (then we simply defend).
        private static float OwnSupportSec(Aircraft ac)
        {
            float need = 0f;
            foreach (AlignUnitToTarget task in ac._alignmentTasks)
            {
                if (!task._isGuidingWeapon || task._alignmentEndRequested || !(task._guidedWeapon is Missile om) || om.IsDestroyed)
                {
                    continue;
                }

                var g = om._seeker != null ? om._seeker._guidanceType : AmmunitionParameters.GuidanceType.None;
                if (g == AmmunitionParameters.GuidanceType.ActiveRadarHoming && om._seeker.isActive())
                {
                    continue;
                }

                ObjectBase t = task._targetObject;
                if (t == null || t.IsDestroyed)
                {
                    continue;
                }

                Vector3 rel = t.transform.position - om.transform.position;
                float range = rel.magnitude;
                float closing = Vector3.Dot(om._velocityVecInUnity - t._velocityVecInUnity, rel.normalized);
                if (closing < 0.05f)
                {
                    return float.MaxValue;
                }

                if (g == AmmunitionParameters.GuidanceType.ActiveRadarHoming)
                {
                    range = Mathf.Max(0f, range - SeekerLiveNm * NotchModel.NmToUnity);
                }

                need = Mathf.Max(need, range / closing);
            }

            return need;
        }

        // Diagnostic: for each of our missiles still being guided, whether its target is inside the guiding radar's field of view.
        private static string SupportInfo(Aircraft ac)
        {
            var sb = new System.Text.StringBuilder();
            foreach (AlignUnitToTarget task in ac._alignmentTasks)
            {
                if (!task._isGuidingWeapon || task._alignmentEndRequested || !(task._guidedWeapon is Missile om) || om.IsDestroyed
                    || om._guidingRadarSystem == null || task._targetObject == null || task._targetObject.IsDestroyed)
                {
                    continue;
                }

                SensorSystemRadar r = om._guidingRadarSystem;
                Vector3 to = task._targetObject.transform.position - ac.transform.position;
                float off = Vector3.Angle(ac.transform.forward, to);
                bool inFov = r.IsObjectInFOV(ac.transform.position, ac.transform.forward, task._targetObject.transform.position);
                sb.Append($" support[{om.getUIDAndName()}->{task._targetObject.getUIDAndName()} off-nose={off:F0}deg arc=({r._horizontalViewArc.x:F0},{r._horizontalViewArc.y:F0}) inFOV={inFov} lost={om.ConnectionLost.Value}]");
            }

            return sb.ToString();
        }

        private static bool ShouldDeferForOwnMissiles(Aircraft ac, Missile m, double now)
        {
            float support = OwnSupportSec(ac);
            if (support <= 0f)
            {
                return false;
            }

            Describe(ac, m, out string kind, out _, out _, out _, out float rangeNm, out float tti);
            if (tti <= support + DeferMarginSec)
            {
                return false;
            }

            if (!NextDeferLog.TryGetValue(ac, out double next) || now >= next)
            {
                NextDeferLog[ac] = now + 10.0;
                ModLog.Event($"DEFENSE deferred {ac.getUIDAndName()}: {kind} threat tti={tti:F0}s, own missile needs support {support:F0}s");
            }

            return true;
        }

        // Stops the aircraft's own attack while it defends: its targets are remembered, its engage tasks are
        // cleared (missiles already guiding keep their alignment tasks) and it cannot fire new ones. Called every
        // tick because the game's auto-engage scan keeps adding targets back.
        private static void AbortOffence(Aircraft ac, Ctl c)
        {
            AI ai = ac._ai;
            if (ai != null)
            {
                if (ai._objectToDestroy != null)
                {
                    Remember(c, ai._objectToDestroy);
                    c.OffPrimary = c.OffPrimary ?? ai._objectToDestroy;
                    ai._objectToDestroy = null;
                }

                if (ai._objectsToDestroyList.Count > 0)
                {
                    foreach (ObjectBase t in ai._objectsToDestroyList)
                    {
                        Remember(c, t);
                    }

                    ai._objectsToDestroyList.Clear();
                }
            }

            bool attacking = false;
            foreach (EngageTask task in ac._currentEngageTasks)
            {
                if (task != null && task._targetObject != null)
                {
                    Remember(c, task._targetObject);
                    attacking = true;
                }
            }

            if (attacking)
            {
                ac.ClearEngageTasksKeepGuiding();
                // Each line means the game queued (or kept) an attack while we were defending; frequent lines = the scan re-adds them.
                if (GameTime.missionElapsedTime >= c.NextClearLog)
                {
                    c.NextClearLog = GameTime.missionElapsedTime + 10;
                    ModLog.Event($"DEFENSE cleared engage tasks {ac.getUIDAndName()} (targets remembered: {c.OffTargets.Count}; throttled)");
                }
            }
        }

        private static void Remember(Ctl c, ObjectBase t)
        {
            if (t != null && !t.IsDestroyed && !t.isWeapon() && !c.OffTargets.Contains(t))
            {
                c.OffTargets.Add(t);
            }
        }

        // After a defence: once the missile threat is gone for a few seconds, resume the interrupted attack on the
        // targets that are still alive and hostile (explicit player orders even under weapons hold).
        private static void TryResume(Aircraft ac, double now)
        {
            if (!Pending.TryGetValue(ac, out PendingEngage p))
            {
                return;
            }

            if (now > p.Expires)
            {
                Pending.Remove(ac);
                return;
            }

            if (now < p.At || PostShotCrankPatch.MissileInbound(ac))
            {
                return;
            }

            Pending.Remove(ac);
            int resumed = 0;
            foreach (ObjectBase t in p.Targets)
            {
                if (t == null || t.IsDestroyed || ac._taskforce == null || t._taskforce == null
                    || ac._taskforce.RelationshipTo(t._taskforce) != RelationsState.Hostile)
                {
                    continue;
                }

                bool ordered = ExplicitOrder.IsPlayerOrdered(ac, t);
                if (ac._weaponStatus == ObjectBase.WeaponStatus.Hold && !ordered)
                {
                    continue;
                }

                if (ac._ai._objectToDestroy == null)
                {
                    ac._ai._objectToDestroy = t == p.Primary || p.Primary == null || p.Primary.IsDestroyed ? t : p.Primary;
                }

                if (!ac._ai._objectsToDestroyList.Contains(t))
                {
                    ac._ai._objectsToDestroyList.Add(t);
                }

                if (t.isAirTarget())
                {
                    ExplicitOrderRecordPatch.CommitEngage(ac, t);
                }

                resumed++;
            }

            if (resumed > 0)
            {
                ModLog.Event($"DEFENSE re-engage {ac.getUIDAndName()}: resuming attack on {resumed} target(s).");
            }
        }

        // Cease fire / hold: forget the attack so nothing re-engages once the defence is over.
        public static void OnCeaseFire(Aircraft ac)
        {
            Pending.Remove(ac);
            if (Active.TryGetValue(ac, out Ctl c))
            {
                c.OffTargets.Clear();
                c.OffPrimary = null;
            }
        }

        // Missile classification and geometry. 'guide' is the aircraft guiding a radar missile (null if none/unnotchable).
        private static void Describe(Aircraft ac, Missile m, out string kind, out bool radar, out bool pitbull, out ObjectBase guide, out float rangeNm, out float tti)
        {
            var g = m._seeker != null ? m._seeker._guidanceType : AmmunitionParameters.GuidanceType.None;
            radar = g == AmmunitionParameters.GuidanceType.SemiActiveRadarHoming || g == AmmunitionParameters.GuidanceType.ActiveRadarHoming;
            Vector3 rel = ac.transform.position - m.transform.position;
            float range = rel.magnitude;
            rangeNm = range / NotchModel.NmToUnity;

            // Treat the missile as "pitbull" a little before the seeker actually goes active, so the jet is already in
            // the MISSILE's notch (not the shooter's) when it starts looking - the two bearings differ once the
            // missile has been coasting on stale data.
            pitbull = g == AmmunitionParameters.GuidanceType.ActiveRadarHoming
                && (m._seeker.isActive() || rangeNm <= PitbullPrepNm);
            kind = g == AmmunitionParameters.GuidanceType.SemiActiveRadarHoming ? "Fox1"
                : g == AmmunitionParameters.GuidanceType.ActiveRadarHoming ? (pitbull ? "Fox3-active" : "Fox3-midcourse")
                : g == AmmunitionParameters.GuidanceType.IRHoming ? "Fox2" : g.ToString();

            guide = null;
            ObjectBase candidate = m._guidingRadarSystem != null ? m._guidingRadarSystem._baseObject : m._launchPlatform;
            if (candidate is Aircraft && !candidate.IsDestroyed && NotchGuidance.EmitterHalfWidthKt(candidate) > 0f)
            {
                guide = candidate;
            }

            float closing = Vector3.Dot(m._velocityVecInUnity - ac._velocityVecInUnity, rel.normalized);
            tti = closing > 0.05f ? range / closing : float.PositiveInfinity;
        }

        private static void Run(Aircraft ac, Ctl c, Missile m, double now)
        {
            var mc = ac.Motioncontroller as FixedWingFlightPhysics;
            if (mc == null)
            {
                End(ac, c, "flight model changed");
                return;
            }

            AbortOffence(ac, c);

            Describe(ac, m, out string kind, out bool radar, out bool pitbull, out ObjectBase guide, out float rangeNm, out float tti);
            // Lock is broken when the guiding platform has lost its track (SARH or mid-course datalink); once active (pitbull) the missile no longer needs us.
            bool lockBroken = m._seeker != null && !pitbull && m.ConnectionLost.Value;

            // A new radar stage (launcher notch -> pitbull) clears any kinematic fallback chosen for the previous one.
            int stage = pitbull ? 1 : 0;
            if (stage != c.Stage)
            {
                c.Stage = stage;
                c.NotchStart = now;
                c.KinFallback = false;
            }

            Phase want = ChoosePhase(c, now, radar, pitbull, guide, lockBroken, tti);
            if (want != c.Phase)
            {
                Enter(ac, c, m, want, guide, now, kind, rangeNm, tti);
            }

            // Common: stay on the flight model's override and in full afterburner.
            mc.commandPositionOverride = true;
            mc.commandAltitudeOverride = false;
            mc._directPoint = false;
            EnsureSpeed(ac, c);

            switch (c.Phase)
            {
                case Phase.Notch:
                case Phase.Hold:
                case Phase.Pitbull:
                    RunNotch(ac, c, mc, now);
                    break;
                case Phase.Reversal:
                    RunReversal(ac, c, mc, now);
                    break;
                case Phase.Kinematic:
                    RunKinematic(ac, c, m, mc, now, tti, rangeNm);
                    break;
                case Phase.LastDitch:
                    RunLastDitch(ac, c, m, mc, tti);
                    break;
            }

            // Remember what the defence wants so it can be re-asserted just before the flight model reads it
            // (vanilla pursuit states rewrite DesiredAltitude / speed each frame and would otherwise win).
            c.WantAlt = ac.DesiredAltitude.Value;
            c.WantPos = mc.CommandPosition;
            c.HasWant = true;

            if (now >= c.NextLog)
            {
                c.NextLog = now + LogSeconds;
                string notchInfo = "";
                if (c.Emitter != null && !c.Emitter.IsDestroyed)
                {
                    float hw = NotchGuidance.EmitterHalfWidthKt(c.Emitter);
                    bool inN = hw > 0f && NotchGuidance.InNotch(ac, c.Emitter, hw, out float rad, out float below);
                    notchInfo = hw > 0f ? $" emitter={c.Emitter.getUIDAndName()} radial={Mathf.Abs(Vector3.Dot(ac.UnityVelocityVector, (c.Emitter.transform.position - ac.transform.position).normalized)) * NotchModel.KtPerUnitSpeed:F0}/{hw:F0}kt inNotch={inN}" : " emitter=off";
                }

                ModLog.Info($"DEFENSE {ac.getUIDAndName()} phase={c.Phase} {kind} range={rangeNm:F1}nm tti={tti:F1}s lockBroken={lockBroken}{notchInfo}{SupportInfo(ac)} alt={ac.transform.position.y * NotchModel.UnityToFt:F0}ft");
            }
        }

        private static Phase ChoosePhase(Ctl c, double now, bool radar, bool pitbull, ObjectBase guide, bool lockBroken, float tti)
        {
            if (tti <= LastDitchStartSec && !lockBroken)
            {
                return Phase.LastDitch;
            }

            if (c.Phase == Phase.Kinematic && now - c.PhaseStart < MinKinematicHoldSec)
            {
                return Phase.Kinematic;
            }

            if (!radar || c.KinFallback)
            {
                return Phase.Kinematic;
            }

            if (pitbull)
            {
                if (c.Phase == Phase.Pitbull && now - c.NotchStart > KinematicAfterSec && !lockBroken && tti > LastDitchStartSec + 1f)
                {
                    c.KinFallback = true;
                    return Phase.Kinematic;
                }

                return Phase.Pitbull;
            }

            if (guide == null)
            {
                return Phase.Kinematic;
            }

            // Once the reversal has started it runs to completion (never interrupted), then holds. The "notch did not
            // break lock" fallback is judged only on the hold, measured from when the hold began.
            if (c.Phase == Phase.Reversal)
            {
                return Phase.Reversal;
            }

            if (c.Phase == Phase.Hold)
            {
                // The notch has to be re-established after the reversal (the jet is still diving/turning), so the
                // "did not break lock" clock only starts once we are confirmed inside it again.
                bool settled = c.NotchedSince >= 0 && now - c.NotchedSince > KinematicAfterSec;
                bool never = now - c.PhaseStart > NotchGiveUpSec * 2f;
                if ((settled || never) && !lockBroken && tti > LastDitchStartSec + 1f)
                {
                    ModLog.Event($"DEFENSE hold ended ({(settled ? "notch held, lock not broken" : "notch not re-established")}) tti={tti:F1}s");
                    c.KinFallback = true;
                    return Phase.Kinematic;
                }

                return Phase.Hold;
            }

            return Phase.Notch;
        }

        private static void Enter(Aircraft ac, Ctl c, Missile m, Phase to, ObjectBase guide, double now, string kind, float rangeNm, float tti)
        {
            Phase from = c.Phase;
            var mc = (FixedWingFlightPhysics)ac.Motioncontroller;

            // Leaving a direct-control phase: hand the stick back to the flight model.
            if (from == Phase.LastDitch || from == Phase.Reversal)
            {
                mc.controlOverride = false;
                mc.controlInput = Vector3.zero;
            }

            c.Phase = to;
            c.PhaseStart = now;
            switch (to)
            {
                case Phase.Notch:
                    c.Emitter = guide;
                    c.Side = NotchGuidance.ChooseSide(ac, guide, false);
                    break;
                case Phase.Pitbull:
                    c.Emitter = m;
                    c.Side = NotchGuidance.ChooseSide(ac, m, false);
                    ac.LaunchChaff();
                    NotchModel.StartChaffBloom(ac);
                    break;
                case Phase.Kinematic:
                    c.Leg = 0;
                    c.LegStart = -1e9;
                    c.BaseAlt = ac.transform.position.y;
                    c.Emitter = null;
                    break;
                case Phase.LastDitch:
                    c.LdChaff = false;
                    c.LdPullChaff = false;
                    break;
            }

            ModLog.Event($"DEFENSE {ac.getUIDAndName()} {from} -> {to} vs {m.getUIDAndName()} {kind} range={rangeNm:F1}nm tti={tti:F1}s");
        }

        // Speed demand per phase: a Split-S is flown at minimum throttle (no need to accelerate toward the ground and
        // it keeps the loop tight); everything else, including the Immelman (which trades speed for height), is in
        // full afterburner.
        private static void EnsureSpeed(Aircraft ac, Ctl c)
        {
            float[] mach = ac.Ap._speedValuesInMach;
            bool idle = c.Phase == Phase.Reversal && !c.Immelman;
            float want = idle ? mach[0] : mach[mach.Length - 1];
            ISpeedCommand cmd = ac.SpeedCommand.Value;
            if (!(cmd is ConstantMach) || Mathf.Abs(cmd.SpeedInMach - want) > 0.001f)
            {
                ac.SetSpeedCommand(new ConstantMach(want, ac));
            }
        }

        // NOTCH / HOLD / PITBULL: fly the beam of c.Emitter, below it.
        private static void RunNotch(Aircraft ac, Ctl c, FixedWingFlightPhysics mc, double now)
        {
            if (c.Emitter == null || c.Emitter.IsDestroyed)
            {
                c.KinFallback = true;
                return;
            }

            float hw = NotchGuidance.EmitterHalfWidthKt(c.Emitter);
            if (hw <= 0f)
            {
                c.KinFallback = true;
                return;
            }

            Vector3 aim = NotchGuidance.BeamAim(ac, c.Emitter, c.Side, hw, true, out _);

            // Crew skill: each interval the crew may fly the beam with a heading error.
            if (now >= c.NextAimRoll)
            {
                c.NextAimRoll = now + CrewSkillModel.BotchRerollSec;
                c.AimErrorDeg = CrewSkillModel.RollAimErrorDeg(ac);
                if (c.AimErrorDeg != 0f)
                {
                    ModLog.Event($"DEFENSE {ac.getUIDAndName()} crew botches the notch heading by {c.AimErrorDeg:F0}deg for {CrewSkillModel.BotchRerollSec:F0}s");
                }
            }

            if (c.AimErrorDeg != 0f)
            {
                aim = ac.transform.position + Quaternion.AngleAxis(c.AimErrorDeg, Vector3.up) * (aim - ac.transform.position);
            }

            mc.CommandPosition = aim;
            ac.DesiredAltitude.Value = NotchGuidance.NotchAltitude(ac, c.Emitter);

            if (c.Phase == Phase.Hold)
            {
                float hwHold = NotchGuidance.EmitterHalfWidthKt(c.Emitter);
                bool inHold = hwHold > 0f && NotchGuidance.InNotch(ac, c.Emitter, hwHold, out _, out _);
                if (inHold && c.NotchedSince < 0)
                {
                    c.NotchedSince = now;
                    ModLog.Event($"DEFENSE {ac.getUIDAndName()} notch re-established after reversal");
                }
            }

            if (c.Phase != Phase.Notch)
            {
                return;
            }

            float rangeNm = (c.Emitter.transform.position - ac.transform.position).magnitude / NotchModel.NmToUnity;
            if (rangeNm > MinNotchRangeNm && NotchGuidance.InNotch(ac, c.Emitter, hw, out float radial, out float below))
            {
                // In the notch: chaff, then the vertical reversal.
                ac.LaunchChaff();
                NotchModel.StartChaffBloom(ac);
                ModLog.Event($"DEFENSE {ac.getUIDAndName()} in notch of {c.Emitter.getUIDAndName()} radial={radial:F0}kt below={below:F0}ft: chaff + reversal");
                StartReversal(ac, c, mc, now);
            }
            else if (now - c.PhaseStart > NotchGiveUpSec)
            {
                ModLog.Event($"DEFENSE {ac.getUIDAndName()} could not reach the notch in {NotchGiveUpSec:F0}s");
                c.KinFallback = true;
            }
        }

        private static void StartReversal(Aircraft ac, Ctl c, FixedWingFlightPhysics mc, double now)
        {
            // Loop diameter at the current speed and an assumed g.
            float v = Mathf.Max(mc.Velocity, 50f);
            float radius = v * v / (9.81f * Mathf.Min(LoopG, ac.Ap._maxG));
            float diameterFt = 2f * radius * FtPerMetre;
            float heightFt = ac.HeightOverTerrain.Value * NotchModel.UnityToFt;
            float emitterAboveFt = (c.Emitter.transform.position.y - ac.transform.position.y) * NotchModel.UnityToFt;

            bool canSplitS = heightFt > diameterFt * 1.1f + MinSplitSFloorFt;
            bool canImmelman = emitterAboveFt > diameterFt + NotchModel.LookDownMarginFt + 300f;
            if (!canSplitS && !canImmelman)
            {
                // No vertical room: reverse in the horizontal plane instead (the flip-side beam is also a notch).
                c.Side = -c.Side;
                c.Phase = Phase.Hold;
                c.PhaseStart = now;
                c.NotchedSince = -1;
                ModLog.Event($"DEFENSE {ac.getUIDAndName()} no room for a vertical reversal (loop {diameterFt:F0}ft, height {heightFt:F0}ft): horizontal reversal");
                return;
            }

            c.Immelman = !canSplitS;
            c.Phase = Phase.Reversal;
            c.PhaseStart = now;
            c.Heading0 = Heading(ac);
            c.Step = c.Immelman ? RevStep.Pull : RevStep.Roll;
            c.StepStart = now;
            c.PullDir0 = ac.UnityVelocityVector.normalized;
            c.Learned = false;
            c.SawSteep = false;
            mc.controlOverride = true;
            ModLog.Event($"DEFENSE {ac.getUIDAndName()} {(c.Immelman ? "Immelman" : "Split-S")} (loop {diameterFt:F0}ft, height {heightFt:F0}ft)");
        }

        private static void RunReversal(Aircraft ac, Ctl c, FixedWingFlightPhysics mc, double now)
        {
            Vector3 fwd = ac.transform.forward;
            float bank = Bank(ac);
            float x = 0f;
            float z = 0f;
            bool inverted = !c.Immelman;                 // reference attitude for wings-level roll control while pulling

            switch (c.Step)
            {
                case RevStep.Roll:
                    // Split-S: roll inverted first.
                    z = Mathf.Clamp(Mathf.DeltaAngle(bank, 180f) / RollGainDeg, -1f, 1f);
                    if (Mathf.Abs(Mathf.DeltaAngle(bank, 180f)) < 15f)
                    {
                        c.Step = RevStep.Pull;
                        c.StepStart = now;
                        c.PullDir0 = ac.UnityVelocityVector.normalized;
                        c.Learned = false;
                    }

                    break;

                case RevStep.Pull:
                    x = _pullSign;
                    // Keep the loop vertical (wings level / inverted) except near the vertical where bank is undefined.
                    // Past the vertical the reference flips: a Split-S exits upright, an Immelman exits inverted.
                    if (Mathf.Abs(fwd.y) < 0.7f)
                    {
                        bool wantInverted = c.SawSteep ? !inverted : inverted;
                        z = Mathf.Clamp(Mathf.DeltaAngle(bank, wantInverted ? 180f : 0f) / RollGainDeg, -1f, 1f);
                    }

                    LearnPullSign(ac, c, now);
                    // Heading flips as the nose passes the vertical, so heading alone ends the loop too early (mid-dive).
                    // Finish only once the loop has gone through the vertical and the nose is back near the horizon.
                    if (Mathf.Abs(fwd.y) > 0.6f)
                    {
                        c.SawSteep = true;
                    }

                    if (c.SawSteep && Mathf.Abs(fwd.y) < LevelOutSinPitch
                        && Mathf.Abs(Mathf.DeltaAngle(Heading(ac), c.Heading0)) >= ReversalTurnDeg)
                    {
                        c.Step = c.Immelman ? RevStep.Upright : RevStep.Done;
                        c.StepStart = now;
                    }

                    break;

                case RevStep.Upright:
                    // Immelman: half-roll from inverted back to upright.
                    z = Mathf.Clamp(Mathf.DeltaAngle(bank, 0f) / RollGainDeg, -1f, 1f);
                    if (Mathf.Abs(Mathf.DeltaAngle(bank, 0f)) < 10f)
                    {
                        c.Step = RevStep.Done;
                    }

                    break;
            }

            mc.controlInput = new Vector3(x, 0f, z);

            if (c.Step == RevStep.Done || now - c.PhaseStart > MaxReversalSec)
            {
                mc.controlOverride = false;
                mc.controlInput = Vector3.zero;
                c.Phase = Phase.Hold;
                c.PhaseStart = now;
                c.NotchedSince = -1;
                c.Side =  NotchGuidance.ChooseSide(ac, c.Emitter, false);
                float hw = NotchGuidance.EmitterHalfWidthKt(c.Emitter);
                bool inN = hw > 0f && NotchGuidance.InNotch(ac, c.Emitter, hw, out _, out _);
                bool timedOut = c.Step != RevStep.Done;
                ModLog.Event($"DEFENSE {ac.getUIDAndName()} reversal complete{(timedOut ? $" (TIMEOUT step={c.Step} pitch={Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg:F0}deg alt={ac.transform.position.y * NotchModel.UnityToFt:F0}ft)" : "")}, afterburner hold, still in notch={inN}");
            }
        }

        // The first time we pull, confirm the nose actually went toward our own "up"; flip the sign otherwise.
        private static void LearnPullSign(Aircraft ac, Ctl c, double now)
        {
            if (c.Learned || now - c.StepStart < 0.7)
            {
                return;
            }

            c.Learned = true;
            float turned = Vector3.Dot(ac.UnityVelocityVector.normalized - c.PullDir0, ac.transform.up);
            if (turned < -0.005f)
            {
                _pullSign = -_pullSign;
                ModLog.Warn($"DEFENSE pitch sign flipped to {_pullSign} (measured {turned:F3})");
            }
        }

        private static void RunKinematic(Aircraft ac, Ctl c, Missile m, FixedWingFlightPhysics mc, double now, float tti, float rangeNm)
        {
            Vector3 me = ac.transform.position;
            Vector3 away = Vector3.ProjectOnPlane(me - m.transform.position, Vector3.up);
            if (away.sqrMagnitude < 1e-4f)
            {
                away = ac.transform.forward;
                away.y = 0f;
            }

            // Re-time every 5-10 s: the faster the missile arrives, the shorter the legs.
            if (now - c.LegStart >= c.LegSec)
            {
                c.Leg++;
                c.LegStart = now;
                c.LegSec = float.IsInfinity(tti) ? KinLegMaxSec : Mathf.Clamp(tti / 2f, KinLegMinSec, KinLegMaxSec);
                ModLog.Info($"DEFENSE {ac.getUIDAndName()} kinematic leg {c.Leg} ({(c.Leg % 2 == 0 ? "right" : "left")}, step {(c.Leg % 3) + 1}) len={c.LegSec:F0}s");
            }

            float side = c.Leg % 2 == 0 ? 1f : -1f;
            Vector3 dir = Quaternion.AngleAxis(side * KinOffsetDeg, Vector3.up) * away.normalized;
            mc.CommandPosition = me + dir * AimDistanceNm * NotchModel.NmToUnity;

            float maxY = KinMaxAltFt / NotchModel.UnityToFt;
            ac.DesiredAltitude.Value = Mathf.Min(c.BaseAlt + (KinClimbStepFt / NotchModel.UnityToFt) * ((c.Leg % 3) + 1), Mathf.Max(maxY, c.BaseAlt));
        }

        private static void RunLastDitch(Aircraft ac, Ctl c, Missile m, FixedWingFlightPhysics mc, float tti)
        {
            mc.controlOverride = true;

            Vector3 fwd = ac.transform.forward;
            Vector3 toMissile = (m.transform.position - ac.transform.position).normalized;
            Vector3 upProj = Vector3.ProjectOnPlane(ac.transform.up, fwd);
            Vector3 mProj = Vector3.ProjectOnPlane(toMissile, fwd);
            float theta = mProj.sqrMagnitude > 1e-6f && upProj.sqrMagnitude > 1e-6f ? Vector3.SignedAngle(upProj, mProj, fwd) : 0f;
            float z = Mathf.Clamp(theta / RollGainDeg, -1f, 1f);
            bool aligned = Mathf.Abs(theta) < LiftAlignedDeg;

            if (!c.LdChaff)
            {
                c.LdChaff = true;
                ac.LaunchChaff();
                NotchModel.StartChaffBloom(ac);
                ModLog.Event($"DEFENSE {ac.getUIDAndName()} LAST-DITCH: rolling lift vector to {m.getUIDAndName()}, tti={tti:F1}s, chaff");
            }

            // Crew skill: a less skilled crew starts the max pull later.
            float pullAt = Mathf.Max(0.25f, PullStartSec - CrewSkillModel.For(ac).LastDitchLateSec);
            float x = 0f;
            if (tti <= pullAt && (aligned || tti <= pullAt * 0.5f))
            {
                x = _pullSign;
                if (!c.LdPullChaff)
                {
                    c.LdPullChaff = true;
                    c.PullDir0 = ac.UnityVelocityVector.normalized;
                    c.StepStart = GameTime.missionElapsedTime;
                    c.Learned = false;
                    ac.LaunchChaff();
                    ModLog.Event($"DEFENSE {ac.getUIDAndName()} LAST-DITCH: max pull, tti={tti:F1}s, lift-vector error {theta:F0}deg");
                }

                LearnPullSign(ac, c, GameTime.missionElapsedTime);
            }

            mc.controlInput = new Vector3(x, 0f, z);
        }

        private static float Heading(Aircraft ac)
        {
            Vector3 f = ac.transform.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        // Same bank convention as the flight model: angle from world-up to the aircraft's up, about its forward axis.
        private static float Bank(Aircraft ac)
        {
            Vector3 f = ac.transform.forward;
            Vector3 a = Vector3.ProjectOnPlane(Vector3.up, f);
            Vector3 b = Vector3.ProjectOnPlane(ac.transform.up, f);
            if (a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f)
            {
                return 0f;
            }

            return Vector3.SignedAngle(a.normalized, b.normalized, f);
        }
    }

    // Our evasion replaces the game's AdvancedMissileEvasion state whenever we are eligible.
    [HarmonyPatch(typeof(Aircraft), "ShouldEvadeMissile")]
    public static class MissileDefenseSuppressVanilla
    {
        private static bool Prefix(Aircraft __instance, ref bool __result)
        {
            if (MissileDefense.Eligible(__instance))
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(FixedWingFlightPhysics), "CalculatePositionAndAltitudeErrors")]
    public static class MissileDefenseReassertPatch
    {
        private static void Prefix(FixedWingFlightPhysics __instance)
        {
            if (__instance._ac is Aircraft ac)
            {
                MissileDefense.Reassert(ac);
            }
        }
    }

    [HarmonyPatch(typeof(Aircraft), "OnFixedUpdate")]
    public static class MissileDefenseTickPatch
    {
        private static void Postfix(Aircraft __instance)
        {
            MissileDefense.Tick(__instance);
        }
    }

    // The game's MovingInFormation state switches an on-station wingman back to formation physics every tick, which
    // undid the switch to free flight the defence (and the wingman extend) needs and ended the defence at once
    // ("flight model changed"), so formation wingmen never evaded. Refuse the switch while one of them is active.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SetFormationPhysicsModel))]
    public static class KeepFreeFlightPatch
    {
        private static bool Prefix(Aircraft __instance)
        {
            return !(MissileDefense.IsActive(__instance) || WingmanExtend.IsExtending(__instance));
        }
    }
}
