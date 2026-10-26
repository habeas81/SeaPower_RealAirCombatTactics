using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Diagnostic: logs every aircraft's full stores (all ammunition groups with type / subtype / guidance / count)
    // once, shortly after it first ticks, so jettison and weapon-selection logic can be checked against what the
    // aircraft really carries (e.g. whether CBUs / Mk82s are Type.Bomb or Type.Cluster, or sit in another group).
    [HarmonyPatch(typeof(Aircraft), "OnFixedUpdate")]
    public static class LoadoutLogPatch
    {
        private const double SettleSeconds = 2.0;   // wait this long after the first tick so stores are fully loaded

        // Diagnostic only (hooks every aircraft tick): installed only when diagnostics are enabled.
        private static bool Prepare() => ModLog.Diagnostics;

        private class State
        {
            public double FirstSeen = -1;
            public bool Logged;
        }

        private static readonly ConditionalWeakTable<Aircraft, State> States = new ConditionalWeakTable<Aircraft, State>();

        private static void Postfix(Aircraft __instance)
        {
            State st = States.GetOrCreateValue(__instance);
            if (st.Logged)
            {
                return;
            }

            try
            {
                double now = GameTime.missionElapsedTime;
                if (st.FirstSeen < 0)
                {
                    st.FirstSeen = now;
                }

                if (now - st.FirstSeen < SettleSeconds || __instance._obp == null)
                {
                    return;
                }

                st.Logged = true;
                ObjectBaseParameters p = __instance._obp;
                ModLog.Event($"LOADOUT {__instance.getUIDAndName()} crew={(__instance._crew != null ? __instance._crew.OverallSkill.ToString() : "?")}");
                Dump(__instance, "hardpoint", p._hardpointAmmunitionGroups);
                Dump(__instance, "launcher", p._launcherAmmunitionGroups);
                Dump(__instance, "gun", p._gunAmmunitionGroups);
                Dump(__instance, "chaff", p._chaffAmmunitionGroups);
                Dump(__instance, "fueltank", p._fueltankAmmunitionGroups);
            }
            catch (Exception ex)
            {
                st.Logged = true;
                ModLog.Error("LoadoutLogPatch", ex);
            }
        }

        private static void Dump(Aircraft ac, string groupName, Dictionary<string, AmmunitionGroup> groups)
        {
            if (groups == null)
            {
                return;
            }

            foreach (KeyValuePair<string, AmmunitionGroup> g in groups)
            {
                List<Ammunition> list = g.Value.getAmmunitionList();
                if (list == null || list.Count == 0)
                {
                    continue;
                }

                AmmunitionParameters ap = list[0]._ap;
                ModLog.Event($"LOADOUT   {ac.getUIDAndName()} [{groupName}] {ap._ammunitionFileName} x{list.Count} (held {ac.getAmmunitionAmountByName(ap._ammunitionFileName)}) type={ap._type} sub={ap._subType} guidance={ap._guidanceType}");
            }
        }
    }
}
