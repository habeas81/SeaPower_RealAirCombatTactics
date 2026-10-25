using System;
using HarmonyLib;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Diagnostic only: logs every engage task an aircraft receives against an air target, so shots can be
    // lined up against the HOLD/OVERRIDE/ALLOW lines and missile telemetry.
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.AddEngageTask))]
    public static class EngageTaskLogPatch
    {
        private static void Postfix(ObjectBase __instance, EngageTask engageTask)
        {
            try
            {
                if (!(__instance is Aircraft) || engageTask == null || engageTask._targetObject == null
                    || !engageTask._targetObject.isAirTarget() || engageTask._targetObject.isWeapon())
                {
                    return;
                }

                if (ModLog.Verbose) ModLog.Event($"t={GameTime.missionElapsedTime:0} ENGAGE TASK: {__instance.getUIDAndName()} -> {engageTask._targetObject.getUIDAndName()}, "
                             + $"ammo={engageTask._ammoId}, shots={engageTask._shotsToFire}, auto={engageTask._isAutoAttack}.");
            }
            catch (Exception ex)
            {
                ModLog.Error("EngageTaskLogPatch.Postfix", ex);
            }
        }
    }
}

