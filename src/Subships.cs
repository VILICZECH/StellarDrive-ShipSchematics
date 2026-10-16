using System;
using System.Collections.Generic;
using FishNet;
using Items.Model;
using MelonLoader;
using Saving.Serialization.V15.Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Joints;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Network.Service;
using Ships.Services;
using Ships.Visuals;
using UnityEngine;
using WorldTracking.Interface.Services;
using WorldTracking.Interface.Values;

namespace ShipSchematics
{
    static class Subships
    {
        const string SubName = "Schematic sub-ship";

        public static void Dress(ShipPartVisuals rootVisual, IShipState root, SchematicFile file)
        {
            if (rootVisual == null || root == null || file == null || file.childShipJson == null || file.childShipJson.Length == 0)
                return;
            if (rootVisual.transform.Find(SubName) != null)
                return;
            ShipTree tree = ShipTree.Read(root, file, PartCatalog.For(file));
            for (int i = 1; i < tree.Ships.Length; i++)
            {
                if (tree.Ships[i] == null || !tree.IsPosed(i))
                    continue;
                ShipPartVisuals visual = SchematicPlacer.CreateHologram(tree.Ships[i]);
                if (visual == null)
                    continue;
                visual.name = SubName;
                (Vector3 position, Quaternion rotation) = tree.PoseOf(i);
                visual.transform.SetParent(rootVisual.transform, false);
                visual.transform.localPosition = position;
                visual.transform.localRotation = rotation;
            }
        }

        public static void VisitParts(IShipState root, SchematicFile file, Action<PartSettings, Vector3> visit)
        {
            if (root == null || visit == null)
                return;
            ShipTree tree = ShipTree.Read(root, file, PartCatalog.For(file));
            for (int i = 0; i < tree.Ships.Length; i++)
            {
                IShipState ship = tree.Ships[i];
                if (ship == null || !tree.IsPosed(i))
                    continue;
                (Vector3 position, Quaternion rotation) = tree.PoseOf(i);
                if (ship.HullParts != null)
                {
                    foreach (HullPart part in ship.HullParts)
                        visit(part.Settings, position + rotation * part.HullPlacement.LocalPosition);
                }
                if (ship.StatefulParts == null)
                    continue;
                foreach (StatefulPart part in ship.StatefulParts)
                {
                    if (part != null)
                        visit(part.Settings, position + rotation * part.LocalPosition);
                }
            }
        }

