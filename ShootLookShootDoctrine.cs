using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;

namespace SeaPowerFighterAI
{
    // Adds a "Shoot-Look-Shoot" toggle to the game's tactical doctrine (Global Standing Orders,
    // formation and per-unit tabs). The tab UI is generated from the doctrine item lists, so a
    // toggle added to the Aircraft list renders, inherits, overrides, saves and loads like vanilla items.
    public static class ShootLookShootDoctrine
    {
        public const string Key = "ShootLookShoot";
        public const string TooltipKey = "DoctrineShootLookShoot";
        public const string Label = "Shoot-Look-Shoot";
        public const string Tooltip = "Fighters wait for their own in-flight missile to be destroyed, miss or lose the target "
                                      + "before firing another missile at the same air contact.";

        private const bool DefaultValue = true;

        private static readonly ConditionalWeakTable<TacticalDoctrine, TacticalDoctrineToggleViewModel> Toggles
            = new ConditionalWeakTable<TacticalDoctrine, TacticalDoctrineToggleViewModel>();

        private static readonly Func<bool> AlwaysVisible = () => true;

        public static TacticalDoctrineToggleViewModel Get(TacticalDoctrine doctrine)
        {
            return doctrine != null && Toggles.TryGetValue(doctrine, out var toggle) ? toggle : null;
        }

        public static bool IsEnabledFor(ObjectBase unit)
        {
#if !DOCTRINE_UI
            // Testing mode: always enforce shoot-look-shoot, no UI toggle.
            return true;
#else
            TacticalDoctrineToggleViewModel toggle = Get(unit?.TacticalDoctrine);
            return toggle == null ? DefaultValue : toggle.ResolvedValue;
#endif
        }

        internal static void Create(TacticalDoctrine doctrine, bool isRoot, bool isPlayer)
        {
            bool initial = DefaultValue;
            if (isRoot && isPlayer)
            {
                initial = Singleton<OptionsManager>.Instance?.OptionsIni?.readValue("Tactics", Key, DefaultValue, false) ?? DefaultValue;
            }

            var isVisible = AccessTools.Field(typeof(TacticalDoctrineToggleViewModel), "_getIsVisible")
                ?.GetValue(doctrine.PlanesWinchester) as Func<bool> ?? AlwaysVisible;

            var toggle = new TacticalDoctrineToggleViewModel(Get(doctrine.ParentDoctrine), Key, TooltipKey, initial, isRoot, isVisible);
            Toggles.Remove(doctrine);
            Toggles.Add(doctrine, toggle);

            var settings = (Dictionary<string, ITacticalDoctrineItem>)AccessTools.Field(typeof(TacticalDoctrine), "_settings").GetValue(doctrine);
            settings[Key] = toggle;
            var aircraftSettings = (List<ITacticalDoctrineItem>)AccessTools.Field(typeof(TacticalDoctrine), "_aircraftSettings").GetValue(doctrine);
            aircraftSettings.Add(toggle);
        }
    }

#if DOCTRINE_UI
    [HarmonyPatch(typeof(TacticalDoctrine), "InitializeAircraftSettings")]
    public static class DoctrineInitPatch
    {
        private static void Postfix(TacticalDoctrine __instance, bool isRoot, bool isPlayer)
        {
            ShootLookShootDoctrine.Create(__instance, isRoot, isPlayer);
        }
    }

    // Vanilla relinks parents by reflecting a property named after each settings key; ours has no
    // such property, so restore the link afterwards.
    [HarmonyPatch(typeof(TacticalDoctrine), "UpdateParent")]
    public static class DoctrineUpdateParentPatch
    {
        private static void Postfix(TacticalDoctrine __instance)
        {
            TacticalDoctrineToggleViewModel toggle = ShootLookShootDoctrine.Get(__instance);
            if (toggle != null && __instance.ParentDoctrine != null)
            {
                toggle.ParentSetting = ShootLookShootDoctrine.Get(__instance.ParentDoctrine);
            }
        }
    }

    [HarmonyPatch(typeof(TacticalDoctrine), nameof(TacticalDoctrine.ResetToDefaults))]
    public static class DoctrineResetPatch
    {
        private static void Postfix(TacticalDoctrine __instance, bool __result)
        {
            TacticalDoctrineToggleViewModel toggle = ShootLookShootDoctrine.Get(__instance);
            if (__result && toggle != null && __instance.ParentDoctrine == null)
            {
                toggle.Value = true;
            }
        }
    }

    [HarmonyPatch(typeof(TacticalDoctrine), nameof(TacticalDoctrine.SaveGlobalDoctrineToOptions))]
    public static class DoctrineSaveGlobalPatch
    {
        private static void Postfix(TacticalDoctrine __instance, bool __result)
        {
            TacticalDoctrineToggleViewModel toggle = ShootLookShootDoctrine.Get(__instance);
            if (!__result || toggle == null)
            {
                return;
            }

            var optionsManager = Singleton<OptionsManager>.Instance;
            optionsManager.OptionsIni?.writeValue("Tactics", ShootLookShootDoctrine.Key, toggle.Value);
            optionsManager.WriteOptions();
        }
    }

    // Supplies label/tooltip text without needing a language file entry.
    [HarmonyPatch(typeof(LanguageResourceHandler), nameof(LanguageResourceHandler.getText))]
    public static class DoctrineTextPatch
    {
        private static bool Prefix(string section, string key, ref string __result)
        {
            if (section == "TacticalDoctrine" && key == ShootLookShootDoctrine.Key)
            {
                __result = ShootLookShootDoctrine.Label;
                return false;
            }

            if (section == "Tooltip" && key == ShootLookShootDoctrine.TooltipKey)
            {
                __result = ShootLookShootDoctrine.Tooltip;
                return false;
            }

            return true;
        }
    }
#endif
}
