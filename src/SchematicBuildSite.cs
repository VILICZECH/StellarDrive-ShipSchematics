using System;
using System.Collections.Generic;
using FishNet;
using HarmonyLib;
using Items.Interface.Service;
using Items.Model;
using MelonLoader;
using Players;
using Players.Interface.Services;
using Players.Network.Services;
using Players.Services;
using Ships.Interface.Model.State;
using Ships.Interface.Settings;
using Ships.Visuals;
using UnityEngine;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using WorldTracking.Interface.Services;
using WorldTracking.Interface.Values;

namespace ShipSchematics
{
    // A survival blueprint: the hologram of a ship in the world, anchored to a planet or a fixed point in space, with
    // bins for the materials. When every material is in, the ship is built where the hologram stands.
    public sealed class SchematicBuildSite : MonoBehaviour
    {
        static readonly List<SchematicBuildSite> Live = new List<SchematicBuildSite>();
        static readonly Color Red = new Color(1f, 0.16f, 0.16f, 0.8f);
        static MaterialPropertyBlock _redBlock;

        SchematicFile _source;
        ISpace _anchorSpace;
        Vector3 _anchorPosition;
        Quaternion _anchorRotation = Quaternion.identity;
        readonly List<MaterialNeed> _needs = new List<MaterialNeed>();
        readonly List<Vector3> _partPoints = new List<Vector3>();
        readonly Dictionary<uint, string> _itemNames = new Dictionary<uint, string>();
        Renderer[] _renderers = new Renderer[0];
        BlueprintPartPoint _point;
        Bounds _localBounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        bool _finishing;
        bool _complete;
        bool _red;
        bool _applied;
        Vector3 _appliedPosition;
        Quaternion _appliedRotation;
        Bin[] _bins = new Bin[0];

        public static IReadOnlyList<SchematicBuildSite> Active => Live;

        public ISpace AnchorSpace => _anchorSpace;
        public Vector3 AnchorPosition => _anchorPosition;
        public Quaternion AnchorRotation => _anchorRotation;
        public SchematicFile Source => _source;
        public Bounds LocalBounds => _localBounds;
        public bool Finishing => _finishing;
        public bool HasWork => !_finishing && !_complete && _needs.Count > 0;

        public string ShipName => _source != null && !string.IsNullOrEmpty(_source.name) ? _source.name : "Ship";

        public struct Bin
        {
            public uint ItemId;
            public int Quantity;
        }

        public struct MaterialNeed
        {
            public uint ItemId;
            public string Name;
            public int Required;
        }

        void OnEnable()
        {
            if (!Live.Contains(this))
                Live.Add(this);
        }

        void OnDisable()
        {
            Live.Remove(this);
        }

        void OnDestroy()
        {
            if (_point != null)
                Destroy(_point.gameObject);
        }

        void LateUpdate()
        {
            ApplyAnchor();
        }

        // The anchor is set before the parts are counted, because a ship that costs nothing is built at once.
        public static SchematicBuildSite Create(IShipState ship, SchematicFile source, ISpace space, Vector3 position, Quaternion rotation, ShipPartVisuals hologram)
        {
            var root = new GameObject("Schematic blueprint");
            var site = root.AddComponent<SchematicBuildSite>();
            site._source = source;
            site.SetAnchor(space, position, rotation);
            if (hologram != null)
            {
                hologram.transform.SetParent(root.transform, false);
                hologram.transform.localPosition = Vector3.zero;
                hologram.transform.localRotation = Quaternion.identity;
                hologram.gameObject.SetActive(true);
                Subships.Dress(hologram, ship, source);
                site._renderers = hologram.GetComponentsInChildren<Renderer>(true);
            }

            Subships.VisitParts(ship, source, site.AddPart);
            if (site._needs.Count > 0)
            {
                site._localBounds = site.AimBounds(hologram);
                site.CreateAimVolume(hologram);
                return site;
            }
            if (site.Finish())
                return site;
            Destroy(root);
            return null;
        }

        void SetAnchor(ISpace space, Vector3 position, Quaternion rotation)
        {
            _anchorSpace = CopySpace(space);
            _anchorPosition = position;
            _anchorRotation = rotation;
            _applied = false;
            ApplyAnchor();
        }

