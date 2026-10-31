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
        private static readonly bool ShowStatus = true;
        private static readonly bool ShowHostileEmissions = true;
        private const int MaxTargetsShown = 3;
        private const float CacheSec = 0.5f;

        private struct CachedTag
        {
            public string Text;
            public float Expires;
        }

        private static readonly Dictionary<ObjectBase, CachedTag> _cache = new Dictionary<ObjectBase, CachedTag>();
        private static float _pruneAt;

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
                if (obj is Missile missile)
                {
                    string mtag = ShowStatus && !missile.IsDestroyed && missile._taskforce != null && IsFriendlySide(missile)
                        ? MissileTag(missile) : string.Empty;
                    if (mtag.Length > 0)
                    {
                        value = (value ?? string.Empty) + mtag;
                    }

                    return;
                }

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

                // The setter fires every frame for every visible unit; the tag only needs refreshing a couple of times a second.
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now > _pruneAt)
                {
                    _pruneAt = now + 30f;
                    _cache.Clear();
                    List<Missile> gone = null;
                    foreach (Missile k in NotchedUntil.Keys)
                    {
                        if (k == null || k.IsDestroyed)
                        {
                            (gone ?? (gone = new List<Missile>())).Add(k);
                        }
                    }

                    if (gone != null)
                    {
                        foreach (Missile k in gone)
                        {
                            NotchedUntil.Remove(k);
                        }
                    }
                }

                string tag;
                if (_cache.TryGetValue(obj, out CachedTag c) && now < c.Expires)
                {
                    tag = c.Text;
                }
                else
                {
                    tag = friendly ? BuildFriendlyTag(obj) + StatusTag(obj) : BuildContactTag(__instance.Unit, obj);
                    _cache[obj] = new CachedTag { Text = tag, Expires = now + CacheSec };
                }

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

        private const double NotchedLabelSec = 3.0;
        private static readonly Dictionary<Missile, double> NotchedUntil = new Dictionary<Missile, double>();

        // Friendly missile state: NOTCHED (shown for 3 s when the link is lost to a notch, then COAST), COAST (link lost),
        // ACTIVE (own seeker on), GUIDED (launcher/datalink guiding), IR (heat seeker). Any state other than COAST
        // overrides NOTCHED immediately.
        private static string MissileTag(Missile m)
        {
            if (!m.isLaunched() || m._seeker == null)
            {
                return string.Empty;
            }

            double now = GameTime.missionElapsedTime;
            string s;
            if (m._seeker.isActive())
            {
                s = "ACTIVE";
            }
            else if (m._seeker._guidanceType == AmmunitionParameters.GuidanceType.IRHoming)
            {
                s = "IR";
            }
            else if (m.ConnectionLost.Value || MissileNotchLockPatch.IsForcedLost(m))
            {
                s = "COAST";
                if (!NotchedUntil.ContainsKey(m))
                {
                    ObjectBase radar = m._guidingRadarSystem != null ? m._guidingRadarSystem._baseObject : null;
                    if (MissileNotchLockPatch.IsForcedLost(m) || NotchModel.IsPairDropped(radar, m.CurrentIntendedTargetObject))
                    {
                        NotchedUntil[m] = now + NotchedLabelSec;
                    }
                }

                if (NotchedUntil.TryGetValue(m, out double until) && now < until)
                {
                    s = "NOTCHED";
                }
            }
            else
            {
                s = "GUIDED";
                NotchedUntil.Remove(m);
            }

            if (s != "COAST" && s != "NOTCHED")
            {
                NotchedUntil.Remove(m);
            }

            return " <" + s + ">";
        }

        // What a friendly fixed-wing aircraft is doing, as one short label (highest priority first):
        //   DEF-NOTCH / DEF-REV / DEF-HOLD / DEF-PIT / DEF-KIN / DEF-LDM  automated missile defence, by phase
        //   NOTCH   player-assist notch      EXT   wingman extend      CRANK / PRESS   after a missile launch
        //   PUR     hot pursuit of an ordered target      ATT   attack tasks or a target assigned
        // Hostile contacts get no label (it would reveal what the enemy AI is doing).
        private static string StatusTag(ObjectBase obj)
        {
            if (!ShowStatus || !(obj is Aircraft ac))
            {
                return string.Empty;
            }

            string label = MissileDefense.Label(ac);
            if (label == null && NotchGuidance.IsActive(ac))
            {
                label = "NOTCH";
            }

            if (label == null && WingmanExtend.IsExtending(ac))
            {
                label = "EXT";
            }

            label = label ?? PostShotCrankPatch.Label(ac);
            if (label == null && HotPursuit.Records.ContainsKey(ac))
            {
                label = "PUR";
            }

            if (label == null && (ac._currentEngageTasks.Count > 0 || (ac._ai != null && ac._ai._objectToDestroy != null)))
            {
                label = "ATT";
            }

            return label == null ? string.Empty : " <" + label + ">";
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
