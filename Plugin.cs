using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeaPowerFighterAI
{
    // BepInPlugin tells BepInEx that this class is a mod plugin.
    // Parameters: Unique GUID, Display Name, Version Number
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.user.seapower.fighterai";
        public const string PLUGIN_NAME = "Sea Power Fighter AI Overhaul";
        public const string PLUGIN_VERSION = "0.1.0";

        // Internal logger so our patches can print debug info to the BepInEx console/log
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
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