        // The game's space objects can be shared and change; the blueprint keeps its own copy of the anchor.
        static ISpace CopySpace(ISpace space)
        {
            if (space is PlanetSurfaceSpace planet)
                return new PlanetSurfaceSpace { PlanetId = planet.PlanetId, Center = planet.Center };
            if (space is FixedWorldSpace pinned)
                return new FixedWorldSpace { Position = pinned.Position };
            return space;
        }

        void ApplyAnchor()
        {
            if (_finishing || !TryAnchorPose(ClientOrigin.LocalVisualSpace, out Vector3 visual, out Quaternion rotation))
                return;
            if (_applied && visual == _appliedPosition && rotation == _appliedRotation)
                return;
            _applied = true;
            _appliedPosition = visual;
            _appliedRotation = rotation;
            transform.SetPositionAndRotation(visual, rotation);
            if (_point != null)
                _point.SnapToHologram();
        }

        bool TryAnchorPose(ISpace target, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (_anchorSpace == null || target == null)
                return false;
            IWorldTransposeClient transpose = GameServices.Get<IWorldTransposeClient>();
            if (transpose == null)
                return false;
            position = transpose.Position(_anchorSpace, target, _anchorPosition);
            rotation = transpose.Rotation(_anchorSpace, target, _anchorRotation);
            return true;
        }

        void AddPart(PartSettings settings, Vector3 local)
        {
            _partPoints.Add(local);
            if (settings == null || settings.BuildingCost == null)
                return;
            foreach (ItemDrop drop in settings.BuildingCost)
            {
                if (drop.Quantity == 0)
                    continue;
                int index = _needs.FindIndex(need => need.ItemId == drop.ItemId);
                if (index < 0)
                {
                    _needs.Add(new MaterialNeed { ItemId = drop.ItemId, Name = ItemName(drop.ItemId), Required = drop.Quantity });
                    continue;
                }
                MaterialNeed need = _needs[index];
                need.Required += drop.Quantity;
                _needs[index] = need;
            }
        }

        void CreateAimVolume(ShipPartVisuals hologram)
        {
            var volume = new GameObject("Blueprint interact");
            BoxCollider box = volume.AddComponent<BoxCollider>();
            box.isTrigger = false;
            box.center = _localBounds.center;
            box.size = _localBounds.size;
            _point = BlueprintPartPoint.Attach(volume, this, hologram);
        }

        // The parts' positions plus the renderers next to them, in the blueprint's own frame. A renderer far from
        // every part (a long beam, a sub-ship's arm) does not stretch the box over empty space.
        Bounds AimBounds(ShipPartVisuals hologram)
        {
            if (_partPoints.Count == 0)
                return new Bounds(Vector3.zero, Vector3.one * 2f);
            var bounds = new Bounds(_partPoints[0], Vector3.zero);
            for (int i = 1; i < _partPoints.Count; i++)
                bounds.Encapsulate(_partPoints[i]);

            if (hologram != null)
            {
                foreach (Renderer renderer in _renderers)
                {
                    if (renderer == null)
                        continue;
                    Bounds world = renderer.bounds;
                    if ((transform.InverseTransformPoint(world.center) - bounds.center).sqrMagnitude > 25f)
                        continue;
                    for (int x = -1; x <= 1; x += 2)
                    {
                        for (int y = -1; y <= 1; y += 2)
                        {
                            for (int z = -1; z <= 1; z += 2)
                            {
                                Vector3 corner = transform.InverseTransformPoint(world.center + Vector3.Scale(world.extents, new Vector3(x, y, z)));
                                if ((corner - bounds.center).sqrMagnitude <= 36f)
                                    bounds.Encapsulate(corner);
                            }
                        }
                    }
                }
            }

            bounds.Expand(0.3f);
            bounds.size = Vector3.Max(bounds.size, new Vector3(0.8f, 0.8f, 0.8f));
            return bounds;
        }

        public void EnsureBins(int count)
        {
            count = Math.Max(0, count);
            for (int i = _bins.Length - 1; i >= 0; i--)
            {
                if (_bins[i].Quantity > 0 && i + 1 > count)
                    count = i + 1;
            }
            if (_bins.Length == count)
                return;
            var next = new Bin[count];
            Array.Copy(_bins, next, Math.Min(count, _bins.Length));
            _bins = next;
        }

