using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SeaPower;
using SeapowerUI;
using Unity.Entities;

namespace SeaPowerFighterAI
{
    // Map emissions overlay.
    //
    // Adds a compact emitter tag to the per-unit data block (line 3) of map contacts:
    //
    //   Friendly (player + allied) units, ground truth:
    //     [XMIT RDR-A FC-A SON | TGT 7003 7006]   or   [EMCON]   or   [SILENT]
    //   Hostile / neutral contacts, ONLY what we have actually detected (no TGT, no EMCON/SILENT):
    //     [XMIT RDR-A FC-G SON]
    //
    //   RDR-A / RDR-G / RDR-A/G  search radar radiating; A = air search, G = surface/ground search
    //   FC-A  / FC-G  / FC-A/G   fire-control radar radiating (same role suffix)
    //   SON                      active sonar pinging
    //   EMCON                    emitters fitted but all silent and the unit is under forced EMCON
    //   SILENT                   emitters fitted but all silent (e.g. radar discipline, operator switched off)
    //   TGT                      track IDs the unit is working (tasks, AI target, FC radar tracks)
    //
    // A fire-control radar only counts as radiating while it is guiding (the same rule the game's own ESM model
    // uses); "powered" FC radars that are not illuminating are not shown.
    //
    // Hostile / neutral detection rule: a radar emitter is shown only if one of our sensors currently holds a fresh
    // ESM track on the contact; sonar only if we hold a fresh active-intercept track. So the tag never reveals an
    // emission the player side could not know about.
    //
    // Implemented as a prefix on the ContactInfoLine3 setter so the tag is merged into the text the game writes
    // each frame (no extra property-changed notifications / flicker). It follows the existing "emissions" map
    // layer toggle.
    [HarmonyPatch(typeof(MapUnitViewModel), nameof(MapUnitViewModel.ContactInfoLine3), MethodType.Setter)]
    public static class MapEmissionsOverlayPatch
    {
        // ---- Tunables -------------------------------------------------------------------------------------------
        private static readonly bool Enabled = true;
        private static readonly bool ShowTargets = true;
        private static readonly bool ShowHostileEmissions = true;
        private const int MaxTargetsShown = 3;

        private struct Emitters
        {
            public bool SearchAir, SearchGround, FcAir, FcGround, Sonar;
            public bool Search => SearchAir || SearchGround;
            public bool Fc => FcAir || FcGround;
            public bool Any => Search || Fc || Sonar;
        }

        public static void Prefix(MapUnitViewModel __instance, MapLayer ____infoEmissionsLayer, ref string value)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                ObjectBase obj = __instance.Unit?.BaseObject;
                if (obj == null || obj.IsDestroyed || obj._taskforce == null || !obj.isUnit() || obj._obp == null
                    || ____infoEmissionsLayer == null || !____infoEmissionsLayer.Visible)
                {
                    return;
                }

                bool friendly = IsFriendlySide(obj);
                if (!friendly && !ShowHostileEmissions)
                {
                    return;
                }

                string tag = friendly ? BuildFriendlyTag(obj) : BuildContactTag(__instance.Unit, obj);
                if (tag.Length == 0)
                {
                    return;
                }

