using System;
using HarmonyLib;
using Players.Interface.Model;
using Players.LocalClient;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace StellarDriveMods.Shared
{
    // A mod key must never act while the keyboard belongs to something else: a text field (the chat, a name, a search),
    // the chat or pause menu, writing or reading on a part, or any menu that has unlocked the cursor.
    // Compiled into each mod from mods/Shared, so no mod depends on another mod's DLL.
    static class GameKeys
    {
        static AccessTools.FieldRef<PlayerControlsActivator, bool> _chat;
        static AccessTools.FieldRef<PlayerControlsActivator, bool> _pause;
        static AccessTools.FieldRef<PlayerControlsActivator, PlayerMainState> _state;
        static bool _bound;
        static PlayerControlsActivator _activator;
        static float _nextLookup;

        // The player is playing and nothing else takes the keys, so a mod key may open or do something.
        public static bool Free()
        {
            if (!Application.isFocused || Cursor.lockState != CursorLockMode.Locked)
                return false;
            return !Typing() && !GameUiOpen();
        }

        // A key that closes the mod's own window: the window holds the cursor, but typing and the chat still win.
        public static bool FreeInOwnWindow()
        {
            return Application.isFocused && !Typing() && !GameUiOpen();
        }

        // The game and these mods type only into uGUI fields. IMGUI keyboard focus is not checked, because it can stay
        // set after another tool's window is gone and would silence every mod key.
        public static bool Typing()
        {
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null)
                return false;
            TMP_InputField field = selected.GetComponentInParent<TMP_InputField>();
            if (field != null && field.isFocused)
                return true;
            InputField legacy = selected.GetComponentInParent<InputField>();
            return legacy != null && legacy.isFocused;
        }

        // The chat or pause flags of the game's control switcher, or a state where the player writes, reads or is dead.
        public static bool GameUiOpen()
        {
            PlayerStateType state;
            return ChatOrPause() || (TryGetState(out state) && (state == PlayerStateType.Writing || state == PlayerStateType.Reading || state == PlayerStateType.Dead));
        }

        public static bool TryGetState(out PlayerStateType state)
        {
            state = PlayerStateType.Unset;
            PlayerControlsActivator activator = Activator();
            if (activator == null || _state == null)
                return false;
            try
            {
                PlayerMainState main = _state(activator);
                if (main == null)
                    return false;
                state = main.CurrentStateType;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool ChatOrPause()
        {
            PlayerControlsActivator activator = Activator();
            if (activator == null)
                return false;
            try
            {
                return (_chat != null && _chat(activator)) || (_pause != null && _pause(activator));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static PlayerControlsActivator Activator()
        {
            Bind();
            if (_activator != null)
                return _activator;
            if (Time.unscaledTime < _nextLookup)
                return null;
            _nextLookup = Time.unscaledTime + 1f;
            _activator = Object.FindAnyObjectByType<PlayerControlsActivator>();
            return _activator;
        }

        static void Bind()
        {
            if (_bound)
                return;
            _bound = true;
            _chat = Field<bool>("_chatIsActive");
            _pause = Field<bool>("_pauseIsActive");
            _state = Field<PlayerMainState>("_state");
        }

        static AccessTools.FieldRef<PlayerControlsActivator, T> Field<T>(string name)
        {
            try
            {
                return AccessTools.FieldRefAccess<PlayerControlsActivator, T>(name);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