        public int BinCount => _bins.Length;

        public Bin BinAt(int index)
        {
            if (index < 0 || index >= _bins.Length)
                return default;
            return _bins[index];
        }

        public bool CanAccept(int index, uint itemId)
        {
            if (index < 0 || index >= _bins.Length || itemId == 0)
                return false;
            return _bins[index].Quantity <= 0 || _bins[index].ItemId == itemId;
        }

        public int BinOf(uint itemId)
        {
            for (int i = 0; i < _bins.Length; i++)
            {
                if (_bins[i].Quantity > 0 && _bins[i].ItemId == itemId)
                    return i;
            }
            return -1;
        }

        public int EmptyBin()
        {
            for (int i = 0; i < _bins.Length; i++)
            {
                if (_bins[i].Quantity <= 0)
                    return i;
            }
            return -1;
        }

        public bool StillNeeds(uint itemId)
        {
            for (int i = 0; i < _needs.Count; i++)
            {
                if (_needs[i].ItemId != itemId)
                    continue;
                return Stored(itemId) < _needs[i].Required;
            }
            return false;
        }

        // The chest clone only has a handful of slots. A needed material past that still goes in, just without a
        // visible cell; the Missing list is what the player reads.
        public bool CanStore(uint itemId)
        {
            return itemId != 0 && (BinOf(itemId) >= 0 || EmptyBin() >= 0 || StillNeeds(itemId));
        }

        public void PutBin(int index, uint itemId, int quantity)
        {
            if (!CanAccept(index, itemId) || quantity <= 0)
                return;
            Bin bin = _bins[index];
            bin.ItemId = itemId;
            bin.Quantity += quantity;
            _bins[index] = bin;
        }

        // Finds an existing or empty bin, or grows past the visible chest when the item is still missing.
        public int Put(uint itemId, int quantity)
        {
            if (itemId == 0 || quantity <= 0)
                return 0;
            int index = BinOf(itemId);
            if (index < 0)
                index = EmptyBin();
            if (index < 0)
            {
                if (!StillNeeds(itemId))
                    return 0;
                index = _bins.Length;
                EnsureBins(index + 1);
                MelonLogger.Msg("Blueprint " + ShipName + " accepted " + quantity + " " + ItemName(itemId) + " without a free slot.");
            }
            PutBin(index, itemId, quantity);
            return quantity;
        }

        public void RestoreBin(int index, uint itemId, int quantity)
        {
            if (index < 0 || itemId == 0 || quantity <= 0)
                return;
            if (index >= _bins.Length)
                EnsureBins(index + 1);
            _bins[index] = new Bin { ItemId = itemId, Quantity = quantity };
        }

        public bool TakeBin(int index, out uint itemId, out int quantity)
        {
            itemId = 0;
            quantity = 0;
            if (index < 0 || index >= _bins.Length || _bins[index].Quantity <= 0)
                return false;
            itemId = _bins[index].ItemId;
            quantity = _bins[index].Quantity;
            _bins[index] = default;
            return true;
        }

        int Stored(uint itemId)
        {
            int total = 0;
            for (int i = 0; i < _bins.Length; i++)
            {
                if (_bins[i].ItemId == itemId)
                    total += _bins[i].Quantity;
            }
            return total;
        }

        public void CollectMissing(List<MaterialNeed> rows)
        {
            rows.Clear();
            foreach (MaterialNeed need in _needs)
            {
                int missing = need.Required - Stored(need.ItemId);
                if (missing > 0)
                    rows.Add(new MaterialNeed { ItemId = need.ItemId, Name = need.Name, Required = missing });
            }
        }

        bool IsSupplied()
        {
            if (_needs.Count == 0)
                return false;
            foreach (MaterialNeed need in _needs)
            {
                if (Stored(need.ItemId) < need.Required)
                    return false;
            }
            return true;
        }

