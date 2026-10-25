using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Pulse-Doppler "notch" (beaming) model.
    //
    // Physics being modelled: an airborne pulse-Doppler radar looking DOWN at a target sees the ground return
    // (main-beam clutter) at a Doppler equal to the radar's own velocity along the line of sight. The filter that
    // rejects that clutter also rejects any target whose ground-referenced radial speed is near zero - i.e. a target
    // that flies ~perpendicular to the line of sight ("beaming" / "notching"). The notch is therefore a function of
    // the TARGET's velocity along the LOS, not of closure rate:
    //        notched  <=>  | v_target . los_unit |  <  half-width   (and the radar is looking down at the target)
    // A radar needs the target to stay in the notch for a short dwell (track memory) before it actually drops it.
    //
    // Where it is applied (symmetric for player and AI, all sides):
    //   1. RadarCalculator.createRadarEcho (long overload)  - airborne search radars and active missile seekers.
    //      A notched target yields no echo, so the contact is not painted / the seeker loses it.
    //   2. RadarCalculator.GetFCRDetectionRange (radar overload) - fire-control radar lock; returns 0 when notched
    //      so a notching target breaks lock, and a missile fired at it is not guided.
    // The radar/seeker identity is not passed to (1), so thin context patches set a thread-static "current emitter".
    //
    // Not modelled yet (see roadmap): semi-active CW illuminator notch, defensive AI that deliberately beams,
    // crew-skill scaling. Ground/ship radars are untouched (no look-down geometry).
    public static class NotchModel
    {
        // ---- Tunables ----
        public const bool Enabled = true;
        public const float LookDownMarginFt = 300f;    // target must be at least this far below the emitter to be in clutter
        public const float MinNotchRangeNm = 2f;       // inside this the geometry is meaningless / radar is not clutter limited
        public const float KtPerUnitSpeed = 1f / 0.0076554087f; // unity units/s -> kt
        public const float UnityToFt = 220.47266f;
        public const float NmToUnity = 27.559496f;
        private const double StateTtlSec = 30.0;       // forget idle emitter/target pairs
        private const double LogEverySec = 15.0;       // per-pair throttle for NOTCH log lines

        public class RadarClass
        {
            public string Name;
            public float HalfWidthKt;   // notch half-width (target ground-referenced radial speed, kt)
            public float DwellSec;      // time in the notch before the radar actually loses the target
        }

        // Generation presets. Half-widths are deliberately round numbers: tune from observed behaviour.
        public static readonly RadarClass None = new RadarClass { Name = "None", HalfWidthKt = 0f, DwellSec = 0f };
        public static readonly RadarClass PdEarly = new RadarClass { Name = "PD-early", HalfWidthKt = 75f, DwellSec = 4f };
        public static readonly RadarClass Pd = new RadarClass { Name = "PD", HalfWidthKt = 55f, DwellSec = 3f };
        public static readonly RadarClass Aesa = new RadarClass { Name = "PD-AESA", HalfWidthKt = 40f, DwellSec = 2f };
        public static readonly RadarClass Seeker = new RadarClass { Name = "Seeker-PD", HalfWidthKt = 45f, DwellSec = 1.5f };

        // First match wins. Anything airborne that matches nothing is treated as PD and logged once.
        private static readonly (Regex pattern, RadarClass cls)[] Table =
        {
            (new Regex(@"APG-?(77|79|80|81|82|83)|N036|RBE2.?AA|ES-?05|Captor.?E|KLJ-?7A", RegexOptions.IgnoreCase), Aesa),
            (new Regex(@"APG-?63(?!\()|APG-?63$", RegexOptions.IgnoreCase), PdEarly),
            (new Regex(@"APG-?(63|65|66|68|70|71|73)|APY-?(1|2|3)|N0(01|10|11|19|25|35)|Zhuk|RDI|RDY|RDM|Blue_?Fox|Foxhunter|Captor|Rubin", RegexOptions.IgnoreCase), Pd),
            (new Regex(@"^RP-?(15|22)|APQ-?\d|APS-?\d|JL-?7|Type_?773|Type\d", RegexOptions.IgnoreCase), None),
        };

        private static readonly Dictionary<string, RadarClass> Cache = new Dictionary<string, RadarClass>();
        private static readonly HashSet<string> LoggedUnknown = new HashSet<string>();

        public static RadarClass Classify(string radarName)
        {
            if (string.IsNullOrEmpty(radarName))
            {
                return Pd;
            }

            if (Cache.TryGetValue(radarName, out RadarClass c))
            {
                return c;
            }

            c = null;
            foreach ((Regex pattern, RadarClass cls) in Table)
            {
                if (pattern.IsMatch(radarName))
                {
                    c = cls;
                    break;
                }
            }

            if (c == null)
            {
                c = Pd;
                if (LoggedUnknown.Add(radarName))
                {
                    ModLog.Info("NOTCH: radar '" + radarName + "' not in table -> defaulting to " + c.Name);
                }
            }

            Cache[radarName] = c;
            return c;
        }

        // Chaff bloom: for a few seconds after a notching aircraft dumps chaff, its notch is wider (the cloud smears the
        // Doppler picture around it), which gives it room to manoeuvre vertically without being picked up.
        public const float ChaffBloomScale = 1.5f;     // notch half-width multiplier while the bloom lasts
        public const double ChaffBloomSeconds = 8.0;   // how long the bloom lasts
        private static readonly Dictionary<ObjectBase, double> ChaffUntil = new Dictionary<ObjectBase, double>();

        public static void StartChaffBloom(ObjectBase target)
        {
            ChaffUntil[target] = GameTime.missionElapsedTime + ChaffBloomSeconds;
        }

        public static float ChaffBloomFactor(ObjectBase target)
        {
            return ChaffUntil.TryGetValue(target, out double until) && GameTime.missionElapsedTime < until ? ChaffBloomScale : 1f;
        }

        // Emitter currently running a detection pass (set by the context patches below).
        [ThreadStatic] public static ObjectBase CtxSource;
        [ThreadStatic] public static string CtxName;
        [ThreadStatic] public static RadarClass CtxClass;

        // First time (mission seconds) the pair was continuously inside the notch, plus bookkeeping.
        private class Pair
        {
            public double Since;
            public double LastSeen;
            public double LastLog;
            public bool Dropped;
        }

        private static readonly Dictionary<long, Pair> Pairs = new Dictionary<long, Pair>();
        private static double _nextPrune;

        // True when 'target' is currently lost to 'source' because it is notching.
        public static bool IsNotched(ObjectBase source, string radarName, RadarClass cls, Vector3 sourcePos, ObjectBase target)
        {
            if (!Enabled || cls == null || cls.HalfWidthKt <= 0f || source == null || target == null)
            {
                return false;
            }

            // Only airborne emitters (aircraft radars, missile seekers) have look-down clutter geometry.
            if (!(source is Aircraft) && !(source is WeaponBase))
            {
                return false;
            }

            // Fixed-wing targets only: rotors fill the notch with blade returns, missiles are handled elsewhere.
            if (!(target is Aircraft))
            {
                return false;
            }

            Vector3 los = target.transform.position - sourcePos;
            float rangeNm = los.magnitude / NmToUnity;
            if (rangeNm < MinNotchRangeNm)
            {
                return false;
            }

            float lookDownFt = (sourcePos.y - target.transform.position.y) * UnityToFt;
            long key = ((long)source.GetInstanceID() << 32) ^ (uint)target.GetInstanceID();
            double now = GameTime.missionElapsedTime;
            Pair p;

            bool geometric = lookDownFt > LookDownMarginFt
                && Mathf.Abs(Vector3.Dot(target.UnityVelocityVector, los.normalized)) * KtPerUnitSpeed < cls.HalfWidthKt * ChaffBloomFactor(target);

            if (!geometric)
            {
                if (Pairs.TryGetValue(key, out p))
                {
                    if (p.Dropped)
                    {
                        ModLog.Event("NOTCH-END: " + source.getUIDAndName() + " [" + radarName + "] re-acquires " + target.getUIDAndName());
                    }

                    Pairs.Remove(key);
                }

                return false;
            }

            if (!Pairs.TryGetValue(key, out p))
            {
                p = new Pair { Since = now };
                Pairs[key] = p;
            }

            p.LastSeen = now;
            bool dropped = now - p.Since >= cls.DwellSec;
            if (dropped && now - p.LastLog >= LogEverySec)
            {
                p.LastLog = now;
                p.Dropped = true;
                float radial = Vector3.Dot(target.UnityVelocityVector, los.normalized) * KtPerUnitSpeed;
                ModLog.Event("NOTCH: " + source.getUIDAndName() + " [" + radarName + "/" + cls.Name + "] loses "
                    + target.getUIDAndName() + " radial=" + radial.ToString("F0") + "kt (notch +/-" + cls.HalfWidthKt.ToString("F0")
                    + "kt) range=" + rangeNm.ToString("F1") + "nm lookdown=" + lookDownFt.ToString("F0") + "ft");
            }

            if (now >= _nextPrune)
            {
                _nextPrune = now + StateTtlSec;
                List<long> stale = null;
                foreach (KeyValuePair<long, Pair> kv in Pairs)
                {
                    if (now - kv.Value.LastSeen > StateTtlSec)
                    {
                        (stale ?? (stale = new List<long>())).Add(kv.Key);
                    }
                }

                if (stale != null)
                {
                    foreach (long k in stale)
                    {
                        Pairs.Remove(k);
                    }
                }
            }

            return dropped;
        }
    }

    // Context: airborne search radar sweep.
    [HarmonyPatch(typeof(SearchRadar), "createRadarEchoes")]
    public static class NotchContextSearchRadar
    {
        private static void Prefix(SearchRadar __instance)
        {
            try
            {
                NotchModel.CtxSource = __instance._homeObject;
                NotchModel.CtxName = __instance._rp?._systemName;
                NotchModel.CtxClass = NotchModel.Classify(NotchModel.CtxName);
            }
            catch (Exception)
            {
                NotchModel.CtxSource = null;
            }
        }

        private static void Finalizer()
        {
            NotchModel.CtxSource = null;
        }
    }

    // Context: active radar missile seeker.
    [HarmonyPatch(typeof(ActiveRadarSeeker), "searchForTarget")]
    public static class NotchContextSeeker
    {
        private static readonly FieldInfo AmmoField = AccessTools.Field(typeof(SeekerBase), "_ammoObject");

        private static void Prefix(ActiveRadarSeeker __instance)
        {
            try
            {
                var ammo = AmmoField?.GetValue(__instance) as ObjectBase;
                NotchModel.CtxSource = ammo;
                NotchModel.CtxName = ammo != null ? ammo.name : "seeker";
                NotchModel.CtxClass = NotchModel.Seeker;
            }
            catch (Exception)
            {
                NotchModel.CtxSource = null;
            }
        }

        private static void Finalizer()
        {
            NotchModel.CtxSource = null;
        }
    }

    // Echo creation (long overload shared by search radars and seekers).
    [HarmonyPatch]
    public static class NotchEchoPatch
    {
        private static MethodBase TargetMethod()
        {
            foreach (MethodInfo m in typeof(RadarCalculator).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == "createRadarEcho" && m.GetParameters().Length > 10)
                {
                    return m;
                }
            }

            return null;
        }

        private static void Postfix(Vector3 sourcePosition, ObjectBase potentialContact, bool ignoreAspect, ref bool __result)
        {
            // ignoreAspect calls are max-range queries, not detections, so they are left alone.
            if (!__result || ignoreAspect || NotchModel.CtxSource == null)
            {
                return;
            }

            try
            {
                if (NotchModel.IsNotched(NotchModel.CtxSource, NotchModel.CtxName, NotchModel.CtxClass, sourcePosition, potentialContact))
                {
                    __result = false;
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("NOTCH echo patch: " + e.Message);
            }
        }
    }

    // Fire-control radar lock range: a notching target cannot be held, so the effective range collapses to zero.
    [HarmonyPatch]
    public static class NotchFcrPatch
    {
        private static MethodBase TargetMethod()
        {
            foreach (MethodInfo m in typeof(RadarCalculator).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetFCRDetectionRange")
                {
                    continue;
                }

                foreach (ParameterInfo pi in m.GetParameters())
                {
                    if (pi.ParameterType == typeof(SensorSystemRadar))
                    {
                        return m;
                    }
                }
            }

            return null;
        }

        private static void Postfix(Vector3 sourcePosition, SensorSystemRadar radarSystem, ObjectBase targetObject, ref float __result)
        {
            if (__result <= 0f)
            {
                return;
            }

            try
            {
                string name = radarSystem?._radar?._rp?._systemName;
                if (NotchModel.IsNotched(radarSystem?._baseObject, name, NotchModel.Classify(name), sourcePosition, targetObject))
                {
                    __result = 0f;
                }
            }
            catch (Exception e)
            {
                ModLog.Warn("NOTCH fcr patch: " + e.Message);
            }
        }
    }
}
