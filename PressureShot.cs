using System;
using SeaPower;

namespace SeaPowerFighterAI
{
    internal static class PressureShot
    {
        // True when `enemy` has a missile in flight at a friendly aircraft other than `shooter`.
        // `shooter` can then fire to force the enemy defensive; the shot need not kill.
        public static bool IsActive(ObjectBase shooter, ObjectBase enemy, out ObjectBase victim)
        {
            victim = null;
            if (shooter == null || enemy == null || enemy._obp?._weaponSystems == null)
            {
                return false;
            }

            foreach (WeaponSystem system in enemy._obp._weaponSystems)
            {
                if (system?._launchedWeapons == null)
                {
                    continue;
                }

                foreach (WeaponBase weapon in system._launchedWeapons)
                {
                    if (weapon == null || weapon.IsDestroyed || weapon.IsDefeatedShot || !(weapon is Missile))
                    {
                        continue;
                    }

                    ObjectBase intended = weapon.CurrentIntendedTargetObject ?? weapon.CurrentTarget;
                    if (intended != null && !intended.IsDestroyed && intended != shooter && intended.IsAirUnit
                        && intended._taskforce == shooter._taskforce)
                    {
                        victim = intended;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
