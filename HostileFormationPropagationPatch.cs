using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using Unity.Entities;

namespace SeaPowerFighterAI
{
    // If one aircraft in a formation is classified hostile on a side's plotting table,
    // propagate that hostile classification to its formation-mates on the same table.
    [HarmonyPatch(typeof(PlottingTable), nameof(PlottingTable.Update))]
    public static class HostileFormationPropagationPatch
    {
        private static readonly Dictionary<string, double> LastLog = new Dictionary<string, double>();

        // The scan walks every plotted contact, so run it at most once per interval per table.
        private const double ScanIntervalSec = 1.0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlottingTable, double[]> NextScan =
            new System.Runtime.CompilerServices.ConditionalWeakTable<PlottingTable, double[]>();

        private static void Postfix(PlottingTable __instance)
        {
            if (__instance?.LocalVehicles == null || __instance.LocalVehicles.Count == 0)
            {
                return;
            }

            double[] next = NextScan.GetValue(__instance, _ => new double[1]);
            double clock = GameTime.missionElapsedTime;
            if (clock < next[0] && next[0] - clock < ScanIntervalSec * 2)
            {
                return;
            }

            next[0] = clock + ScanIntervalSec;

            Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return;
            }

            EntityManager em = world.EntityManager;
            foreach (KeyValuePair<ObjectBase, Vehicle> kv in __instance.LocalVehicles)
            {
                ObjectBase contact = kv.Key;
                Vehicle track = kv.Value;
                if (contact == null || track == null || !contact.IsAirUnit || !contact.InFormation.Value || contact.Formation == null)
                {
                    continue;
                }

                if (track.CurrentRelationship() != RelationsState.Hostile)
                {
                    continue;
                }

                foreach (Station station in contact.Formation.Stations)
                {
                    ObjectBase mate = station?.UnitObject;
                    if (mate == null || mate == contact || !mate.IsAirUnit)
                    {
                        continue;
                    }

                    if (!__instance.LocalVehicles.TryGetValue(mate, out Vehicle mateTrack) || mateTrack == null)
                    {
                        continue;
                    }

                    if (mateTrack.CurrentRelationship() == RelationsState.Hostile || mateTrack.Entity == Entity.Null || !em.Exists(mateTrack.Entity))
                    {
                        continue;
                    }

                    ForcedRelationState forced = new ForcedRelationState { ForcedState = RelationsState.Hostile };
                    if (em.HasComponent<ForcedRelationState>(mateTrack.Entity))
                    {
                        em.SetComponentData(mateTrack.Entity, forced);
                    }
                    else
                    {
                        em.AddComponentData(mateTrack.Entity, forced);
                    }

                    LogPropagation(contact, mate, __instance);
                }
            }
        }

        private static void LogPropagation(ObjectBase hostile, ObjectBase mate, PlottingTable table)
        {
            if (!ModLog.Verbose)
            {
                return;
            }

            double now = GameTime.missionElapsedTime;
            string key = table.GetHashCode() + ":" + hostile.GetInstanceID() + ":" + mate.GetInstanceID();
            if (LastLog.TryGetValue(key, out double last) && now - last < 20.0)
            {
                return;
            }

            LastLog[key] = now;
            ModLog.Event($"t={now:0} CLASSIFY: propagated HOSTILE from {hostile.getUIDAndName()} to formation-mate {mate.getUIDAndName()}.");
        }
    }
}

