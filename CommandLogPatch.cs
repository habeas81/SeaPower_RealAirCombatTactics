using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Diagnostic: logs orders / engage tasks / speed / waypoint changes on aircraft together with a short
    // caller chain, so we can tell a player command from automation, and see whether a command reached
    // the whole formation or just the leader. Lines are prefixed "CMD".
    //   CMD ...      = the game method that was called (and who called it)
    // Reading the caller chain: UI types (SeapowerUI.*) = player input; AI / state classes = automation.
    [HarmonyPatch]
    public static class CommandLogPatch
    {
        // ---- Tunables ----
        private const int MaxCallerFrames = 4;        // frames shown in the "via" chain
        private const double SpeedLogMinGapSec = 5.0; // per-unit throttle for speed-command lines (they are frequent)

        // Last speed-line time per unit uid, to keep the log readable.
        private static readonly Dictionary<ObjectBase, double> LastSpeedLog = new Dictionary<ObjectBase, double>();

        // Methods to watch (all on ObjectBase / Aircraft). Looked up by name so overloads are all covered.
        private static readonly string[] ObjectBaseMethods =
        {
            "setOrder", "InsertEngageTask", "AddEngageTask", "SetSpeedCommand",
            "setWaypointTask", "SetWaypointTask", "SetAttackAtWaypointTask", "SetRelativeWaypointTask",
            "RemoveWaypoints", "DeleteSelectedWaypoint", "DeleteWaypoints"
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in ObjectBaseMethods)
            {
                foreach (MethodInfo m in typeof(ObjectBase).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name)
                    {
                        yield return m;
                    }
                }
            }

            yield return AccessTools.Method(typeof(Aircraft), "setTelegraph");
        }

        private static void Postfix(ObjectBase __instance, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (!(__instance is Aircraft) || !ModLog.CommandTrace)
                {
                    return;
                }

                string name = __originalMethod.Name;
                if (name == "SetSpeedCommand" || name == "setTelegraph")
                {
                    double now = GameTime.missionElapsedTime;
                    if (LastSpeedLog.TryGetValue(__instance, out double last) && now - last < SpeedLogMinGapSec)
                    {
                        return;
                    }

                    LastSpeedLog[__instance] = now;
                }

                // AddEngageTask is called by InsertEngageTask; log only the insert to avoid doubles unless called directly.
                var sb = new StringBuilder();
                sb.Append($"t={GameTime.missionElapsedTime:0} CMD {name}: {__instance.getUIDAndName()}");
                sb.Append(Role(__instance));
                sb.Append(Describe(name, __args));
                sb.Append(" via ").Append(Callers());
                ModLog.Event(sb.ToString());
            }
            catch (Exception ex)
            {
                ModLog.Error("CommandLogPatch.Postfix", ex);
            }
        }

        // Player / formation role of the unit receiving the command.
        private static string Role(ObjectBase ob)
        {
            string s = ob.IsPlayerObject ? " [player" : " [ai";
            if (ob.Formation != null)
            {
                s += ob.IsFormationLeader ? ",formation-LEAD" : ",formation-wingman";
                s += ob.FollowingFormation ? "(following)" : "(independent)";
            }

            return s + "]";
        }

        // Human-readable arguments.
        private static string Describe(string name, object[] a)
        {
            var sb = new StringBuilder(" ");
            if (a == null)
            {
                return "";
            }

            foreach (object o in a)
            {
                if (o is ObjectBase ob)
                {
                    sb.Append("target=").Append(ob.getUIDAndName()).Append(' ');
                }
                else if (o is EngageTask et)
                {
                    sb.Append($"ammo={et._ammoId} target={(et._targetObject != null ? et._targetObject.getUIDAndName() : "none")} "
                              + $"shots={et._shotsToFire} auto={et._isAutoAttack} formationAttack={et._isFormationAttack} prio={et._priority} ");
                }
                else if (o is ISpeedCommand sc)
                {
                    sb.Append($"speed={sc.GetType().Name}({sc.SpeedInMach:0.00}M) ");
                }
                else if (o is WaypointData wp)
                {
                    sb.Append("waypoint=").Append(wp).Append(' ');
                }
                else if (o != null)
                {
                    sb.Append(o).Append(' ');
                }
            }

            return sb.ToString().TrimEnd();
        }

        // Compact "Class.Method <- Class.Method ..." of the first non-mod, non-Harmony frames above the patched method.
        private static string Callers()
        {
            var st = new StackTrace(3, false);
            var parts = new List<string>();
            foreach (StackFrame f in st.GetFrames() ?? new StackFrame[0])
            {
                MethodBase m = f.GetMethod();
                Type t = m?.DeclaringType;
                if (t == null || t.Namespace == "SeaPowerFighterAI" || t.Namespace == "HarmonyLib" || t.Namespace == "MonoMod.RuntimeDetour"
                    || m.Name.StartsWith("DMD<") || m.Name.StartsWith("_Patch"))
                {
                    continue;
                }

                parts.Add(t.Name + "." + m.Name);
                if (parts.Count >= MaxCallerFrames)
                {
                    break;
                }
            }

            return parts.Count == 0 ? "?" : string.Join(" <- ", parts);
        }
    }
}