                // Keep the altitude line (after the first newline) below our tag.
                value = value ?? string.Empty;
                int nl = value.IndexOf('\n');
                value = nl >= 0 ? value.Substring(0, nl) + tag + value.Substring(nl) : value + tag;
            }
            catch (System.Exception ex)
            {
                ModLog.Error("MapEmissionsOverlayPatch", ex);
            }
        }

        // Player and allied taskforces get the full tag; everything else is treated as a detected contact.
        private static bool IsFriendlySide(ObjectBase obj)
        {
            Taskforce.TfType side = obj._taskforce.Side;
            return side == Taskforce.TfType.Player || side == Taskforce.TfType.Ally;
        }

        // What is really radiating right now. Fire-control radars count only while guiding.
        private static Emitters ReadEmitters(ObjectBase obj)
        {
            var e = new Emitters();
            ObjectBaseParameters p = obj._obp;
            AddRadars(ref e, p._airSearchRadars, fireControl: false);
            AddRadars(ref e, p._surfaceSearchRadars, fireControl: false);
            AddRadars(ref e, p._searchRadarSystems, fireControl: false);
            AddRadars(ref e, p._targetingRadarSystems, fireControl: true);
            foreach (SensorSystemSonar s in p._activeSonarSystems)
            {
                if (s != null && !s.Inoperable.Value && s.IsOn.Value)
                {
                    e.Sonar = true;
                }
            }

            return e;
        }

        private static void AddRadars(ref Emitters e, List<SensorSystemRadar> radars, bool fireControl)
        {
            foreach (SensorSystemRadar r in radars)
            {
                if (r == null || r.Inoperable.Value || !r.IsOn.Value || (fireControl && !r._isGuiding))
                {
                    continue;
                }

                RadarParameters.Role role = r._radar._rp._role;
                bool air = role == RadarParameters.Role.Air || role == RadarParameters.Role.SurfaceAndAir;
                bool ground = role == RadarParameters.Role.Surface || role == RadarParameters.Role.SurfaceAndAir;
                if (!air && !ground)
                {
                    air = true; // unknown role: assume air
                }

                if (fireControl)
                {
                    e.FcAir |= air;
                    e.FcGround |= ground;
                }
                else
                {
                    e.SearchAir |= air;
                    e.SearchGround |= ground;
                }
            }
        }

        private static bool HasEmitterFitted(ObjectBaseParameters p)
        {
            return p._airSearchRadars.Count + p._surfaceSearchRadars.Count + p._searchRadarSystems.Count
                + p._targetingRadarSystems.Count + p._activeSonarSystems.Count > 0;
        }

        private static string RoleSuffix(bool air, bool ground)
        {
            return air && ground ? "-A/G" : ground ? "-G" : "-A";
        }

        private static void AppendEmitters(StringBuilder sb, Emitters e)
        {
            sb.Append("XMIT");
            if (e.Search)
            {
                sb.Append(" RDR").Append(RoleSuffix(e.SearchAir, e.SearchGround));
            }

            if (e.Fc)
            {
                sb.Append(" FC").Append(RoleSuffix(e.FcAir, e.FcGround));
            }

            if (e.Sonar)
            {
                sb.Append(" SON");
            }
        }

        private static string BuildFriendlyTag(ObjectBase obj)
        {
            if (!HasEmitterFitted(obj._obp))
            {
                return string.Empty;
            }

            Emitters e = ReadEmitters(obj);
            var sb = new StringBuilder(" [");
            if (e.Any)
            {
                AppendEmitters(sb, e);
            }
            else
            {
                sb.Append(obj._ai != null && obj._ai._excludeFromAIRadarRoutine ? "EMCON" : "SILENT");
            }

            if (ShowTargets)
            {
                string targets = TargetIds(obj);
                if (targets.Length > 0)
                {
                    sb.Append(" | TGT").Append(targets);
                }
            }

            return sb.Append(']').ToString();
        }

        // Hostile / neutral contact: show only emissions our own sensors have actually intercepted.
        private static string BuildContactTag(Vehicle contact, ObjectBase obj)
        {
            if (!HasEmitterFitted(obj._obp))
            {
                return string.Empty;
            }

            bool esm = false, activeIntercept = false;
            foreach (Entity track in contact.ValidTracks())
            {
                TrackComponent? tc = TrackEntityUtils.GetTrackComponent(track);
                if (!tc.HasValue)
                {
                    continue;
                }

                esm |= tc.Value.SensorType == SensorSystem.SensorType.ESM;
                activeIntercept |= tc.Value.SensorType == SensorSystem.SensorType.ActiveIntercept;
            }

            if (!esm && !activeIntercept)
            {
                return string.Empty;
            }

            Emitters e = ReadEmitters(obj);
            if (!esm)
            {
                e.SearchAir = e.SearchGround = e.FcAir = e.FcGround = false;
            }

            if (!activeIntercept)
            {
                e.Sonar = false;
            }

            if (!e.Any)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(" [");
            AppendEmitters(sb, e);
            return sb.Append(']').ToString();
        }

        // Distinct track IDs the unit is working, from tasks, the AI's chosen target and sensor associations.
        private static string TargetIds(ObjectBase obj)
        {
            var seen = new HashSet<ObjectBase>();
            var sb = new StringBuilder();
            int count = 0;

            void Add(ObjectBase t)
            {
                if (t == null || t.IsDestroyed || t.isWeapon() || count >= MaxTargetsShown || !seen.Add(t))
                {
                    return;
                }

                sb.Append(' ').Append(t.GetMapContactID());
                count++;
            }

            foreach (EngageTask task in obj._currentEngageTasks)
            {
                Add(task?._targetObject);
            }

            foreach (AlignUnitToTarget a in obj._alignmentTasks)
            {
                Add(a?._targetObject);
            }

            Add(obj._ai?._objectToDestroy);

            foreach (SensorSystemRadar r in obj._obp._targetingRadarSystems)
            {
                if (r != null)
                {
                    foreach (ObjectBase t in r._associatedTargets)
                    {
                        Add(t);
                    }
                }
            }

            return sb.ToString();
        }
    }
}
