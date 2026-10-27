using System;
using System.Collections.Generic;
using System.Reflection;
using FishNet;
using HarmonyLib;
using MelonLoader;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Services;
using Ships.Values;
using Ships.Visuals;
using StellarDriveMods.Shared;
using UnityEngine;
using UnityEngine.InputSystem;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using WorldTracking.Interface.Services;
using WorldTracking.Interface.Values;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // The hologram that follows the player's aim while a schematic is being placed.
    public sealed class SchematicPlacer
    {
        const float TurnStep = 11.25f;

        IShipState _ship;
        ShipPartVisuals _hologram;
        Vector3[] _corners = new Vector3[0];
        float _yaw;
        float _wheel;
        bool _hasPoint;
        Vector3 _point;
        Quaternion _rotation;
        string _error;

        public static bool Busy { get; private set; }

        public string Error => _error;

        public void Begin(IShipState ship, SchematicFile source)
        {
            Cancel();
            _ship = ship;
            _yaw = 0f;
            _wheel = 0f;
            _error = null;
            try
            {
                _hologram = CreateHologram(ship);
                if (_hologram != null)
                    Subships.Dress(_hologram, ship, source);
            }
            catch (Exception ex)
            {
                MelonLogger.Error(ex);
                if (_hologram != null)
                    Object.Destroy(_hologram.gameObject);
                _hologram = null;
            }
            if (_hologram == null)
            {
                _error = "Could not create the ship preview.";
                return;
            }
            _corners = LocalCorners(_hologram);
            Busy = true;
            GameplayLock.SetPlacing(true);
        }

        public void Cancel()
        {
            if (_hologram != null)
                Object.Destroy(_hologram.gameObject);
            _hologram = null;
            _ship = null;
            _corners = new Vector3[0];
            _hasPoint = false;
            Busy = false;
            GameplayLock.SetPlacing(false);
        }

        public void Tick(Camera camera)
        {
            _hasPoint = false;
            Busy = _ship != null;
            if (_ship == null || camera == null)
                return;

            if (GameKeys.Free())
                Turn();

            int mask = 1 << LayerMask.NameToLayer("Default");
            if (!ClientScene.PhysicsScene.Raycast(camera.transform.position, camera.transform.forward, out RaycastHit hit, 40f, mask, QueryTriggerInteraction.Ignore))
            {
                if (_hologram != null)
                    _hologram.gameObject.SetActive(false);
                return;
            }

            Vector3 up = hit.normal.sqrMagnitude > 0.001f ? hit.normal.normalized : Vector3.up;
            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, up);
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.ProjectOnPlane(camera.transform.right, up);
            _rotation = Quaternion.AngleAxis(_yaw, up) * Quaternion.LookRotation(forward.normalized, up);

            Vector3 lifted = hit.point + up * (0.2f - LowestPoint(_rotation, up));
            IWorldTransposeClient transpose = GameServices.Get<IWorldTransposeClient>();
            _point = transpose == null
                ? lifted
                : transpose.Position(ClientOrigin.LocalPhysicsSpace, ClientOrigin.LocalVisualSpace, lifted);
            _hasPoint = true;

            if (_hologram == null)
                return;
            _hologram.gameObject.SetActive(true);
            _hologram.transform.SetPositionAndRotation(_point, _rotation);
        }

        // One step per wheel notch, also for wheels that send a notch in several small scroll events.
        void Turn()
        {
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            if (mouse != null)
                _wheel += mouse.scroll.ReadValue().y / WheelTick();
            int steps = 0;
            while (_wheel >= 1f)
            {
                _wheel -= 1f;
                steps++;
            }
            while (_wheel <= -1f)
            {
                _wheel += 1f;
                steps--;
            }
            if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
                steps++;
            if (keyboard != null && keyboard.qKey.wasPressedThisFrame)
                steps--;
            _yaw += steps * TurnStep;
        }

        static float _wheelTick;

        // InputSystem.scrollWheelDeltaPerTick is internal; 120 is the Windows wheel delta.
        static float WheelTick()
        {
            if (_wheelTick > 0f)
                return _wheelTick;
            try
            {
                PropertyInfo property = typeof(InputSystem).GetProperty("scrollWheelDeltaPerTick", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (property != null)
                    _wheelTick = (float)property.GetValue(null, null);
            }
            catch (Exception)
            {
                _wheelTick = 0f;
            }
            if (_wheelTick <= 0f)
                _wheelTick = 120f;
            return _wheelTick;
        }

        public bool TryPlace(SchematicFile source, out string message)
        {
            message = null;
            if (!_hasPoint || _ship == null)
            {
                message = "Aim at the ground.";
                return false;
            }
            if (!CreativeWorlds.InstantBuild())
                return PlaceBlueprint(source, out message);

            if (!InstanceFinder.IsServerStarted)
                return AskHost(source, out message);

            bool placed = Materialize(source, _point, _rotation, out message);
            if (placed)
                Cancel();
            return placed;
        }

        bool AskHost(SchematicFile source, out string message)
        {
            uint space = ClientOrigin.LocalPhysicsSpaceId;
            if (!SchematicNet.Request(source, space, _point, _rotation, (ok, text) =>
                {
                    if (!string.IsNullOrEmpty(text))
                        ScreenMessages.Status(text);
                }))
            {
                message = "Could not reach the host.";
                return false;
            }
            Cancel();
            message = "The host is placing your ship.";
            return true;
        }

        public static bool Materialize(SchematicFile source, Vector3 point, Quaternion rotation, out string message)
        {
            if (!InstanceFinder.IsServerStarted)
            {
                message = "Could not reach the host.";
                return false;
            }
            return Subships.Place(source, ClientOrigin.LocalPhysicsSpaceId, point, rotation, out message, true);
        }

        // A blueprint is saved with the world relative to a planet or a fixed point in space; anywhere else it would
        // be lost on the next save.
        bool PlaceBlueprint(SchematicFile source, out string message)
        {
            ISpace space = ClientOrigin.LocalVisualSpace;
            if (!(space is PlanetSurfaceSpace) && !(space is FixedWorldSpace))
            {
                message = "A blueprint can only be placed on a planet or in open space.";
                return false;
            }
            ShipPartVisuals visuals = _hologram;
            IShipState ship = _ship;
            Vector3 point = _point;
            Quaternion rotation = _rotation;
            _hologram = null;
            Cancel();
            SchematicBuildSite site = SchematicBuildSite.Create(ship, source, space, point, rotation, visuals);
            message = site != null && site.HasWork ? "Blueprint placed. Look at it and press Interact to add materials." : null;
            return true;
        }

        internal static CablePlugSocket Plug(uint shipId, int partId, int socketId)
        {
            var plug = new CablePlugSocket();
            plug.IsHeldByPlayer = false;
            plug.EntityPartSocket = new EntityPartSocket(shipId, (ushort)partId, (byte)socketId);
            return plug;
        }

        float LowestPoint(Quaternion rotation, Vector3 up)
        {
            float lowest = 0f;
            bool any = false;
            foreach (Vector3 corner in _corners)
                Consider(rotation * corner, up, ref lowest, ref any);
            if (any)
                return lowest;
            if (_ship.HullParts != null)
            {
                foreach (var part in _ship.HullParts)
                    Consider(rotation * part.HullPlacement.LocalPosition, up, ref lowest, ref any);
            }
            if (_ship.StatefulParts != null)
            {
                foreach (var part in _ship.StatefulParts)
                    Consider(rotation * part.LocalPosition, up, ref lowest, ref any);
            }
            return any ? lowest : 0f;
        }

        // The corners of every renderer's bounds in the hologram's own frame, measured once per placement.
        static Vector3[] LocalCorners(ShipPartVisuals hologram)
        {
            var corners = new List<Vector3>();
            Transform root = hologram.transform;
            foreach (Renderer renderer in hologram.GetComponentsInChildren<Renderer>(true))
            {
                Bounds box = renderer.localBounds;
                for (int x = -1; x <= 1; x += 2)
                {
                    for (int y = -1; y <= 1; y += 2)
                    {
                        for (int z = -1; z <= 1; z += 2)
                        {
                            Vector3 corner = box.center + Vector3.Scale(box.extents, new Vector3(x, y, z));
                            corners.Add(root.InverseTransformPoint(renderer.transform.TransformPoint(corner)));
                        }
                    }
                }
            }
            return corners.ToArray();
        }

        static void Consider(Vector3 offset, Vector3 up, ref float lowest, ref bool any)
        {
            float height = Vector3.Dot(offset, up);
            if (!any || height < lowest)
            {
                lowest = height;
                any = true;
            }
        }

        public static ShipPartVisuals CreateHologram(IShipState ship)
        {
            ShipPartVisuals prefab = ShipVisualPrefab();
            if (ship == null || prefab == null)
                return null;

            ShipPartVisuals visuals = Object.Instantiate(prefab);
            visuals.name = "Schematic hologram";
            visuals.gameObject.SetActive(true);
            try
            {
                visuals.Setup(new SchematicShipView(ship));
                ShipHullVisuals hull = visuals.GetComponent<ShipHullVisuals>();
                if (ship.HullParts != null && hull != null)
                {
                    foreach (var part in ship.HullParts)
                        hull.AddHullPart(part);
                    hull.UpdateHull();
                }
                if (ship.StatefulParts != null)
                {
                    foreach (var part in ship.StatefulParts)
                        visuals.AddStatefulPart(part);
                }
            }
            catch (Exception)
            {
                Object.Destroy(visuals.gameObject);
                throw;
            }
            foreach (Collider collider in visuals.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            IShipHologramService holograms = GameServices.Get<IShipHologramService>();
            if (holograms != null)
            {
                try
                {
                    holograms.ChangeObjectStyleToHolographic(visuals.gameObject, true);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("The hologram outline skipped a mesh. " + ex.Message);
                }
            }
            return visuals;
        }

        static ShipPartVisuals _visualPrefab;
        static AccessTools.FieldRef<ShipRepositioningUpdater, ShipPrefabs> _prefabs;
        static bool _prefabsBound;

        // The ship visual prefab of the game's ship mover; any loaded ShipPrefabs asset when that field is gone.
        static ShipPartVisuals ShipVisualPrefab()
        {
            if (_visualPrefab != null)
                return _visualPrefab;
            if (!_prefabsBound)
            {
                _prefabsBound = true;
                try
                {
                    _prefabs = AccessTools.FieldRefAccess<ShipRepositioningUpdater, ShipPrefabs>("shipPrefabs");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("The ship mover has changed; looking for the ship prefabs elsewhere. " + ex.Message);
                }
            }
            ShipRepositioningUpdater updater = GameServices.Find<ShipRepositioningUpdater>();
            ShipPrefabs prefabs = updater != null && _prefabs != null ? _prefabs(updater) : null;
            if (prefabs == null)
            {
                foreach (ShipPrefabs candidate in Resources.FindObjectsOfTypeAll<ShipPrefabs>())
                {
                    if (candidate != null && candidate.partVisualPrefab != null)
                    {
                        prefabs = candidate;
                        break;
                    }
                }
            }
            _visualPrefab = prefabs == null ? null : prefabs.partVisualPrefab;
            return _visualPrefab;
        }
    }

    // A cable whose part is missing on its ship (left out because its mod is not installed) is skipped; the cable
    // tracker would throw.
    sealed class CableSpawner
    {
        readonly CablesServerTracker _cables = GameServices.Find<CablesServerTracker>();
        readonly ShipsServerTracker _ships = GameServices.Find<ShipsServerTracker>();

        public int Placed;
        public int Skipped;

        public void AddAll(uint shipId, CableRecord[] records)
        {
            if (records == null)
                return;
            foreach (CableRecord record in records)
            {
                if (record != null)
                    Add(record.type, shipId, record.aPart, record.aSocket, shipId, record.bPart, record.bSocket);
            }
        }

        public void Add(int type, uint aShip, int aPart, int aSocket, uint bShip, int bPart, int bSocket)
        {
            if (_cables == null || !HasPart(aShip, aPart) || !HasPart(bShip, bPart))
            {
                Skipped++;
                return;
            }
            try
            {
                _cables.AddCable(new CableState
                {
                    Type = (CableType)type,
                    PlugA = SchematicPlacer.Plug(aShip, aPart, aSocket),
                    PlugB = SchematicPlacer.Plug(bShip, bPart, bSocket)
                });
                Placed++;
            }
            catch (Exception ex)
            {
                Skipped++;
                MelonLogger.Warning("A connection was not placed. " + ex.Message);
            }
        }

        public void Report()
        {
            if (Placed == 0 && Skipped == 0)
                return;
            string text = "Placed " + Placed + " connection" + (Placed == 1 ? "" : "s") + ".";
            if (Skipped > 0)
                text += " Skipped " + Skipped + " whose parts are missing.";
            MelonLogger.Msg(text);
        }

        bool HasPart(uint shipId, int partId)
        {
            if (_ships == null)
                return true;
            TrackedShipServer ship = _ships.GetShip(shipId);
            return ship != null && ship.TryGetStatefulPart((ushort)partId, out _);
        }
    }

    // What ShipPartVisuals.Setup reads from a ship that exists only as a schematic.
    sealed class SchematicShipView : IShipStateRead
    {
        readonly IShipState _ship;

        public SchematicShipView(IShipState ship)
        {
            _ship = ship;
        }

        public uint Id => _ship.Id;
        public uint SpaceId => _ship.SpaceId;
        public Vector3 Position => _ship.Position;
        public Quaternion Rotation => _ship.Rotation;
        public float Mass => 0f;
        public uint RelativeSpaceId => _ship.RelativeSpaceId;
        public ShipPhysicsState PhysicsState => default;
        public ShipTargetState TargetState => default;
        public float HullMass => 0f;
        public float HullAndPartsMass => 0f;
        public float HullPartsAndPlayersMass => 0f;
        public Vector3 CenterOfMass => Vector3.zero;
        public int PartCount => _ship.StatefulParts == null ? 0 : _ship.StatefulParts.Length;
        public bool IsShipDestroyed => false;
        public IEnumerable<StatefulPart> StatefulParts => _ship.StatefulParts;

        public Ships.Grid.Model.ShipVoxel GetVoxel(Vector3Int coord) => null;

        public IEnumerable<StatefulPart> GetAllStatefulParts<T>() where T : Ships.Interface.Model.Parts.StateTypes.IStatefulPartState
        {
            yield break;
        }

        public bool TryGetStatefulPart(ushort partId, out StatefulPart part)
        {
            part = null;
            return false;
        }

        public bool IsApproximatelyInRoom(Vector3 shipRelativePos) => false;
        public Ships.Interface.Model.RoomSampleResult SampleRoomAtPosition(Vector3 shipRelativePos) => default;
        public bool HasFloorTile(Vector3 shipRelativePos) => false;

        public bool TryGetPartPlacement(ushort partId, out Ships.Interface.Model.Placement.IPartPlacement placement)
        {
            placement = null;
            return false;
        }

        public bool TryGetSocketType(PartSocket socket, out CableType socketType)
        {
            socketType = default;
            return false;
        }

        public float GetMassIncludingConnectedShips() => 0f;
    }
}
