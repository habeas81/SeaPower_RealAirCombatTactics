using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Opt-in profiler (config "PerfMonitor"). It wraps every Harmony patch method this mod installed with a stopwatch,
    // so the log shows exactly which patch costs frame time, without hand-instrumenting each one. Every ReportSec it
    // writes one "PERF" block: frame-time stats, GC activity and memory, and the most expensive patch methods.
    // Overhead is a dictionary lookup per patch call, so leave it off for normal play.
    public static class Perf
    {
        private const double ReportSec = 30.0;  // interval between PERF blocks
        private const int TopN = 12;            // patch methods listed per block
        private const float SpikeMs = 33.3f;    // frame counted as a stutter above this (under 30 fps)
        private const float HitchMs = 100f;     // frame counted as a hitch above this

        private sealed class Stat
        {
            public string Name;
            public long Calls;
            public long Ticks;
            public long MaxTicks;
        }

        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private static int _lastFrame = -1;
        private static int _frames, _spikes, _hitches;
        private static float _maxFrameMs, _sumFrameMs;
        private static float _nextReport;
        private static int _gc0, _gc1, _gc2;
        private static long _startedMem;

        public static bool Enabled { get; private set; }

        // Call after the mod's own patches are applied. Does nothing unless enabled.
        public static void Init(Harmony owner, bool enabled)
        {
            Enabled = enabled;
            if (!enabled)
            {
                return;
            }

            try
            {
                Harmony perf = new Harmony(owner.Id + ".perf");
                HarmonyMethod begin = new HarmonyMethod(typeof(Perf).GetMethod(nameof(Begin), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                HarmonyMethod end = new HarmonyMethod(typeof(Perf).GetMethod(nameof(End), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
                HashSet<MethodInfo> seen = new HashSet<MethodInfo>();
                int wrapped = 0;
                foreach (MethodBase target in owner.GetPatchedMethods().ToList())
                {
                    Patches info = Harmony.GetPatchInfo(target);
                    if (info == null)
                    {
                        continue;
                    }

                    foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Where(x => x.owner == owner.Id))
                    {
                        MethodInfo pm = p.PatchMethod;
                        if (pm == null || !seen.Add(pm))
                        {
                            continue;
                        }

                        try
                        {
                            perf.Patch(pm, begin, end);
                            wrapped++;
                        }
                        catch (Exception ex)
                        {
                            ModLog.Warn($"Perf: could not time {pm.DeclaringType?.Name}.{pm.Name}: {ex.Message}");
                        }
                    }
                }

                _startedMem = GC.GetTotalMemory(false);
                _gc0 = GC.CollectionCount(0);
                _gc1 = GC.CollectionCount(1);
                _gc2 = GC.CollectionCount(2);
                _nextReport = Time.realtimeSinceStartup + (float)ReportSec;
                ModLog.Info($"PerfMonitor ON: timing {wrapped} patch methods, report every {ReportSec:0} s.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                ModLog.Error("Perf.Init", ex);
            }
        }

        private static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void End(MethodBase __originalMethod, long __state)
        {
            long ticks = Stopwatch.GetTimestamp() - __state;
            if (!Stats.TryGetValue(__originalMethod, out Stat s))
            {
                s = new Stat { Name = (__originalMethod.DeclaringType?.Name ?? "?") + "." + __originalMethod.Name };
                Stats[__originalMethod] = s;
            }

            s.Calls++;
            s.Ticks += ticks;
            if (ticks > s.MaxTicks)
            {
                s.MaxTicks = ticks;
            }

            // Frame timing piggybacks on the patch calls: once per new frame, record the last frame's duration.
            int frame = Time.frameCount;
            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                float ms = Time.unscaledDeltaTime * 1000f;
                _frames++;
                _sumFrameMs += ms;
                if (ms > _maxFrameMs) _maxFrameMs = ms;
                if (ms > SpikeMs) _spikes++;
                if (ms > HitchMs) _hitches++;
                if (Time.realtimeSinceStartup >= _nextReport)
                {
                    Report();
                }
            }
        }

        private static void Report()
        {
            _nextReport = Time.realtimeSinceStartup + (float)ReportSec;
            try
            {
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                double memMb = GC.GetTotalMemory(false) / 1048576.0;
                double growthMb = (GC.GetTotalMemory(false) - _startedMem) / 1048576.0;
                StringBuilder sb = new StringBuilder();
                sb.Append($"PERF t={GameTime.missionElapsedTime:0} frames={_frames} avg={(_frames > 0 ? _sumFrameMs / _frames : 0):0.0}ms max={_maxFrameMs:0}ms ")
                  .Append($">33ms={_spikes} >100ms={_hitches} | GC gen0/1/2 +{g0 - _gc0}/+{g1 - _gc1}/+{g2 - _gc2} heap={memMb:0}MB (+{growthMb:0} since start)");
                double totalMs = Stats.Values.Sum(x => x.Ticks) * MsPerTick;
                sb.Append($" | mod patches {totalMs:0}ms total ({(_frames > 0 ? totalMs / _frames : 0):0.00} ms/frame)");
                foreach (Stat s in Stats.Values.OrderByDescending(x => x.Ticks).Take(TopN))
                {
                    double ms = s.Ticks * MsPerTick;
                    sb.Append($"{System.Environment.NewLine}    {s.Name,-46} total={ms,8:0.0}ms calls={s.Calls,8} avg={ms * 1000.0 / Math.Max(1, s.Calls),8:0.0}us max={s.MaxTicks * MsPerTick,7:0.00}ms");
                }

                ModLog.Event(sb.ToString());

                _gc0 = g0;
                _gc1 = g1;
                _gc2 = g2;
                _frames = _spikes = _hitches = 0;
                _maxFrameMs = _sumFrameMs = 0f;
                foreach (Stat s in Stats.Values)
                {
                    s.Calls = 0;
                    s.Ticks = 0;
                    s.MaxTicks = 0;
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("Perf.Report", ex);
            }
        }
    }
}
