using System.Collections.Generic;
using HarmonyLib;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Diagnostic for plain right-click attack orders on aircraft (no weapon chosen): vanilla only sets
    // AI._objectToDestroy / the formation target list, and a later AI tick must turn that into an engage task.
    // Logs, every 5 s per aircraft while an order is pending, where that hand-off stalls.
    [HarmonyPatch(typeof(AI), nameof(AI.OnUpdate))]
    public static class ClickOrderDiagPatch
    {
        private const double LogIntervalSec = 5.0;

        // Diagnostic only: installed only when diagnostics are enabled.
        private static bool Prepare() => ModLog.Diagnostics;
        private static readonly Dictionary<int, double> LastLog = new Dictionary<int, double>();

        private static void Postfix(AI __instance, ObjectBase ____baseObject)
        {
            try
            {
                if (!(____baseObject is Aircraft) || !____baseObject.IsPlayerObject) return;
                ObjectBase tgt = __instance._objectToDestroy;
                int listCount = __instance._objectsToDestroyList.Count;
                int fCount = ____baseObject.Formation != null ? ____baseObject.Formation._objectsToDestroy.Count : 0;
                if (tgt == null && listCount == 0 && fCount == 0) return;

                double now = GameTime.missionElapsedTime;
                int id = ____baseObject.GetInstanceID();
                if (LastLog.TryGetValue(id, out double last) && now - last < LogIntervalSec) return;
                LastLog[id] = now;

                ObjectBase probe = tgt ?? (listCount > 0 ? __instance._objectsToDestroyList[0] : null);
                string prio = probe != null ? ____baseObject.GetEngagePriority(probe).ToString() : "-";
                ModLog.Event($"t={now:0} CLICK-ORDER-DIAG {____baseObject.getUIDAndName()}: objectToDestroy={(tgt != null ? tgt.getUIDAndName() : "null")} " +
                             $"aiList={listCount} formList={fCount} engageTasks={____baseObject._currentEngageTasks.Count} " +
                             $"weaponStatus={____baseObject._weaponStatus} prio={prio} aligning={____baseObject.Aligning} " +
                             $"order={____baseObject.CurrentOrderText}");
            }
            catch { }
        }
    }
}
