using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using Players.Interface.Model;
using Players.LocalClient;
using Players.LocalClient.Controls;
using StellarDriveMods.Shared;
using UI.BuildMenu;
using UI.Interface.Services;
using UnityEngine;

namespace ShipSchematics
{
    // While a schematic window is open, the game gets no keys at all except Escape, the same as in its pause menu,
    // so typing a name or a search opens no game menu, chat or guide. While a ship is being placed, the tool in hand
    // stays still: the clicks, the scroll wheel and the number keys belong to the placement. The blueprint material
    // window only holds the mouse look, because the game's inventory works next to it.
    static class GameplayLock
    {
        static AccessTools.FieldRef<PlayerControlsActivator, List<MonoBehaviour>> _allScripts;
        static Action<PlayerControlsActivator> _reload;
        static bool _bound;

        static bool _window;
        static bool _placing;
        static bool _look;

        public static bool Active => _window || _placing || _look;

        public static void SetWindow(bool open)
        {
            if (_window == open)
                return;
            _window = open;
            Changed();
        }

        public static void SetPlacing(bool placing)
        {
            if (_placing == placing)
                return;
            _placing = placing;
            Changed();
        }

        public static void SetLook(bool held)
        {
            if (_look == held)
                return;
            _look = held;
            Changed();
        }

        public static void Apply()
        {
            if (!Active)
                return;
            List<MonoBehaviour> scripts = Scripts();
            if (scripts == null)
                return;
            for (int i = 0; i < scripts.Count; i++)
            {
                MonoBehaviour script = scripts[i];
                if (script != null && script.enabled && Holds(script))
                    script.enabled = false;
            }
        }

        static bool Holds(MonoBehaviour script)
        {
            if (_window)
                return !(script is PlayerEscapeControl);
            if (_look && script is PlayerLookControls)
                return true;
            return _placing && (script is PlayerPrimaryActionControl
                || script is PlayerSecondaryActionControl
                || script is PlayerTertiaryControl
                || script is PlayerScrollControl
                || script is PlayerToolSelectionControls);
        }

        // The game decides again which controls its own menus, chat and player state allow; the postfix below then
        // takes back what a lock holds.
        static void Changed()
        {
            Bind();
            PlayerControlsActivator activator = GameKeys.Activator();
            if (activator != null && _reload != null)
            {
                try
                {
                    _reload(activator);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Could not give the controls back to the game. " + ex.Message);
                }
            }
            Apply();
        }

        static List<MonoBehaviour> Scripts()
        {
            Bind();
            PlayerControlsActivator activator = GameKeys.Activator();
            if (activator == null || _allScripts == null)
                return null;
            try
            {
                return _allScripts(activator);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void Bind()
        {
            if (_bound)
                return;
            _bound = true;
            try
            {
                _allScripts = AccessTools.FieldRefAccess<PlayerControlsActivator, List<MonoBehaviour>>("_allScripts");
                _reload = AccessTools.MethodDelegate<Action<PlayerControlsActivator>>(AccessTools.Method(typeof(PlayerControlsActivator), "ReloadEnabledScripts"));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("The game's control switcher has changed; schematic windows cannot hold the game's keys. " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerControlsActivator), "ReloadEnabledScripts")]
    static class GameplayLockPatch
    {
        static bool Prepare()
        {
            return AccessTools.Method(typeof(PlayerControlsActivator), "ReloadEnabledScripts") != null;
        }

        static void Postfix()
        {
            try
            {
                GameplayLock.Apply();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not hold the game's keys. " + ex.Message);
            }
        }
    }

    // Q and E switch build menu tabs through a game event, outside the control switcher. The schematic window is a
    // build menu without its tabs, so typing those letters there must not reach the tab code.
    [HarmonyPatch(typeof(BuildMenu), nameof(BuildMenu.OnSwitchTabDirection))]
    static class SchematicWindowKeepsTab
    {
        static bool Prefix(BuildMenu __instance)
        {
            return __instance == null || __instance.GetComponent<SchematicScreen>() == null;
        }
    }

    // Escape closes the rename dialog, cancels a placement or closes a schematic window. When nothing else would use
    // it, the game toggles its pause menu, so that one press must not also open the pause menu.
    [HarmonyPatch(typeof(PlayerEscapeControl), nameof(PlayerEscapeControl.OnEscape))]
    static class EscapeCancelsPlacement
    {
        // Set when the mod used this frame's Escape before the game's input callback ran.
        public static int ConsumedFrame = -1;
        // Set when the game's Escape callback ran this frame, so the mod's own Update leaves that press alone.
        public static int SeenFrame = -1;
        public static Action CancelPlacement;

        static bool Prefix(PlayerEscapeControl __instance, UnityEngine.InputSystem.InputAction.CallbackContext context)
        {
            try
            {
                if (__instance == null || !__instance.isActiveAndEnabled || !context.performed)
                    return true;
                SeenFrame = Time.frameCount;
                if (SchematicScreen.CloseDialog())
                {
                    ConsumedFrame = Time.frameCount;
                    return false;
                }
                bool placing = SchematicPlacer.Busy;
                if (!placing && ConsumedFrame != Time.frameCount)
                    return true;
                if (GameKeys.TryGetState(out PlayerStateType state) && WouldUseEscape(state))
                    return true;
                IClosableMenuService menus = GameServices.Get<IClosableMenuService>();
                if (menus != null && menus.HasClosableMenuOpen())
                    return true;
                if (placing && CancelPlacement != null)
                    CancelPlacement();
                ConsumedFrame = Time.frameCount;
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not handle Escape. " + ex.Message);
                return true;
            }
        }

        static bool WouldUseEscape(PlayerStateType state)
        {
            return state == PlayerStateType.InteractingWithChest
                || state == PlayerStateType.Writing
                || state == PlayerStateType.WalkingRepositioningShip
                || state == PlayerStateType.WalkingHoldingCable
                || state == PlayerStateType.CheatFlyingHoldingCable;
        }
    }
}
