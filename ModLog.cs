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

        // Set true to re-enable chatty diagnostics for features already validated in game (costs frame time).
        public static bool Verbose = false;

        // Kinematic trace of pursuit geometry (lead vs direct); small cost, 1 line per engager per 5 s.
        public static bool PursuitTrace = true;

        // Logs orders / engage tasks / speed / waypoint commands with their caller chain ("CMD" lines).
        public static bool CommandTrace = true;

        private static readonly StringBuilder Pending = new StringBuilder();
        private static Timer _flushTimer;

        public static string Path => _path;

        public static void Init()
        {
            try
            {
                _path = System.IO.Path.Combine(Paths.BepInExRootPath, "SeaPowerFighterAI.log");
                File.WriteAllText(_path, $"=== {Plugin.PLUGIN_NAME} v{Plugin.PLUGIN_VERSION} log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
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