        // Builds every ship of the schematic before the first one is loaded, so a broken file leaves nothing behind.
        // Once the root ship is in the world, a sub-ship that fails is skipped and the placement still counts.
        public static bool Place(SchematicFile file, Vector3 point, Quaternion rotation, out string message)
        {
            message = "Could not place the ship.";
            if (file == null || string.IsNullOrEmpty(file.shipJson))
                return false;
            if (!InstanceFinder.IsServerStarted)
            {
                message = "Only the host can place a ship.";
                return false;
            }
            var ids = GameServices.Get<IWorldObjectsIdsServerProvider>();
            var spaces = GameServices.Get<ISpacesServerProvider>();
            var ships = GameServices.Get<ShipsServerTracker>();
            if (ids == null || spaces == null || ships == null)
            {
                MelonLogger.Warning("The game's ship services were not found, so the ship cannot be placed.");
                return false;
            }

            PartCatalog catalog = PartCatalog.For(file);
            int count = 1 + (file.childShipJson == null ? 0 : file.childShipJson.Length);
            var serials = new SerializableShip[count];
            var cables = new CableRecord[count][];
            var ready = new IShipState[count];
            var newIds = new uint[count];
            try
            {
                serials[0] = SchematicLibrary.ReadSerializable(file.shipJson, catalog);
                if (serials[0] == null)
                    return false;
                cables[0] = SchematicLibrary.PreferCables(file.cables, file.cableData);
                for (int i = 1; i < count; i++)
                {
                    string json = file.childShipJson[i - 1];
                    if (string.IsNullOrEmpty(json))
                        continue;
                    try
                    {
                        serials[i] = SchematicLibrary.ReadSerializable(json, catalog);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning("Could not read a sub-ship. " + ex.Message);
                        continue;
                    }
                    string text = file.childCableData != null && i - 1 < file.childCableData.Length ? file.childCableData[i - 1] : "";
                    cables[i] = SchematicLibrary.PreferCables(null, text);
                }

                var lives = new IShipState[count];
                int parts = 0;
                for (int i = 0; i < count; i++)
                {
                    if (serials[i] == null)
                        continue;
                    lives[i] = SchematicLibrary.ToLive(serials[i]);
                    parts += (lives[i].HullParts == null ? 0 : lives[i].HullParts.Length) + (lives[i].StatefulParts == null ? 0 : lives[i].StatefulParts.Length);
                }
                if (parts == 0)
                {
                    message = "None of the parts of this ship are installed.";
                    return false;
                }
                ShipTree tree = ShipTree.From(lives, file.childPoses);
                int leftOut = 0;
                for (int i = 1; i < count; i++)
                {
                    if (serials[i] == null || tree.IsPosed(i))
                        continue;
                    serials[i] = null;
                    leftOut++;
                }
                if (leftOut > 0)
                    MelonLogger.Warning("Left out " + leftOut + " sub-ship" + (leftOut == 1 ? "" : "s") + " that no part of this ship holds any more.");

                var map = new Dictionary<uint, uint>();
                for (int i = 0; i < count; i++)
                {
                    if (serials[i] == null)
                        continue;
                    newIds[i] = ids.CreateUniqueIdForObject();
                    uint oldId = serials[i].id;
                    if (oldId != 0 && !map.ContainsKey(oldId))
                        map[oldId] = newIds[i];
                }
                for (int i = 0; i < count; i++)
                {
                    SerializableShip serial = serials[i];
                    if (serial == null)
                        continue;
                    (Vector3 offset, Quaternion turn) = tree.PoseOf(i);
                    serial.id = newIds[i];
                    SchematicLibrary.RetargetLinks(serial, id => map.TryGetValue(id, out uint target) ? target : 0u);
                    serial.relativeSpaceId = spaces.GetOrCreateShipRelativeSpace(serial.id);
                    serial.spaceId = ClientOrigin.LocalPhysicsSpaceId;
                    serial.position = point + rotation * offset;
                    serial.rotation = rotation * turn;
                    serial.velocity = Vector3.zero;
                    ready[i] = SchematicLibrary.ToLive(serial);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(ex);
                return false;
            }

            ShipInteractionNotifier notifier = GameServices.Get<ShipInteractionNotifier>();
            var placed = new List<(uint ship, uint space)>();
            int skipped = 0;
            for (int i = 0; i < count; i++)
            {
                if (ready[i] == null)
                    continue;
                try
                {
                    ships.LoadShip(ready[i]);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error(ex);
                    if (i == 0)
                        return false;
                    serials[i] = null;
                    skipped++;
                    continue;
                }
                placed.Add((newIds[i], serials[i].relativeSpaceId));
                if (notifier == null)
                    continue;
                try
                {
                    notifier.NotifyShipCreation(serials[i].spaceId, serials[i].position, FishNet.Transporting.Channel.Reliable);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Could not tell the players about the new ship. " + ex.Message);
                }
            }
            SchematicLibrary.ClaimShips(file, placed);

            var spawner = new CableSpawner();
            for (int i = 0; i < count; i++)
            {
                if (serials[i] != null)
                    spawner.AddAll(newIds[i], cables[i]);
            }
            foreach (LinkRecord link in SchematicLibrary.ParseLinks(file.linkData))
            {
                bool known = link.aShip >= 0 && link.aShip < count && link.bShip >= 0 && link.bShip < count
                    && serials[link.aShip] != null && serials[link.bShip] != null;
                if (known)
                    spawner.Add(link.type, newIds[link.aShip], link.aPart, link.aSocket, newIds[link.bShip], link.bPart, link.bSocket);
                else
                    spawner.Skipped++;
            }
            spawner.Report();

            int children = placed.Count - 1;
            message = children <= 0
                ? "Ship placed."
                : "Ship placed with " + children + " sub-ship" + (children == 1 ? "" : "s") + ".";
            if (skipped > 0)
                message += " " + skipped + " sub-ship" + (skipped == 1 ? "" : "s") + " could not be built.";
            if (catalog.HasMissing)
                message += " Left out parts that are not installed: " + catalog.MissingText() + ".";
            MelonLogger.Msg(message);
            return true;
        }

    }

    // The ships of one schematic: index 0 is shipJson, index i + 1 is childShipJson[i] (null when it could not be read).
    // Poses are relative to the root ship. They come from the schematic when it kept them; older schematics get them
    // from the same joint math the game uses for rotors, so a mod that moves the head of its own part moves it here too.
    sealed class ShipTree
    {
        public IShipState[] Ships;
        readonly (Vector3, Quaternion)[] _poses;
        readonly bool[] _posed;

        ShipTree(IShipState[] ships)
        {
            Ships = ships;
            _poses = new (Vector3, Quaternion)[ships.Length];
            _posed = new bool[ships.Length];
        }