        // The ship is built first; the bins are emptied only when it stands, so a failed build keeps the materials.
        public void CompleteIfReady()
        {
            if (_finishing || !IsSupplied() || !Finish())
                return;
            var left = new Dictionary<uint, int>();
            foreach (MaterialNeed need in _needs)
                left[need.ItemId] = need.Required;
            int lost = 0;
            for (int i = 0; i < _bins.Length; i++)
            {
                if (_bins[i].Quantity <= 0)
                    continue;
                left.TryGetValue(_bins[i].ItemId, out int need);
                int consume = Math.Min(_bins[i].Quantity, need);
                left[_bins[i].ItemId] = need - consume;
                int extra = _bins[i].Quantity - consume;
                if (extra > 0)
                    lost += ReturnItems(_bins[i].ItemId, extra);
                _bins[i] = default;
            }
            if (lost > 0)
                ScreenMessages.Status("Your inventory was full. " + lost + " leftover item" + (lost == 1 ? " was" : "s were") + " lost.");
        }

        // Scrapping returns what was delivered. Whatever does not fit in the inventory stays in the blueprint.
        public void Scrap()
        {
            if (_finishing)
                return;
            if (!RefundStored())
            {
                ScreenMessages.Status("Your inventory is full. Make room to scrap the blueprint.");
                return;
            }
            _finishing = true;
            BlueprintSupplyMenu.CloseCurrent();
            Destroy(gameObject);
        }

        bool RefundStored()
        {
            bool all = true;
            for (int i = 0; i < _bins.Length; i++)
            {
                if (_bins[i].Quantity <= 0)
                    continue;
                int left = ReturnItems(_bins[i].ItemId, _bins[i].Quantity);
                if (left > 0)
                {
                    _bins[i].Quantity = left;
                    all = false;
                }
                else
                {
                    _bins[i] = default;
                }
            }
            return all;
        }

        public void SetRecycleLook(bool on)
        {
            if (_red == on)
                return;
            _red = on;
            if (on && _redBlock == null)
            {
                _redBlock = new MaterialPropertyBlock();
                _redBlock.SetColor("_BaseColor", Red);
                _redBlock.SetColor("_Color", Red);
                _redBlock.SetColor("_EmissionColor", new Color(1.4f, 0.05f, 0.05f));
            }
            foreach (Renderer renderer in _renderers)
            {
                if (renderer != null)
                    renderer.SetPropertyBlock(on ? _redBlock : null);
            }
        }

        bool Finish()
        {
            if (_finishing)
                return false;
            _finishing = true;
            BlueprintSupplyMenu.CloseCurrent();
            if (!TryAnchorPose(ClientOrigin.LocalPhysicsSpace, out Vector3 point, out Quaternion rotation))
            {
                point = transform.position;
                rotation = transform.rotation;
            }
            if (!InstanceFinder.IsServerStarted)
                return FinishOnHost(point, rotation);
            if (!SchematicPlacer.Materialize(_source, point, rotation, out string message))
            {
                _finishing = false;
                message = string.IsNullOrEmpty(message) ? "Could not build the ship." : message;
                MelonLogger.Warning(message);
                ScreenMessages.Status(message);
                return false;
            }
            _complete = true;
            MelonLogger.Msg(message);
            ScreenMessages.Status(message);
            Destroy(gameObject);
            return true;
        }

        bool FinishOnHost(Vector3 point, Quaternion rotation)
        {
            if (!SchematicNet.Request(_source, ClientOrigin.LocalPhysicsSpaceId, point, rotation, OnHostBuilt))
            {
                _finishing = false;
                ScreenMessages.Status("Could not reach the host.");
                return false;
            }
            ScreenMessages.Status("The host is placing your ship.");
            return false;
        }

        void OnHostBuilt(bool ok, string message)
        {
            if (this == null)
                return;
            if (!ok)
            {
                _finishing = false;
                ScreenMessages.Status(string.IsNullOrEmpty(message) ? "Could not build the ship." : message);
                return;
            }
            _complete = true;
            if (!string.IsNullOrEmpty(message))
                ScreenMessages.Status(message);
            Destroy(gameObject);
        }

