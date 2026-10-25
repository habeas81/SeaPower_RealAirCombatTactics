using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Wingman "extend and turn back in" (mutual support).
    //
    // When the flight lead and a bandit are stuck in a circle fight neither can win (both inside CircleRangeNm of each
    // other, the lead having turned through CircleTurnDeg without a kill), the wingman should not join the merry-go-round.
    // Provided nothing else threatens the flight, he:
    //   EXTEND  - leaves the fight in full afterburner, directly away from it (offset to the side closest to his heading),
    //             until he has the room for a missile shot (about 60% of his best missile's launch range, clamped);
    //   TURN-IN - hands control back and orders a missile attack on the bandit. The order is registered with HotPursuit,
    //             so it stays alive (and the mod's lead pursuit flies it) until the bandit is dead, and the existing
    //             shoot-look-shoot doctrine still governs the shots.
    // Aborted at once on any incoming missile, any other hostile in range, the loss of the lead/bandit, low fuel, a
    // missile defence or a manual player order. Applies to AI and (optionally) player wingmen of every side.
    public static class WingmanExtend
    {
        public static readonly bool Enabled = true;
        public static readonly bool AffectPlayerAircraft = true;

        // ---- Tunables ----
        private const float CircleRangeNm = 3.5f;       // lead and bandit closer than this = merged fight
        private const float CircleBreakNm = 6f;         // ... fight considered over once farther apart than this
        private const float CircleTurnDeg = 270f;       // lead must have turned this much inside the fight (a circle, not a pass)
        private const float CircleMinSec = 20f;         // ... and the fight must have lasted at least this long
        private const float OtherThreatNm = 25f;        // any other hostile air contact this close to the wingman or lead blocks it
        private const float ExtendLaunchFraction = 0.6f; // extend to this fraction of the best missile's maximum launch range
        private const float MinExtendNm = 5f;
        private const float MaxExtendNm = 10f;
        private const float MaxExtendSec = 60f;         // turn in anyway after this long
        private const float ExtendOffsetDeg = 30f;      // extend this far off the straight-away line (towards current heading)
        private const float CooldownSec = 90f;          // after a turn-in, no new extension for this long
        private const float TurnInPriority = 30f;       // engage-task priority (same as the HOT-PURSUIT re-task)
        private const double CheckIntervalSec = 1.0;
        private const float AimDistanceNm = 20f;
        private const float NmToUnity = 27.559496f;

        private enum Stage { Watching, Extending }

        private class State
        {
            public Stage Stage;
            public double NextCheck;
            public double Cooldown;        // no new extension before this time

            // Circle-fight tracking (lead vs bandit).
            public ObjectBase Bandit;
            public double FightStart;
            public float TurnAccum;
            public float LastLeadHdg;
            public double LastClose;       // last time lead and bandit were inside CircleBreakNm
            public bool HeldBack;          // blocked-by-threat already logged for this fight

            // Extension.
            public double ExtendStart;
            public float ExtendNm;
            public string AmmoId;
            public bool PrevPosOverride;
            public float PrevAltitude;
            public float PrevSpeedKt;
            public float PrevMach;
            public Vector3 WantPos;
            public float WantAlt;
        }

        private static readonly Dictionary<Aircraft, State> States = new Dictionary<Aircraft, State>();

        public static bool IsExtending(Aircraft ac) => ac != null && States.TryGetValue(ac, out State s) && s.Stage == Stage.Extending;

        // Per-frame hook (before the flight model reads its commands): keep the extension's demands over the game's own.
        public static void Reassert(Aircraft ac)
        {
            if (!States.TryGetValue(ac, out State s) || s.Stage != Stage.Extending)
            {
                return;
            }

            if (ac.Motioncontroller != null)
            {
                ac.Motioncontroller.commandPositionOverride = true;
                ac.Motioncontroller.CommandPosition = s.WantPos;
            }

            ac.DesiredAltitude.Value = s.WantAlt;
            EnsureAfterburner(ac);
        }

        public static void Tick(Aircraft ac)
        {
            if (!Enabled || ac == null || ac.IsDestroyed)
            {
                return;
            }

            try
            {
                double now = GameTime.missionElapsedTime;
                States.TryGetValue(ac, out State s);
                if (s != null && now < s.NextCheck)
                {
                    return;
                }

                if (!(ac.IsFormationWingman && ac.Formation != null))
                {
                    if (s != null)
                    {
                        Abort(ac, s, "no longer a wingman");
                        States.Remove(ac);
                    }

                    return;
                }

                if (s == null)
                {
                    s = new State();
                    States[ac] = s;
                }

                s.NextCheck = now + CheckIntervalSec;
                Aircraft lead = ac.Formation.LeaderStation?.UnitObject as Aircraft;
                if (s.Stage == Stage.Extending)
                {
                    RunExtend(ac, s, lead, now);
                }
                else
                {
                    Watch(ac, s, lead, now);
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("WingmanExtend.Tick", ex);
                if (States.TryGetValue(ac, out State bad) && bad.Stage == Stage.Extending)
                {
                    Abort(ac, bad, "error");
                }
            }
        }

        // ---- Detection ----

        private static void Watch(Aircraft ac, State s, Aircraft lead, double now)
        {
            if (lead == null || lead.IsDestroyed || lead == ac || !Capable(ac) || now < s.Cooldown)
            {
                ResetFight(s);
                return;
            }

            // Track the fight: the hostile air contact nearest to the lead, if it is close enough to be merged.
            ObjectBase bandit = null;
            float best = CircleRangeNm * NmToUnity;
            foreach (ObjectBase c in PostShotCrankPatch.HostileAirContacts(lead))
            {
                float d = Flat(c.transform.position - lead.transform.position).magnitude;
                if (d < best)
                {
                    best = d;
                    bandit = c;
                }
            }

            float hdg = Heading(lead);
            if (bandit != null)
            {
                if (s.Bandit != bandit)
                {
                    ResetFight(s);
                    s.Bandit = bandit;
                    s.FightStart = now;
                    s.LastLeadHdg = hdg;
                }

                s.LastClose = now;
                s.TurnAccum += Mathf.Abs(Mathf.DeltaAngle(s.LastLeadHdg, hdg));
                s.LastLeadHdg = hdg;
            }
            else if (s.Bandit != null)
            {
                bool apart = Flat(s.Bandit.transform.position - lead.transform.position).magnitude > CircleBreakNm * NmToUnity;
                if (s.Bandit.IsDestroyed || apart || now - s.LastClose > 5.0)
                {
                    ResetFight(s);
                }

                return;
            }
            else
            {
                return;
            }

            if (s.Bandit.IsDestroyed || now - s.FightStart < CircleMinSec || s.TurnAccum < CircleTurnDeg)
            {
                return;
            }

            // A shot from the lead is still in the air: the fight may be about to end, so wait.
            if (HasMissileAt(lead, s.Bandit) || HasMissileAt(ac, s.Bandit))
            {
                return;
            }

            string block = WhyBlocked(ac, lead, s.Bandit, out float launchNm, out string ammo);
            if (block != null)
            {
                if (!s.HeldBack)
                {
                    s.HeldBack = true;
                    ModLog.Event($"t={now:0} EXTEND held: {ac.getUIDAndName()} sees {lead.getUIDAndName()} in a circle fight with {s.Bandit.getUIDAndName()} but {block}.");
                }

                return;
            }

            Begin(ac, s, lead, ammo, launchNm, now);
        }

        private static void ResetFight(State s)
        {
            s.Bandit = null;
            s.TurnAccum = 0f;
            s.HeldBack = false;
        }

        // Can this aircraft be driven by us at all?
        private static bool Capable(Aircraft ac)
        {
            if (!ac.HasControl || ac.isLanding)
            {
                return false;
            }

            if (ac.IsPlayerObject && (!AffectPlayerAircraft || ac._playerCommandOverride))
            {
                return false;
            }

            // A manual attack order is the player's call.
            if (HotPursuit.Records.ContainsKey(ac) || MissileDefense.IsActive(ac))
            {
                return false;
            }

            return ac.Motioncontroller is FixedWingFlightPhysics || ac.Motioncontroller?.GetType().Name == "FormationFlightPhysics";
        }

        // Null when the wingman may extend; otherwise a short reason. Also returns the weapon to use.
        private static string WhyBlocked(Aircraft ac, Aircraft lead, ObjectBase bandit, out float launchNm, out string ammo)
        {
            launchNm = 0f;
            ammo = null;

            if (PostShotCrankPatch.MissileInbound(ac) || PostShotCrankPatch.MissileInbound(lead))
            {
                return "a missile is inbound on the flight";
            }

            if (ac.RangeOnMap.Value < HotPursuit.MinFuelRangeNm * NmToUnity)
            {
                return "fuel is too low";
            }

            foreach (ObjectBase c in PostShotCrankPatch.HostileAirContacts(ac))
            {
                if (c == bandit)
                {
                    continue;
                }

                float nearest = Mathf.Min((c.transform.position - ac.transform.position).magnitude,
                    (c.transform.position - lead.transform.position).magnitude);
                if (nearest < OtherThreatNm * NmToUnity)
                {
                    return "another hostile (" + c.getUIDAndName() + ") is within " + OtherThreatNm.ToString("0") + " nm";
                }
            }

            if (ac._ai != null)
            {
                foreach (Ammunition a in ac._ai.AmmunitionForTarget(bandit, Ammunition.Type.Missile))
                {
                    if (a?._ap == null)
                    {
                        continue;
                    }

                    float nm = a._ap._launchRangesInUnity.y / NmToUnity;
                    if (nm > launchNm)
                    {
                        launchNm = nm;
                        ammo = a._ap._ammunitionFileName;
                    }
                }
            }

            return ammo == null ? "he has no missile for the bandit" : null;
        }

        // Is `shooter` still guiding or has in flight a missile aimed at `target`?
        private static bool HasMissileAt(ObjectBase shooter, ObjectBase target)
        {
            if (shooter?._obp?._weaponSystems == null)
            {
                return false;
            }

            foreach (WeaponSystem system in shooter._obp._weaponSystems)
            {
                if (system?._launchedWeapons == null)
                {
                    continue;
                }

                foreach (WeaponBase w in system._launchedWeapons)
                {
                    if (w is Missile && !w.IsDestroyed && !w.IsDefeatedShot
                        && (w.CurrentIntendedTargetObject ?? w.CurrentTarget) == target)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // ---- Extension ----

        private static void Begin(Aircraft ac, State s, Aircraft lead, string ammo, float launchNm, double now)
        {
            if (ac.Motioncontroller?.GetType().Name == "FormationFlightPhysics")
            {
                ac.SetFlightPhysicsModel(isStartingAirborne: true);
            }

            if (!(ac.Motioncontroller is FixedWingFlightPhysics mc))
            {
                return;
            }

            s.Stage = Stage.Extending;
            s.ExtendStart = now;
            s.AmmoId = ammo;
            s.ExtendNm = Mathf.Clamp(launchNm * ExtendLaunchFraction, MinExtendNm, MaxExtendNm);
            s.PrevPosOverride = mc.commandPositionOverride;
            s.PrevAltitude = ac.DesiredAltitude.Value;
            s.PrevSpeedKt = ac.SpeedCommand.Value != null ? ac.SpeedCommand.Value.SpeedInKnots : 0f;
            s.PrevMach = ac.SpeedCommand.Value is ConstantMach ? ac.SpeedCommand.Value.SpeedInMach : 0f;
            s.WantAlt = ac.transform.position.y * 220.47266f;
            UpdateAim(ac, s, lead);
            ModLog.Event($"t={now:0} EXTEND start: {ac.getUIDAndName()} leaves the circle fight of {lead.getUIDAndName()} vs {s.Bandit.getUIDAndName()} to {s.ExtendNm:0.0} nm ({ammo}, launch range {launchNm:0} nm).");
            Reassert(ac);
        }

        private static void RunExtend(Aircraft ac, State s, Aircraft lead, double now)
        {
            string why = null;
            if (!Capable(ac))
            {
                why = "manual order, missile defence or control lost";
            }
            else if (lead == null || lead.IsDestroyed || s.Bandit == null || s.Bandit.IsDestroyed)
            {
                why = "lead or bandit gone";
            }
            else if (PostShotCrankPatch.MissileInbound(ac) || PostShotCrankPatch.MissileInbound(lead))
            {
                why = "missile inbound";
            }
            else if (ac.RangeOnMap.Value < HotPursuit.MinFuelRangeNm * NmToUnity)
            {
                why = "fuel";
            }
            else
            {
                foreach (ObjectBase c in PostShotCrankPatch.HostileAirContacts(ac))
                {
                    if (c != s.Bandit && (c.transform.position - ac.transform.position).magnitude < OtherThreatNm * NmToUnity)
                    {
                        why = "new threat " + c.getUIDAndName();
                        break;
                    }
                }
            }

            if (why != null)
            {
                Abort(ac, s, why);
                s.Cooldown = now + CooldownSec / 3f;
                return;
            }

            Vector3 center = FightCenter(lead, s.Bandit);
            float distNm = Flat(ac.transform.position - center).magnitude / NmToUnity;
            if (distNm >= s.ExtendNm || now - s.ExtendStart > MaxExtendSec)
            {
                TurnIn(ac, s, now, distNm);
                return;
            }

            UpdateAim(ac, s, lead);
        }

        private static void UpdateAim(Aircraft ac, State s, Aircraft lead)
        {
            Vector3 center = FightCenter(lead, s.Bandit);
            Vector3 away = Flat(ac.transform.position - center);
            Vector3 fwd = Flat(ac.transform.forward);
            if (away.magnitude < 1f * NmToUnity)
            {
                away = fwd;
            }

            away.Normalize();
            Vector3 left = Quaternion.AngleAxis(-ExtendOffsetDeg, Vector3.up) * away;
            Vector3 right = Quaternion.AngleAxis(ExtendOffsetDeg, Vector3.up) * away;
            Vector3 dir = Vector3.Angle(left, fwd) < Vector3.Angle(right, fwd) ? left : right;
            s.WantPos = ac.transform.position + dir * (AimDistanceNm * NmToUnity);
        }

        private static Vector3 FightCenter(ObjectBase lead, ObjectBase bandit)
        {
            return (lead.transform.position + bandit.transform.position) * 0.5f;
        }

        private static void TurnIn(Aircraft ac, State s, double now, float distNm)
        {
            ObjectBase bandit = s.Bandit;
            string ammo = ac.getAmmunitionAmountByName(s.AmmoId) > 0 ? s.AmmoId : null;
            Restore(ac, s);
            s.Stage = Stage.Watching;
            s.Cooldown = now + CooldownSec;
            ResetFight(s);

            if (ammo == null || bandit == null || bandit.IsDestroyed)
            {
                ModLog.Event($"t={now:0} EXTEND end: {ac.getUIDAndName()} extended {distNm:0.0} nm but has no shot left.");
                return;
            }

            ModLog.Event($"t={now:0} EXTEND turn-in: {ac.getUIDAndName()} -> {bandit.getUIDAndName()} with {ammo} after extending {distNm:0.0} nm.");
            HotPursuit.Records[ac] = new HotPursuit.Record
            {
                Target = bandit,
                AmmoId = ammo,
                Priority = (int)TurnInPriority,
                NextCheck = now + HotPursuit.PollIntervalSec,
                LastRetask = now
            };
            HotPursuit.Inserting = true;
            try
            {
                ac.InsertEngageTask(ammo, bandit, bandit.transform.position, 1, (int)TurnInPriority);
            }
            finally
            {
                HotPursuit.Inserting = false;
            }
        }

        private static void Abort(Aircraft ac, State s, string why)
        {
            if (s.Stage != Stage.Extending)
            {
                return;
            }

            Restore(ac, s);
            s.Stage = Stage.Watching;
            ResetFight(s);
            ModLog.Event($"t={GameTime.missionElapsedTime:0} EXTEND aborted: {ac.getUIDAndName()} ({why}).");
        }

        // Give back what the extension borrowed (never a zero-speed command, that stalls the jet).
        private static void Restore(Aircraft ac, State s)
        {
            if (ac.Motioncontroller is FixedWingFlightPhysics mc)
            {
                mc.commandPositionOverride = s.PrevPosOverride;
            }

            if (ac.IsDestroyed)
            {
                return;
            }

            ac.DesiredAltitude.Value = s.PrevAltitude;
            if (s.PrevMach > 0.05f)
            {
                ac.SetSpeedCommand(new ConstantMach(s.PrevMach, ac));
            }
            else
            {
                float kt = s.PrevSpeedKt > 50f ? s.PrevSpeedKt : Mathf.Max(ac.getVelocityInKnots(), 300f);
                ac.SetSpeedCommand(new ConstantSpeed(kt, ac));
            }
        }

        private static void EnsureAfterburner(Aircraft ac)
        {
            float[] mach = ac.Ap._speedValuesInMach;
            float want = mach[mach.Length - 1];
            ISpeedCommand cmd = ac.SpeedCommand.Value;
            if (!(cmd is ConstantMach) || Mathf.Abs(cmd.SpeedInMach - want) > 0.001f)
            {
                ac.SetSpeedCommand(new ConstantMach(want, ac));
            }
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        private static float Heading(Aircraft ac)
        {
            Vector3 f = ac.transform.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
    }

    [HarmonyPatch(typeof(Aircraft), "OnFixedUpdate")]
    public static class WingmanExtendTickPatch
    {
        private static void Postfix(Aircraft __instance)
        {
            WingmanExtend.Tick(__instance);
        }
    }

    [HarmonyPatch(typeof(FixedWingFlightPhysics), "CalculatePositionAndAltitudeErrors")]
    public static class WingmanExtendReassertPatch
    {
        private static void Prefix(FixedWingFlightPhysics __instance)
        {
            if (__instance._ac is Aircraft ac)
            {
                WingmanExtend.Reassert(ac);
            }
        }
    }
}