        public static ShipTree Read(IShipState root, SchematicFile file, PartCatalog catalog)
        {
            int count = 1 + (file == null || file.childShipJson == null ? 0 : file.childShipJson.Length);
            var ships = new IShipState[count];
            ships[0] = root;
            for (int i = 1; i < count; i++)
            {
                string json = file.childShipJson[i - 1];
                if (string.IsNullOrEmpty(json))
                    continue;
                try
                {
                    ships[i] = SchematicLibrary.FromShipJson(json, catalog);
                }
                catch (Exception ex)
                {
                    MelonLoader.MelonLogger.Warning("Could not read a sub-ship. " + ex.Message);
                }
            }
            return From(ships, file == null ? null : file.childPoses);
        }

        public static ShipTree From(IShipState[] ships, string[] childPoses)
        {
            var tree = new ShipTree(ships);
            var byId = new Dictionary<uint, int>();
            for (int i = 0; i < ships.Length; i++)
            {
                if (ships[i] == null || (i > 0 && ships[i].Id == 0) || byId.ContainsKey(ships[i].Id))
                    continue;
                byId[ships[i].Id] = i;
            }
            bool[] held = Held(ships, byId);
            tree._poses[0] = (Vector3.zero, Quaternion.identity);
            tree._posed[0] = true;
            for (int i = 1; i < ships.Length; i++)
            {
                if (!held[i] || childPoses == null || i - 1 >= childPoses.Length)
                    continue;
                if (SchematicLibrary.TryParsePose(childPoses[i - 1], out Vector3 position, out Quaternion rotation))
                {
                    tree._poses[i] = (position, rotation);
                    tree._posed[i] = true;
                }
            }

            // Walks rotor joints in both directions: a schematic saved before 1.0.2 while standing on a sub-ship has that
            // sub-ship as its root.
            var provider = new TreeShips(ships);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int host = 0; host < ships.Length; host++)
                {
                    IShipState ship = ships[host];
                    if (ship == null || ship.StatefulParts == null || !held[host])
                        continue;
                    foreach (StatefulPart part in ship.StatefulParts)
                    {
                        if (part == null || !(part.State is RotorState rotor) || rotor.AnchoredShipId == 0)
                            continue;
                        if (!byId.TryGetValue(rotor.AnchoredShipId, out int child) || child == host || tree._posed[host] == tree._posed[child])
                            continue;
                        (Vector3 position, Quaternion rotation) = Joint(provider, ship.Id, part, rotor);
                        if (tree._posed[host])
                        {
                            (Vector3 hostPosition, Quaternion hostRotation) = tree._poses[host];
                            tree._poses[child] = (hostPosition + hostRotation * position, hostRotation * rotation);
                            tree._posed[child] = true;
                        }
                        else
                        {
                            Quaternion back = Quaternion.Inverse(rotation);
                            (Vector3 childPosition, Quaternion childRotation) = tree._poses[child];
                            tree._poses[host] = (childPosition + childRotation * (back * -position), childRotation * back);
                            tree._posed[host] = true;
                        }
                        changed = true;
                    }
                }
            }
            return tree;
        }

