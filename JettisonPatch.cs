using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Combat jettison: an aircraft that is threatened in an air-to-air fight sheds its non-A2A stores so it can
    // fight clean. Vanilla only drops tanks when a missile is already inbound, and not for player aircraft.
    //   - External fuel tanks are always dropped (AI and player aircraft).
    //   - Free-fall bombs / cluster stores are dropped too (flag below). Guided A2G/anti-ship missiles are NOT
    //     jettisoned (releasing them "at our own position" would be dangerous and wasteful).
    // Threat = an inbound missile, or a hostile air contact that is close, fast and pointed at us
    // (same test the post-shot crank uses, so the two features agree on what "threatened" means).
    [HarmonyPatch(typeof(AI), nameof(AI.OnUpdate))]
    public static class JettisonPatch
    {
        // ---- Tunables ----
        private const double CheckIntervalSec = 2.0;  // threat evaluation period per aircraft
        private const bool DropTanks = true;           // drop external fuel tanks when threatened
        private const bool DropBombs = true;           // also drop free-fall bombs / cluster stores
        private const bool IncludePlayerAircraft = true; // vanilla never auto-drops for the player (unless a debug flag)

        // Next time (mission seconds) each aircraft may be evaluated.
        private class State
        {
            public double NextCheck;
            public bool BombsDropped;
            public int BombTries;
            public double NextBombTry;
        }

        // Guided bombs need a ground point to "attack"; retry a few times (spaced) until the stores are gone.
        private const int MaxBombTries = 4;
        private const double BombRetrySec = 6.0;

        private static readonly ConditionalWeakTable<Aircraft, State> States = new ConditionalWeakTable<Aircraft, State>();

        private static void Postfix(AI __instance, ObjectBase ____baseObject)
        {
            try
            {
                Aircraft ac = ____baseObject as Aircraft;
                if (ac == null || !ac.HasControl || !ac._isInFlight || ac.IsLanding || ac.IsDestroyed)
                {
                    return;
                }

                if (!IncludePlayerAircraft && ac.IsPlayerObject)
                {
                    return;
                }

                State st = States.GetOrCreateValue(ac);
                double now = GameTime.missionElapsedTime;
                if (now < st.NextCheck)
                {
                    return;
                }

                st.NextCheck = now + CheckIntervalSec;

                bool haveTanks = DropTanks && !ac._fuelTanksDropped && ac._obp._fueltankAmmunitionGroups.Count > 0;
                bool haveBombs = DropBombs && !st.BombsDropped;
                if (!haveTanks && !haveBombs)
                {
                    return;
                }

                string reason = ThreatReason(ac);
                if (reason == null)
                {
                    return;
                }

                if (haveTanks)
                {
                    ac.DropFueltanks(combatDrop: true);
                    ModLog.Event($"t={now:0} JETTISON tanks: {ac.getUIDAndName()} ({reason}).");
                }

                if (haveBombs && now >= st.NextBombTry)
                {
                    st.BombTries++;
                    st.NextBombTry = now + BombRetrySec;
                    int left = DropBombStores(ac, reason, now, st.BombTries);
                    if (left == 0 || st.BombTries >= MaxBombTries)
                    {
                        st.BombsDropped = true;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("JettisonPatch.Postfix", ex);
            }
        }

        // Why we consider ourselves threatened, or null when we are not.
        private static string ThreatReason(Aircraft ac)
        {
            if (PostShotCrankPatch.MissileInbound(ac))
            {
                return "missile inbound";
            }

            foreach (ObjectBase c in PostShotCrankPatch.HostileAirContacts(ac))
            {
                if (PostShotCrankPatch.IsHotThreat(ac, c))
                {
                    return "hot hostile " + c.getUIDAndName();
                }
            }

            return null;
        }

        // Queue a jettison engage task (no target, priority -1 = same mechanism vanilla uses for tanks) for each
        // bomb type still on the aircraft. Bombs (including laser/GPS-guided ones such as GBU-12) are carried in the
        // launcher groups, free-fall ones in the hardpoint groups, so both are scanned. Fuel tanks are also typed
        // Bomb (subtype Fueltank) but are handled by DropFueltanks, so they are skipped here.
        // Returns how many bombs were still aboard when called (0 = nothing left to drop).
        private static int DropBombStores(Aircraft ac, string reason, double now, int attempt)
        {
            var files = new HashSet<string>();
            CollectBombs(ac._obp._hardpointAmmunitionGroups, files);
            CollectBombs(ac._obp._launcherAmmunitionGroups, files);

            // Laser/GPS bombs require a target; a null-target task is never executed. Aim at a sea-level point
            // ahead of the aircraft so the release is accepted.
            Vector3 fwd = ac.transform.forward;
            fwd.y = 0f;
            Vector3 ground = ac.transform.position + fwd.normalized * 2f;
            ground.y = 0f;

            int total = 0;
            foreach (string file in files)
            {
                int count = ac.getAmmunitionAmountByName(file);
                if (count > 0)
                {
                    total += count;
                    ac.InsertEngageTask(file, null, ground, count, -1);
                    ModLog.Event($"t={now:0} JETTISON bombs: {ac.getUIDAndName()} {file} x{count} ({reason}) try {attempt}.");
                }
            }

            return total;
        }

        private static void CollectBombs(Dictionary<string, AmmunitionGroup> groups, HashSet<string> files)
        {
            foreach (var group in groups)
            {
                foreach (Ammunition ammo in group.Value.getAmmunitionList())
                {
                    AmmunitionParameters ap = ammo._ap;
                    if ((ap._type == Ammunition.Type.Bomb || ap._type == Ammunition.Type.Cluster)
                        && ap._subType != Ammunition.Type.Fueltank && ap._subType != Ammunition.Type.Sonobuoy)
                    {
                        files.Add(ap._ammunitionFileName);
                    }
                }
            }
        }
    }
}
