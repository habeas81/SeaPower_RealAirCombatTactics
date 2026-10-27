using System;
using System.Linq;
using System.Reflection;
using AnchorChain;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // Entry point used by AnchorChain (Sea Power's community chainloader), which loads this DLL straight from the
    // enabled mod folder (Mods menu / Steam Workshop). AnchorChain scans every mod folder for a class implementing
    // IAnchorChainMod and tagged with [ACPlugin], then calls TriggerEntryPoint once at start-up.
    [ACPlugin(Plugin.PLUGIN_GUID, Plugin.PLUGIN_NAME, Plugin.PLUGIN_VERSION)]
    public class AnchorChainEntry : IAnchorChainMod
    {
        public void TriggerEntryPoint() => Plugin.Init();
    }

    public static class Plugin
    {
        public const string PLUGIN_GUID = "com.habeas81.seapower.realaircombattactics";
        public const string PLUGIN_NAME = "Real Air Combat Tactics";
        public const string PLUGIN_VERSION = "0.9.1";

        // Internal logger so our patches can print debug info to the BepInEx console/log
        internal static ManualLogSource Log;

        private static bool _initialised;

        public static void Init()
        {
            // Guard: never patch twice (e.g. if the DLL ends up loaded from two places).
            if (_initialised)
            {
                return;
            }

            _initialised = true;
            Log = BepInEx.Logging.Logger.CreateLogSource(PLUGIN_NAME);
            ModLog.Init();
            ModLog.Info($"{PLUGIN_NAME} v{PLUGIN_VERSION} starting. Detailed log: {ModLog.Path}");

            // Attribute crashes to this mod: log any unhandled/Unity-reported exception whose stack mentions us.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.logMessageReceived += OnUnityLog;

            try
            {
                ApplyPatches();
            }
            catch (Exception ex)
            {
                ModLog.Error("Patch setup failed", ex);
            }
        }
        // Patch each class on its own so one broken patch (e.g. a game update renamed a method)
        // is logged and skipped instead of taking the whole mod down.
        private static void ApplyPatches()
        {
            Harmony harmony = new Harmony(PLUGIN_GUID);
            int ok = 0, failed = 0;

            foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly())
                         .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0))
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                    ModLog.Info($"Patched OK: {type.Name}");
                }
                catch (Exception ex)
                {
                    failed++;
                    ModLog.Error($"Patch FAILED: {type.Name}", ex);
                }
            }

            ModLog.Info($"Patching complete: {ok} ok, {failed} failed.");
            Perf.Init(harmony, ModLog.PerfMonitor);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                ModLog.Info($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}): {e.ExceptionObject}");
            }
            catch
            {
            }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                if ((type == LogType.Exception || type == LogType.Error)
                    && stackTrace != null && stackTrace.Contains("SeaPowerFighterAI"))
                {
                    ModLog.Error("Unity-reported exception involving this mod", new Exception(condition + Environment.NewLine + stackTrace));
                }
            }
            catch
            {
            }
        }
    }
}