        // Ships reachable from the root through rotor, piston or docking links in either direction.
        static bool[] Held(IShipState[] ships, Dictionary<uint, int> byId)
        {
            var neighbours = new List<int>[ships.Length];
            for (int host = 0; host < ships.Length; host++)
            {
                IShipState ship = ships[host];
                if (ship == null || ship.StatefulParts == null)
                    continue;
                foreach (StatefulPart part in ship.StatefulParts)
                {
                    uint target = LinkTarget(part, ship.Id);
                    if (target == 0 || !byId.TryGetValue(target, out int other) || other == host)
                        continue;
                    (neighbours[host] ?? (neighbours[host] = new List<int>())).Add(other);
                    (neighbours[other] ?? (neighbours[other] = new List<int>())).Add(host);
                }
            }
            var held = new bool[ships.Length];
            var queue = new Queue<int>();
            held[0] = ships.Length > 0 && ships[0] != null;
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                if (neighbours[index] == null)
                    continue;
                foreach (int next in neighbours[index])
                {
                    if (held[next])
                        continue;
                    held[next] = true;
                    queue.Enqueue(next);
                }
            }
            return held;
        }

        static uint LinkTarget(StatefulPart part, uint ownShip)
        {
            if (part == null)
                return 0;
            if (part.State is RotorState rotor)
                return rotor.AnchoredShipId;
            if (part.State is DockingDoorState dock && dock.IsDocked && dock.DockedConnectedPart.ShipId != ownShip)
                return dock.DockedConnectedPart.ShipId;
            return 0;
        }

        // False for a sub-ship that nothing in the schematic holds any more, for example because the part holding it
        // came from a mod that is not installed.
        public bool IsPosed(int index)
        {
            return index >= 0 && index < _posed.Length && _posed[index];
        }

        public (Vector3 position, Quaternion rotation) PoseOf(int index)
        {
            if (!IsPosed(index))
                return (Vector3.zero, Quaternion.identity);
            return _poses[index];
        }

        static (Vector3, Quaternion) Joint(IShipsProvider provider, uint hostId, StatefulPart part, RotorState rotor)
        {
            RotorJointConnection connection = RotorJointConnection.BuildFromRotorPart(new PartContext(hostId, part.Id), part.Placement, rotor.ChildShipAnchorPosition);
            connection.IsDirectionReversed = true;
            try
            {
                return connection.CalculateTransform(provider);
            }
            catch (Exception)
            {
                return connection.CalculateTransform(rotor.Angle);
            }
        }
    }

    sealed class TreeShips : IShipsProvider
    {
        readonly Dictionary<uint, TreeShipView> _ships = new Dictionary<uint, TreeShipView>();
        readonly TreeShipView _root;

        public TreeShips(IShipState[] ships)
        {
            for (int i = 0; i < ships.Length; i++)
            {
                if (ships[i] == null || _ships.ContainsKey(ships[i].Id))
                    continue;
                var view = new TreeShipView(ships[i]);
                _ships[ships[i].Id] = view;
                if (i == 0)
                    _root = view;
            }
        }

        public Action<IShipStateRead> OnShipShipStateReadUntracked { get; set; }
        public Action<uint, StatefulPart> ShipStatefulPartAdded { get; set; }
        public Action<uint, StatefulPartStateChangeEvent> ShipStatefulPartUpdated { get; set; }
        public Action<uint, StatefulPart> ShipStatefulPartRemoved { get; set; }
        public Action<uint, ShipPhysicsState> OnShipReposition { get; set; }

        public bool TryGetShip(uint shipId, out IShipStateRead ship)
        {
            bool found = _ships.TryGetValue(shipId, out TreeShipView view);
            ship = view;
            return found;
        }

        public bool TryGetStatefulPart(PartContext partContext, out StatefulPart part)
        {
            part = null;
            return _ships.TryGetValue(partContext.ShipId, out TreeShipView view) && view.TryGetStatefulPart(partContext.PartId, out part);
        }

        public IReadOnlyCollection<RootRelativeShipData> GetRootConnectedShips(uint rootShipId)
        {
            return Array.Empty<RootRelativeShipData>();
        }

        public IEnumerable<IShipStateRead> GetAllShipsInSpace(uint spaceId)
        {
            return Array.Empty<IShipStateRead>();
        }

        public bool TryGetChestInventorySlot(ShipChestSlotId slotId, out InventorySlot slot)
        {
            slot = default;
            return false;
        }

        public bool TryGetRootShip(uint shipId, out IShipStateRead ship)
        {
            ship = _root;
            return _root != null;
        }

        public bool TryGetNearestRepositionableShip(uint spaceId, Vector3 position, out IShipStateRead ship)
        {
            ship = null;
            return false;
        }
    }

    sealed class TreeShipView : IShipStateRead
    {
        readonly IShipState _ship;
        readonly Dictionary<ushort, StatefulPart> _parts = new Dictionary<ushort, StatefulPart>();

        public TreeShipView(IShipState ship)
        {
            _ship = ship;
            if (ship.StatefulParts == null)
                return;
            foreach (StatefulPart part in ship.StatefulParts)
            {
                if (part != null)
                    _parts[part.Id] = part;
            }
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
        public int PartCount => _parts.Count;
        public bool IsShipDestroyed => false;
        public IEnumerable<StatefulPart> StatefulParts => _parts.Values;

        public Ships.Grid.Model.ShipVoxel GetVoxel(Vector3Int coord) => null;

        public IEnumerable<StatefulPart> GetAllStatefulParts<T>() where T : IStatefulPartState
        {
            foreach (StatefulPart part in _parts.Values)
            {
                if (part.State is T)
                    yield return part;
            }
        }

        public bool TryGetStatefulPart(ushort partId, out StatefulPart part)
        {
            return _parts.TryGetValue(partId, out part);
        }

        public bool IsApproximatelyInRoom(Vector3 shipRelativePos) => false;
        public RoomSampleResult SampleRoomAtPosition(Vector3 shipRelativePos) => default;
        public bool HasFloorTile(Vector3 shipRelativePos) => false;

        public bool TryGetPartPlacement(ushort partId, out IPartPlacement placement)
        {
            placement = _parts.TryGetValue(partId, out StatefulPart part) ? part.Placement : null;
            return placement != null;
        }

        public bool TryGetSocketType(PartSocket socket, out CableType socketType)
        {
            socketType = default;
            return false;
        }

        public float GetMassIncludingConnectedShips() => 0f;
    }
}