        // Takes up to quantity items from the local player and returns how many were taken.
        public int PullItems(uint itemId, int quantity)
        {
            TrackedPlayerServer player = LocalPlayer();
            IInventoryProvider inventory = GameServices.Get<IInventoryProvider>();
            if (player == null || inventory == null || itemId == 0 || quantity <= 0)
                return 0;
            int have;
            try
            {
                have = inventory.GetItemQuantity(itemId);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not read inventory item " + itemId + ". " + ex.Message);
                return 0;
            }
            quantity = Math.Min(quantity, have);
            if (quantity <= 0)
                return 0;
            try
            {
                player.RemoveItemsFromInventory(Drops(itemId, quantity));
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not move the material into the blueprint.", ex);
                return 0;
            }
            return quantity;
        }

        // Gives items back to the local player and returns how many did not fit.
        public int ReturnItems(uint itemId, int quantity)
        {
            if (itemId == 0 || quantity <= 0)
                return 0;
            TrackedPlayerServer player = LocalPlayer();
            if (player == null)
                return quantity;
            int left = quantity;
            while (left > 0)
            {
                int chunk = Math.Min(left, 255);
                int added = Give(player, itemId, chunk);
                left -= added;
                if (added < chunk)
                    break;
            }
            return left;
        }

        static List<ItemDrop> Drops(uint itemId, int quantity)
        {
            var drops = new List<ItemDrop>();
            for (int left = quantity; left > 0; left -= 255)
                drops.Add(new ItemDrop { ItemId = itemId, Quantity = (byte)Math.Min(left, 255) });
            return drops;
        }

        static AccessTools.FieldRef<TrackedPlayerServer, PlayerNetworkInventoryOperations> _operations;
        static AccessTools.FieldRef<TrackedPlayerServer, PlayerNetworkInventoryNotifications> _notifications;
        static bool _inventoryBound;

        // TrackedPlayerServer.AddItemToInventory drops what does not fit without saying how much; ServerAddItem
        // returns the amount added.
        static int Give(TrackedPlayerServer player, uint itemId, int quantity)
        {
            var drop = new ItemDrop { ItemId = itemId, Quantity = (byte)quantity };
            if (!_inventoryBound)
            {
                _inventoryBound = true;
                try
                {
                    _operations = AccessTools.FieldRefAccess<TrackedPlayerServer, PlayerNetworkInventoryOperations>("_inventoryOperations");
                    _notifications = AccessTools.FieldRefAccess<TrackedPlayerServer, PlayerNetworkInventoryNotifications>("_notifications");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("The player inventory has changed; a full inventory cannot be detected. " + ex.Message);
                }
            }
            PlayerNetworkInventoryOperations operations = _operations == null ? null : _operations(player);
            if (operations == null)
            {
                player.AddItemToInventory(drop);
                return quantity;
            }
            byte added = operations.ServerAddItem(drop);
            PlayerNetworkInventoryNotifications notifications = _notifications == null ? null : _notifications(player);
            if (added > 0 && notifications != null)
            {
                drop.Quantity = added;
                notifications.NotifyItemReceive(drop);
            }
            return added;
        }

        string ItemName(uint itemId)
        {
            if (_itemNames.TryGetValue(itemId, out string cached))
                return cached;
            string name = "item " + itemId;
            try
            {
                IItemSettingsProvider items = GameServices.Get<IItemSettingsProvider>();
                ItemSettings settings = items == null ? null : items.GetItemSettingsById(itemId);
                if (settings != null && !string.IsNullOrEmpty(settings.itemName))
                    name = settings.itemName;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not read the name of item " + itemId + ". " + ex.Message);
            }
            _itemNames[itemId] = name;
            return name;
        }

        static TrackedPlayerServer LocalPlayer()
        {
            try
            {
                PlayersClientTracker clients = GameServices.Get<PlayersClientTracker>();
                PlayersServerTracker servers = GameServices.Get<PlayersServerTracker>();
                if (clients == null || clients.LocalPlayer == null || servers == null)
                    return null;
                uint id = clients.LocalPlayer.Id;
                if (servers.GetPlayer(id) is TrackedPlayerServer found)
                    return found;
                foreach (object entry in servers.ActivePlayers)
                {
                    if (entry is TrackedPlayerServer server && server.Id == id)
                        return server;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not find the local player. " + ex.Message);
            }
            return null;
        }
    }
}
