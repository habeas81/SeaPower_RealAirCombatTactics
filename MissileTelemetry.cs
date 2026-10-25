using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Kinematic trace for every missile guiding on an air target (any side, player or AI):
    // missile and target position/heading/speed plus the intercept point the missile is steering at,
    // so lead vs direct pursuit can be verified from geometry.
    [HarmonyPatch(typeof(Missile), "CalculateInterceptAimPoint")]
    public static class MissileTelemetry
    {
        private const double IntervalSeconds = 2.0;

        private class Box
        {
            public double Last = -100.0;
            public bool Announced;
        }

        private static readonly ConditionalWeakTable<Missile, Box> Boxes = new ConditionalWeakTable<Missile, Box>();

        private static void Postfix(Missile __instance)
        {
            if (!ModLog.PursuitTrace)
            {
                return;
            }

            try
            {
                Missile m = __instance;
                ObjectBase target = m.CurrentIntendedTargetObject ?? m.CurrentTarget;
                if (m == null || m.IsDestroyed || target == null || target.IsDestroyed || !target.isAirTarget() || m._ap == null)
                {
                    return;
                }

                Box box = Boxes.GetOrCreateValue(m);
                double now = GameTime.missionElapsedTime;
                if (now - box.Last < IntervalSeconds)
                {
                    return;
                }

                box.Last = now;
                Vector3 aim = m._leadTarget ? m._targetInterceptPosition.ToUnity() : m._currentKnownTargetPosition;
                string text = PursuitTelemetry.Describe(m.transform.position, m._velocityVecInUnity, m.transform.forward,
                    m._currentKnownTargetPosition, m._lastKnownTargetVelocity, aim, aim);

                string launcher = m._launchPlatform != null ? m._launchPlatform.getUIDAndName() : "?";
                string head = box.Announced ? "" : $" [launch: forceDirectPoint={m._ap._forceDirectPoint}, navGain={m._ap._navigationGain:0.0}]";
                box.Announced = true;
                ModLog.Event($"t={now:0} MISSILE-PURSUIT {m._ap._ammunitionFileName} ({launcher}) -> {target.getUIDAndName()}: leadTarget={m._leadTarget}, stage={m._flightStage}, targetLost={m._targetLost}: {text}{head}");
            }
            catch (System.Exception ex)
            {
                ModLog.Error("MissileTelemetry.Postfix", ex);
            }
        }
    }
}
