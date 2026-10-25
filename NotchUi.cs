using System;
using System.Collections.Generic;
using HarmonyLib;
using MapControl;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPowerFighterAI
{
    // Click-to-pick state, modelled on the game's AerialRefuelState: right-click an emitter to notch it, middle-click
    // cancels, and the cursor shows "unavailable" over anything that cannot be notched.
    public class NotchState : InteractionState
    {
        private readonly InteractionStateManager _manager;
        private readonly Aircraft _source;

        public NotchState(InteractionStateManager manager, Aircraft source)
        {
            CursorName = "default";
            _manager = manager;
            _source = source;
        }

        public override Noesis.HitTestResultBehavior MouseAction(object v, Noesis.RoutedEventArgs args, GeoPosition mousePosition, MapBase map, ref bool handled)
        {
            if (args is Noesis.MouseButtonEventArgs e)
            {
                if (e.RightButton == Noesis.MouseButtonState.Pressed)
                {
                    ObjectBase target = Pick(v);
                    if (target != null && NotchGuidance.EmitterHalfWidthKt(target) > 0f)
                    {
                        NotchGuidance.Start(_source, target);
                    }
                    else
                    {
                        _source?.ShowOrderFeedback("Cannot notch: not a radar emitter");
                    }

                    _manager.PopState<NotchState>();
                    handled = true;
                    return Noesis.HitTestResultBehavior.Stop;
                }

                if (e.MiddleButton == Noesis.MouseButtonState.Pressed)
                {
                    _manager.PopState<NotchState>();
                    handled = true;
                    return Noesis.HitTestResultBehavior.Stop;
                }
            }
            else
            {
                CursorName = NotchGuidance.EmitterHalfWidthKt(Pick(v)) > 0f ? "identify" : "unavailable";
            }

            return base.MouseAction(v, args, mousePosition, map, ref handled);
        }

        private static ObjectBase Pick(object item) => (item as MapUnitViewModel)?.Unit?.BaseObject;

        // Shared by the menu and bottom-bar entries. Pressing it again while active cancels the maneuver.
        public static void Begin(ObjectBase obj)
        {
            if (!(obj is Aircraft ac) || ac.IsDestroyed)
            {
                return;
            }

            if (NotchGuidance.IsActive(ac))
            {
                NotchGuidance.Stop(ac, "cancelled by player");
                return;
            }

            InteractionStateManager mgr = Globals._mainGameViewModel.StateManager;
            mgr.PushSingleState(new NotchState(mgr, ac));
        }
    }

    // Map right-click menu: append "Notch" to the unit's Orders submenu.
    [HarmonyPatch(typeof(ObjectBaseViewModel), "GetContextMenuItems")]
    public static class NotchMenuPatch
    {
        private static void Postfix(ObjectBaseViewModel __instance, ObjectBase ____objectBase, TrulyObservableCollection<ContextMenuItem> __result)
        {
            try
            {
                if (!(____objectBase is Aircraft ac) || !(ac.IsPlayerObject || Globals.AllowControlOfEnemyUnits.Value) || __result == null)
                {
                    return;
                }

                string ordersLabel = Singleton<LanguageResourceHandler>.Instance.getText("ContextMenu", "Orders");
                foreach (ContextMenuItem item in __result)
                {
                    if (item != null && item.Items != null && string.Equals(item.Label as string, ordersLabel, StringComparison.Ordinal))
                    {
                        item.Items.Add(new ContextMenuItem("Notch", null, new DelegateCommand((object _) => NotchState.Begin(ac))));
                        return;
                    }
                }

                ModLog.Warn("NotchMenuPatch: Orders submenu not found");
            }
            catch (Exception ex)
            {
                ModLog.Error("NotchMenuPatch", ex);
            }
        }
    }

    // Bottom bar: add a blank WeaponEntry (type Generic leaves the constructor empty) and fill it in ourselves.
    [HarmonyPatch(typeof(ObjectEffectorsViewModel), MethodType.Constructor, new[] { typeof(ObjectBase), typeof(bool) })]
    public static class NotchBottomBarPatch
    {
        private static void Postfix(ObjectEffectorsViewModel __instance, ObjectBase objectBase, bool isWaypointEdit)
        {
            try
            {
                if (isWaypointEdit || !(objectBase is Aircraft ac) || !(ac.IsPlayerObject || Globals.AllowControlOfEnemyUnits.Value))
                {
                    return;
                }

                var entry = new WeaponEntry(objectBase, BaseSystem.SystemType.Generic)
                {
                    Label = "NOTCH",
                    Command = new DelegateCommand((object _) => NotchState.Begin(ac))
                };
                entry.AmmoAmount = 1;
                __instance.WeaponEntries.Add(entry);
            }
            catch (Exception ex)
            {
                ModLog.Error("NotchBottomBarPatch", ex);
            }
        }
    }
}
