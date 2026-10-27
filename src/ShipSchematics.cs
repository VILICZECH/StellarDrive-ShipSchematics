using System;
using FishNet;
using MelonLoader;
using Players.Interface.Services;
using Players.Interface.Values;
using Ships;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using StellarDriveMods.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Spaces;

[assembly: MelonInfo(typeof(ShipSchematics.ShipSchematicsMod), "Ship Schematics", "1.0.3", "VILIK")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace ShipSchematics
{
    public class ShipSchematicsMod : MelonMod
    {
        const string PlacementHint = "Scroll or Q/E to rotate, left click to place, right click or Esc to cancel";

        enum Mode
        {
            None,
            Prompt,
            Library,
            Placing
        }

        readonly SchematicPlacer _placer = new SchematicPlacer();
        readonly SaveHoldHint _holdHint = new SaveHoldHint();
        Mode _mode;
        float _pTime;
        bool _pTracking;
        bool _saveOpened;
        TrackedShip _pShip;
        CaptureResult _pending;
        SchematicFile _placingFile;
        SchematicScreen _screen;

        public override void OnInitializeMelon()
        {
            EscapeCancelsPlacement.CancelPlacement = CancelPlacement;
            LoggerInstance.Msg("Ship Schematics: hold P to save the ship, tap P to open the collection.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            CancelPlacement();
            _pTracking = false;
            _pShip = null;
            _pending = null;
            if (_screen == null)
                _mode = Mode.None;
            PartCatalog.SceneLoaded();
            ModMatch.Reset();
            ScreenKit.RememberNameField();
            BlueprintWorld.SceneLoaded();
        }

        public override void OnUpdate()
        {
            BlueprintWorld.Tick();
            ModMatch.Tick();
            SchematicNet.Tick();
            ScreenMessages.Tick();
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            // The game's Escape callback normally runs first and already closed a menu or cancelled the placement.
            bool escape = keyboard.escapeKey.wasPressedThisFrame && EscapeCancelsPlacement.SeenFrame != Time.frameCount;
            if (escape && _screen != null)
            {
                _screen.Back();
                EscapeCancelsPlacement.ConsumedFrame = Time.frameCount;
            }
            else if (escape && _mode == Mode.Placing && !GameKeys.Typing() && !GameKeys.GameUiOpen())
            {
                CancelPlacement();
                EscapeCancelsPlacement.ConsumedFrame = Time.frameCount;
            }

            if (_mode == Mode.Placing)
                TickPlacement();
            else
                TrackPress(keyboard);

            float duration = HoldDuration();
            bool charging = _mode == Mode.None && _pTracking && keyboard.pKey.isPressed && HeldShip() != null;
            _holdHint.Tick(charging, duration <= 0f ? 0f : Mathf.Clamp01(_pTime / duration));
        }

        public override void OnDeinitializeMelon()
        {
            CancelPlacement();
            _holdHint.Destroy();
            if (_screen != null)
                _screen.Close();
        }

        void TickPlacement()
        {
            _placer.Tick(ActiveCamera());
            Mouse mouse = GameKeys.Free() ? Mouse.current : null;
            if (mouse != null && mouse.rightButton.wasPressedThisFrame)
            {
                CancelPlacement();
            }
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                bool placed = _placer.TryPlace(_placingFile, out string message);
                if (placed)
                {
                    _placingFile = null;
                    _mode = Mode.None;
                    ScreenMessages.Hint(null);
                }
                if (!string.IsNullOrEmpty(message))
                    Status(message);
            }
        }

        void CancelPlacement()
        {
            _placer.Cancel();
            _placingFile = null;
            ScreenMessages.Hint(null);
            if (_mode == Mode.Placing)
                _mode = Mode.None;
        }

        void TrackPress(Keyboard keyboard)
        {
            // A window that failed to open, or closed without telling the mod, must not leave P dead.
            if ((_mode == Mode.Prompt || _mode == Mode.Library) && _screen == null)
            {
                _mode = Mode.None;
                _pending = null;
            }
            if (_mode != Mode.None)
            {
                if (_mode == Mode.Library && keyboard.pKey.wasPressedThisFrame && _screen != null && !_screen.IsTyping && GameKeys.FreeInOwnWindow())
                    _screen.Close();
                return;
            }

            if (!GameKeys.Free())
            {
                _pTracking = false;
                return;
            }

            if (keyboard.pKey.wasPressedThisFrame)
            {
                _pTracking = true;
                _pTime = 0f;
                _saveOpened = false;
                _pShip = CurrentShip();
            }
            if (!_pTracking)
                return;

            if (keyboard.pKey.isPressed)
            {
                _pTime += Time.deltaTime;
                if (!_saveOpened && _pTime >= HoldDuration() && HeldShip() != null)
                {
                    _saveOpened = true;
                    OpenSaveWindow(HeldShip());
                }
            }

            if (keyboard.pKey.wasReleasedThisFrame)
            {
                bool tap = _pTime < HoldDuration();
                _pTracking = false;
                _pShip = null;
                if (!_saveOpened && tap)
                    OpenLibrary();
            }
        }

        TrackedShip HeldShip()
        {
            return _pShip != null && _pShip.State != null ? _pShip : null;
        }

        void OpenSaveWindow(TrackedShip ship)
        {
            try
            {
                CaptureResult result = SchematicLibrary.Capture(ship);
                if (result == null)
                {
                    Status("This ship has nothing to save.");
                    return;
                }
                string suggested = result.Match != null ? result.Match.name : SchematicLibrary.SuggestName();
                IShipState state = SchematicLibrary.Restore(result.File);
                _pending = result;
                _screen = SchematicScreen.OpenSave(state, result.File, suggested, result.Match != null, HandleScreenClosed, Commit);
                if (_screen != null)
                {
                    _mode = Mode.Prompt;
                    return;
                }
                _pending = null;
                Status("Could not open the window.");
            }
            catch (Exception ex)
            {
                _pending = null;
                _screen = null;
                _mode = Mode.None;
                LoggerInstance.Error(ex);
                Status("Could not prepare the ship.");
            }
        }

        void Commit(string name, byte[] png, bool asNew)
        {
            if (_pending == null)
                return;
            if (!string.IsNullOrEmpty(name))
                _pending.File.name = name.Trim();
            bool saved;
            if (_pending.Match == null || asNew)
            {
                saved = SchematicLibrary.SaveNew(_pending.File);
                Status(saved ? "Saved as " + _pending.File.name + "." : "Could not save the ship.");
            }
            else
            {
                saved = SchematicLibrary.Overwrite(_pending.Match, _pending.File);
                Status(saved ? "Overwritten: " + _pending.File.name + "." : "Could not save the ship.");
            }
            if (saved)
                SchematicLibrary.SavePreview(_pending.File, png);
            _pending = null;
            if (_screen != null)
                _screen.Close();
        }

        void OpenLibrary()
        {
            try
            {
                SchematicLibrary.EnsureLoaded();
                _screen = SchematicScreen.OpenLibrary(HandleScreenClosed, StartPlacement, DeleteFromMenu, Status);
            }
            catch (Exception ex)
            {
                _screen = null;
                LoggerInstance.Error(ex);
            }
            if (_screen != null)
                _mode = Mode.Library;
            else
                Status("Could not open the menu.");
        }

        void DeleteFromMenu(SchematicFile file)
        {
            string name = file.name;
            Status(SchematicLibrary.Delete(file) ? "Deleted: " + name + "." : "Could not delete " + name + ".");
        }

        void HandleScreenClosed()
        {
            _screen = null;
            if (_mode == Mode.Placing)
                return;
            _pending = null;
            _mode = Mode.None;
        }

        void StartPlacement(SchematicFile file)
        {
            try
            {
                PartCatalog survey = SchematicLibrary.Survey(file);
                IShipState ship = SchematicLibrary.Restore(file, survey);
                _placer.Begin(ship, file);
                if (_placer.Error != null)
                {
                    _placer.Cancel();
                    Status(_placer.Error);
                    return;
                }
                _placingFile = file;
                _mode = Mode.Placing;
                ScreenMessages.Hint(PlacementHint);
                if (_screen != null)
                    _screen.Close();
                if (survey.HasMissing)
                    Status("Parts that are not installed will be left out: " + survey.MissingText() + ".");
            }
            catch (Exception ex)
            {
                CancelPlacement();
                LoggerInstance.Error(ex);
                Status("Could not load the schematic.");
            }
        }

        static float HoldDuration()
        {
            float duration = PlayerConstants.RepositionShipHoldKeyTime;
            return duration < 0.25f ? 0.45f : duration;
        }

        static TrackedShip CurrentShip()
        {
            IPlayersProviderClient players = GameServices.Get<IPlayersProviderClient>();
            IShipsClientProvider ships = GameServices.Get<IShipsClientProvider>();
            if (players == null || ships == null || players.LocalPlayerTransform == null)
                return null;

            SpacesClientTracker spaces = GameServices.Get<SpacesClientTracker>();
            if (spaces != null && spaces.TryGetSpace(players.LocalPlayerTransform.SpaceId, out var space) && space is ShipRelativeSpace aboard
                && ships.TryGetShip(aboard.ShipId, out IShipStateReadClient ship) && ship is TrackedShip tracked)
                return tracked;

            if (!ships.TryGetNearestRepositionableShip(players.LocalPlayerTransform.SpaceId, players.LocalPlayerTransform.Position, out IShipStateRead nearest))
                return null;
            return nearest as TrackedShip;
        }

        static Camera ActiveCamera()
        {
            if (Camera.main != null)
                return Camera.main;
            Camera[] cameras = Camera.allCameras;
            return cameras.Length == 0 ? null : cameras[0];
        }

        void Status(string message)
        {
            LoggerInstance.Msg(message);
            ScreenMessages.Status(message);
        }
    }
}
