using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;

namespace SeaPowerFighterAI
{
    // Dedicated log file (BepInEx\SeaPowerFighterAI.log) that mirrors into the BepInEx console/LogOutput.log.
    // Never throws: logging must not be able to crash the game.
    public static class ModLog
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> ErrorCounts = new Dictionary<string, int>();
        private static string _path;

        // Past this many repeats of the same error, further copies are suppressed (a summary is still logged).
        private const int MaxRepeatsPerError = 5;

        // Master switch for the chatty diagnostics (pursuit/missile kinematic traces, command logging with stack
        // walks, per-hardpoint hold logging, loadout dumps). OFF for normal play: those hooks run every frame or on
        // every order and cost frame time. Enable via the "Diagnostics" option in
        // BepInEx\config\<plugin guid>.cfg (created on first run) when preparing a bug report.
        // Harmony patches that exist only for diagnostics check this in Prepare() and are not installed when off.
        public static bool Diagnostics;

        // Opt-in profiler: times every patch method and logs a PERF block every 30 s (see Perf.cs).
        public static bool PerfMonitor;
        // Per-feature switches derived from Diagnostics (kept as separate names so call sites read clearly).
        public static bool Verbose => Diagnostics;
        public static bool PursuitTrace => Diagnostics;
        public static bool CommandTrace => Diagnostics;

        // Hard cap on the log file so a long session cannot fill the disk or slow the flush thread.
        private const long MaxLogBytes = 20L * 1024 * 1024;
        private static long _written;

        private static readonly StringBuilder Pending = new StringBuilder();
        private static Timer _flushTimer;

        public static string Path => _path;

        public static void Init()
        {
            try
            {
                _path = System.IO.Path.Combine(Paths.BepInExRootPath, "SeaPowerFighterAI.log");
                try
                {
                    var cfg = new BepInEx.Configuration.ConfigFile(
                        System.IO.Path.Combine(Paths.ConfigPath, Plugin.PLUGIN_GUID + ".cfg"), true);
                    Diagnostics = cfg.Bind("Logging", "Diagnostics", false,
                        "Enable verbose diagnostic tracing (pursuit/missile traces, command log, missile-hold log). Costs frame time; use only for bug reports.").Value;
                    PerfMonitor = cfg.Bind("Logging", "PerfMonitor", false,
                        "Time every patch method and log a PERF summary every 30 s (frame times, GC, slowest patches). Small overhead; use to find lag.").Value;
                }
                catch
                {
                    Diagnostics = false;
                }

                File.WriteAllText(_path, $"=== {Plugin.PLUGIN_NAME} v{Plugin.PLUGIN_VERSION} log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (diagnostics {(Diagnostics ? "ON" : "off")}) ==={Environment.NewLine}");
                _flushTimer = new Timer(_ => Flush(), null, 2000, 2000);
            }
            catch
            {
                _path = null;
            }
        }

        public static void Info(string message) => Write("INFO", message, null, mirrorLevel: 0);

        public static void Flush()
        {
            try
            {
                string text;
                lock (Gate)
                {
                    if (Pending.Length == 0 || _path == null)
                    {
                        return;
                    }

                    text = Pending.ToString();
                    Pending.Length = 0;
                }

                // Stop writing once the cap is reached (one marker line is written so the cut-off is obvious).
                if (_written > MaxLogBytes)
                {
                    return;
                }

                _written += text.Length;
                if (_written > MaxLogBytes)
                {
                    text += "[log size cap reached; further lines dropped]" + Environment.NewLine;
                }

                File.AppendAllText(_path, text);
            }
            catch
            {
            }
        }

        public static void Warn(string message) => Write("WARN", message, null, mirrorLevel: 1);

        // Game-time stamped decision trace; also shown in the BepInEx console.
        public static void Event(string message) => Write("EVENT", message, null, mirrorLevel: 0);

        public static void Error(string context, Exception ex)
        {
            try
            {
                string key = context + "|" + ex.GetType().Name + "|" + ex.Message;
                int count;
                lock (Gate)
                {
                    ErrorCounts.TryGetValue(key, out count);
                    ErrorCounts[key] = ++count;
                }

                if (count <= MaxRepeatsPerError)
                {
                    Write("ERROR", $"{context}: {ex}", null, mirrorLevel: 2);
                }
                else if (count == MaxRepeatsPerError + 1)
                {
                    Write("ERROR", $"{context}: same error repeated, suppressing further copies ({ex.GetType().Name}: {ex.Message})", null, mirrorLevel: 2);
                }
            }
            catch
            {
            }
        }

        private static void Write(string level, string message, Exception ex, int mirrorLevel)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
                if (_path != null)
                {
                    lock (Gate)
                    {
                        Pending.AppendLine(line);
                    }
                }

                if (level == "ERROR" || level == "WARN")
                {
                    Flush();
                }

                // Per-decision events go to the file only; console mirroring is slow.
                if (level == "EVENT")
                {
                    return;
                }

                var log = Plugin.Log;
                if (log == null)
                {
                    return;
                }

                switch (mirrorLevel)
                {
                    case 2: log.LogError(message); break;
                    case 1: log.LogWarning(message); break;
                    default: log.LogInfo(message); break;
                }
            }
            catch
            {
            }
        }
    }
}

