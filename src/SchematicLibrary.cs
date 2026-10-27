using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MelonLoader;
using Saving.Serialization.V15.Ships;
using Saving.Services.Converters;
using Saving.Services.Model.State;
using Ships;
using Ships.Cables;
using Ships.Interface.Model;
using Ships.Interface.Model.Joints;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Joints;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Services;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    public sealed class CaptureResult
    {
        public SchematicFile File;
        public SchematicFile Match;
    }

    public static class SchematicLibrary
    {
        static readonly ShipsConverter Converter = new ShipsConverter();
        const int MaxPictureBytes = 4 * 1024 * 1024;
        // Version 2 files own their ship pairs; older ones get the pair of their own ship once (BackfillIdentity).
        public const int FileVersion = 2;

        static bool _loaded;
        static string _folder;

        public static List<SchematicFile> Entries { get; } = new List<SchematicFile>();

        public static string Folder
        {
            get
            {
                if (_folder == null)
                {
                    _folder = Path.Combine(Application.persistentDataPath, "Schematics");
                    Directory.CreateDirectory(_folder);
                }
                return _folder;
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded)
                return;
            Load();
            _loaded = true;
        }

        // The file name is the id: a copied or renamed file must not share an id with another entry.
        static void Load()
        {
            Entries.Clear();
            var ids = new HashSet<string>();
            foreach (string path in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    SchematicFile entry = JsonUtility.FromJson<SchematicFile>(File.ReadAllText(path));
                    if (entry == null || string.IsNullOrEmpty(entry.shipJson))
                        continue;
                    string id = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(id) || !ids.Add(id))
                        continue;
                    entry.id = id;
                    entry.cables = PreferCables(entry.cables, entry.cableData);
                    Entries.Add(entry);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Could not load schematic " + Path.GetFileName(path) + ". " + ex.Message);
                }
            }
            foreach (SchematicFile entry in Entries.ToArray())
                BackfillIdentity(entry);
            SortEntries();
        }

        static void SortEntries()
        {
            Entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        public static CaptureResult Capture(TrackedShip ship)
        {
            EnsureLoaded();
            ship = HostCopy(ship);
            if (ship == null || ship.State == null)
                return null;
            List<TrackedShip> cluster = FromRoot(Cluster(ship), ship);
            var index = new Dictionary<uint, int>();
            var spaceIndex = new Dictionary<uint, int>();
            for (int i = 0; i < cluster.Count; i++)
            {
                index[cluster[i].State.Id] = i;
                if (cluster[i].State.RelativeSpaceId != 0)
                    spaceIndex[cluster[i].State.RelativeSpaceId] = i;
            }

            CableRecord[][] shipCables = ReadCables(cluster, index, spaceIndex, out List<LinkRecord> links);
            var kept = new HashSet<uint>(index.Keys);
            HashSet<uint> owned = HeldByJoints(cluster);
            var used = new List<PartSettings>();
            var fileIndex = new int[cluster.Count];
            int saved = 0;
            var ids = new List<uint>();
            var spaces = new List<uint>();
            var children = new List<string>();
            var childCables = new List<string>();
            var childPoses = new List<string>();
            IWorldTranspose transpose = Transpose();
            string shipJson = null;
            CableRecord[] cables = Array.Empty<CableRecord>();
            int partCount = 0;
            int cableCount = 0;
            for (int i = 0; i < cluster.Count; i++)
            {
                SerializableShip serializable = SerializeShip(cluster[i], kept, used, out int parts);
                if (serializable == null)
                {
                    fileIndex[i] = -1;
                    continue;
                }
                if (i == 0)
                {
                    shipJson = JsonUtility.ToJson(serializable);
                    cables = shipCables[i];
                }
                else
                {
                    children.Add(JsonUtility.ToJson(serializable));
                    childCables.Add(FormatCables(shipCables[i]));
                    childPoses.Add(RelativePose(transpose, cluster[0], cluster[i]));
                }
                fileIndex[i] = saved++;
                partCount += parts;
                cableCount += shipCables[i].Length;
                if (owned.Contains(cluster[i].State.Id))
                {
                    ids.Add(cluster[i].State.Id);
                    spaces.Add(cluster[i].State.RelativeSpaceId);
                }
            }
            if (string.IsNullOrEmpty(shipJson) || partCount == 0)
                return null;

            var savedLinks = new List<LinkRecord>();
            foreach (LinkRecord link in links)
            {
                int a = fileIndex[link.aShip];
                int b = fileIndex[link.bShip];
                if (a < 0 || b < 0)
                    continue;
                link.aShip = a;
                link.bShip = b;
                savedLinks.Add(link);
            }

            var file = new SchematicFile
            {
                version = FileVersion,
                id = Guid.NewGuid().ToString("N"),
                savedUtc = DateTime.UtcNow.ToString("o"),
                shipJson = shipJson,
                childShipJson = children.ToArray(),
                childCableData = childCables.ToArray(),
                childPoses = childPoses.ToArray(),
                linkData = FormatLinks(savedLinks),
                partNames = PartCatalog.Format(used),
                partCount = partCount,
                shipIds = ids.ToArray(),
                shipSpaces = spaces.ToArray(),
                cables = cables
            };
            MelonLogger.Msg("Captured " + Plural(partCount, "part") + ", " + Plural(cableCount, "connection") + ", "
                + Plural(children.Count, "sub-ship") + " and " + Plural(savedLinks.Count, "connection") + " between ships.");

            return new CaptureResult
            {
                File = file,
                Match = FindMatch(cluster, owned)
            };
        }

        static string Plural(int count, string noun)
        {
            return count + " " + noun + (count == 1 ? "" : "s");
        }

        // A docked shuttle may have a schematic of its own, so only the root and the ships its rotors and pistons hold
        // decide which schematic this ship is.
        static SchematicFile FindMatch(List<TrackedShip> cluster, HashSet<uint> owned)
        {
            foreach (TrackedShip ship in cluster)
            {
                if (!owned.Contains(ship.State.Id))
                    continue;
                SchematicFile match = FindByShip(ship.State.Id, ship.State.RelativeSpaceId);
                if (match != null)
                    return match;
            }
            return null;
        }

        static HashSet<uint> HeldByJoints(List<TrackedShip> cluster)
        {
            var held = new HashSet<uint>();
            if (cluster.Count == 0)
                return held;
            var inCluster = new HashSet<uint>();
            foreach (TrackedShip ship in cluster)
                inCluster.Add(ship.State.Id);
            held.Add(cluster[0].State.Id);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (TrackedShip ship in cluster)
                {
                    foreach (uint target in RotorTargets(ship))
                    {
                        if (!inCluster.Contains(target) || held.Contains(ship.State.Id) == held.Contains(target))
                            continue;
                        held.Add(ship.State.Id);
                        held.Add(target);
                        grew = true;
                    }
                }
            }
            return held;
        }

        static TrackedShip HostCopy(TrackedShip ship)
        {
            if (ship == null || ship.State == null)
                return ship;
            return ServerShip(ship.State.Id) ?? ship;
        }

        // The host's own copy of a ship; null on a client or for a ship the server does not know.
        static TrackedShipServer ServerShip(uint id)
        {
            ShipsServerTracker server = GameServices.Find<ShipsServerTracker>();
            if (server == null || id == 0)
                return null;
            try
            {
                TrackedShipServer hosted = server.GetShip(id);
                return hosted != null && hosted.State != null ? hosted : null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not read ship " + id + " on the server. " + ex.Message);
                return null;
            }
        }

        static IShipsProvider ShipsProvider()
        {
            if ((object)GameServices.Find<ShipsServerTracker>() is IShipsProvider server)
                return server;
            return GameServices.Get<IShipsProvider>();
        }

        // Every ship joined to the start by a rotor, a piston or docking, in either direction.
        static List<TrackedShip> Cluster(TrackedShip start)
        {
            var candidates = new Dictionary<uint, TrackedShip>();
            AddCandidate(candidates, start);
            IShipsProvider provider = ShipsProvider();
            if (provider != null)
            {
                try
                {
                    if (provider.TryGetRootShip(start.State.Id, out IShipStateRead root) && root != null)
                    {
                        foreach (RootRelativeShipData data in provider.GetRootConnectedShips(root.Id))
                            AddCandidate(candidates, data.Ship as TrackedShip);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Could not read the ships joined to this one. " + ex.Message);
                }
                AddSpace(candidates, provider, start.SpaceId);
                AddSpace(candidates, provider, start.State.RelativeSpaceId);
            }

            var found = new List<TrackedShip>();
            var seen = new HashSet<uint>();
            var queue = new Queue<TrackedShip>();
            queue.Enqueue(start);
            // The built rotor tree is on the joint graph, not only on RotorState.AnchoredShipId. A vanilla rotor
            // head is a child ship; if the state id is 0 the joint still knows it.
            if (provider != null)
            {
                try
                {
                    if (provider.TryGetRootShip(start.State.Id, out IShipStateRead root) && root != null)
                    {
                        foreach (RootRelativeShipData data in provider.GetRootConnectedShips(root.Id))
                        {
                            if (!(data.Ship is TrackedShip tree))
                                continue;
                            AddCandidate(candidates, tree);
                            if (candidates.TryGetValue(tree.State.Id, out TrackedShip queued))
                                queue.Enqueue(queued);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Could not read the rotor tree of this ship. " + ex.Message);
                }
            }
            while (queue.Count > 0)
            {
                TrackedShip ship = queue.Dequeue();
                if (ship == null || ship.State == null || !seen.Add(ship.State.Id))
                    continue;
                found.Add(ship);
                foreach (uint next in Links(ship))
                {
                    if (!candidates.TryGetValue(next, out TrackedShip linked))
                    {
                        linked = ResolveShip(next);
                        AddCandidate(candidates, linked);
                    }
                    if (linked != null)
                        queue.Enqueue(linked);
                    else
                        MelonLogger.Warning("A ship joined to this one was not loaded, so it was left out.");
                }
                foreach (TrackedShip other in candidates.Values)
                {
                    if (seen.Contains(other.State.Id))
                        continue;
                    foreach (uint target in Links(other))
                    {
                        if (target != ship.State.Id)
                            continue;
                        queue.Enqueue(other);
                        break;
                    }
                }
            }
            return found;
        }

        // The root is the ship no rotor holds, the largest one when several qualify (a docked shuttle is not the root
        // of its carrier). A save made while standing on a sub-ship still places the whole machine.
        static List<TrackedShip> FromRoot(List<TrackedShip> cluster, TrackedShip start)
        {
            var byId = new Dictionary<uint, TrackedShip>();
            foreach (TrackedShip ship in cluster)
                byId[ship.State.Id] = ship;
            var targeted = new HashSet<uint>();
            var neighbours = new Dictionary<uint, List<TrackedShip>>();
            foreach (TrackedShip ship in cluster)
            {
                foreach (uint target in RotorTargets(ship))
                {
                    if (target != ship.State.Id && byId.ContainsKey(target))
                        targeted.Add(target);
                }
                foreach (uint target in Links(ship))
                {
                    if (target == ship.State.Id || !byId.TryGetValue(target, out TrackedShip other))
                        continue;
                    Neighbours(neighbours, ship.State.Id).Add(other);
                    Neighbours(neighbours, target).Add(ship);
                }
            }
            TrackedShip root = null;
            foreach (TrackedShip ship in cluster)
            {
                if (targeted.Contains(ship.State.Id))
                    continue;
                if (root == null || ship.State.PartCount > root.State.PartCount || (ship == start && ship.State.PartCount == root.State.PartCount))
                    root = ship;
            }
            if (root == null)
                root = start;

            var order = new List<TrackedShip>();
            var seen = new HashSet<uint>();
            var queue = new Queue<TrackedShip>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                TrackedShip ship = queue.Dequeue();
                if (!seen.Add(ship.State.Id))
                    continue;
                order.Add(ship);
                if (!neighbours.TryGetValue(ship.State.Id, out List<TrackedShip> next))
                    continue;
                foreach (TrackedShip other in next)
                    queue.Enqueue(other);
            }
            foreach (TrackedShip ship in cluster)
            {
                if (seen.Add(ship.State.Id))
                    order.Add(ship);
            }
            return order;
        }

        static List<TrackedShip> Neighbours(Dictionary<uint, List<TrackedShip>> map, uint id)
        {
            if (!map.TryGetValue(id, out List<TrackedShip> list))
            {
                list = new List<TrackedShip>();
                map[id] = list;
            }
            return list;
        }

        // Ships this ship holds by a rotor or a piston, and ships it is docked to.
        static IEnumerable<uint> Links(TrackedShip ship)
        {
            if (ship == null || ship.State == null)
                yield break;
            foreach (StatefulPart part in ship.State.GetStatefulParts())
            {
                if (part == null)
                    continue;
                if (part.State is RotorState rotor && rotor.AnchoredShipId != 0)
                    yield return rotor.AnchoredShipId;
                else if (part.State is DockingDoorState dock && dock.IsDocked && dock.DockedConnectedPart.ShipId != 0 && dock.DockedConnectedPart.ShipId != ship.State.Id)
                    yield return dock.DockedConnectedPart.ShipId;
            }
            foreach (uint id in JointShips(ship))
                yield return id;
        }

        // Vanilla rotor heads live on the joint cache. RotorState.AnchoredShipId can be 0 while the joint still holds.
        static IEnumerable<uint> JointShips(TrackedShip ship)
        {
            ShipsJointsChangeProvider joints = Joints();
            if (joints == null || ship == null || ship.State == null)
                yield break;
            foreach (RotorJointData joint in joints.GetShipRotorJoints(ship.State.Id))
            {
                if (joint.OtherShipId != 0 && joint.OtherShipId != ship.State.Id)
                    yield return joint.OtherShipId;
                uint host = joint.RotorPart.ShipId;
                if (host != 0 && host != ship.State.Id)
                    yield return host;
            }
        }

        static ShipsJointsChangeProvider Joints()
        {
            ShipsJointsChangeProvider server = GameServices.Get<ShipsJointsChangeProviderServer>();
            if (server != null)
                return server;
            return GameServices.Get<ShipsJointsChangeProviderClient>();
        }

        static IWorldTranspose Transpose()
        {
            IWorldTranspose server = GameServices.Get<IWorldTransposeServer>();
            return server ?? GameServices.Get<IWorldTransposeClient>();
        }

        // Where a sub-ship sits in the root ship's frame, as x,y,z,qx,qy,qz,qw. Empty when the spaces are unknown.
        static string RelativePose(IWorldTranspose transpose, TrackedShip root, TrackedShip ship)
        {
            if (transpose == null)
                return "";
            try
            {
                TransformOperation operation = transpose.ToTransformOperation(ship.State.RelativeSpaceId, root.State.RelativeSpaceId);
                return FormatPose(operation.MultiplyPoint(Vector3.zero), ((Quaternion)operation.Rotation).normalized);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not measure where a sub-ship sits. " + ex.Message);
                return "";
            }
        }

        public static string FormatPose(Vector3 position, Quaternion rotation)
        {
            float[] values = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
            var text = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    text.Append(',');
                text.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        public static bool TryParsePose(string text, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (string.IsNullOrEmpty(text))
                return false;
            string[] fields = text.Split(',');
            if (fields.Length != 7)
                return false;
            var values = new float[7];
            for (int i = 0; i < 7; i++)
            {
                if (!float.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                    return false;
            }
            var turn = new Quaternion(values[3], values[4], values[5], values[6]);
            float length = Mathf.Sqrt(turn.x * turn.x + turn.y * turn.y + turn.z * turn.z + turn.w * turn.w);
            if (length < 0.0001f)
                return false;
            position = new Vector3(values[0], values[1], values[2]);
            rotation = new Quaternion(turn.x / length, turn.y / length, turn.z / length, turn.w / length);
            return true;
        }

        static void AddCandidate(Dictionary<uint, TrackedShip> candidates, TrackedShip ship)
        {
            if (ship == null || ship.State == null)
                return;
            candidates[ship.State.Id] = HostCopy(ship);
        }

        static void AddSpace(Dictionary<uint, TrackedShip> candidates, IShipsProvider provider, uint spaceId)
        {
            if (spaceId == 0)
                return;
            try
            {
                foreach (IShipStateRead ship in provider.GetAllShipsInSpace(spaceId))
                    AddCandidate(candidates, ship as TrackedShip);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not list the ships in space " + spaceId + ". " + ex.Message);
            }
        }

        static TrackedShip ResolveShip(uint id)
        {
            if (id == 0)
                return null;
            IShipsClientProvider client = GameServices.Get<IShipsClientProvider>();
            if (client != null && client.TryGetShip(id, out IShipStateReadClient read) && read is TrackedShip trackedClient)
                return HostCopy(trackedClient);
            return ServerShip(id);
        }

        static IEnumerable<uint> RotorTargets(TrackedShip ship)
        {
            if (ship == null || ship.State == null)
                yield break;
            foreach (StatefulPart part in ship.State.GetStatefulParts())
            {
                if (part != null && part.State is RotorState rotor && rotor.AnchoredShipId != 0)
                    yield return rotor.AnchoredShipId;
            }
            ShipsJointsChangeProvider joints = Joints();
            if (joints == null)
                yield break;
            foreach (RotorJointData joint in joints.GetShipRotorJoints(ship.State.Id))
            {
                if (joint.RotorPart.ShipId == ship.State.Id && joint.OtherShipId != 0 && joint.OtherShipId != ship.State.Id)
                    yield return joint.OtherShipId;
            }
        }

        static SerializableShip SerializeShip(TrackedShip ship, HashSet<uint> kept, List<PartSettings> used, out int partCount)
        {
            ShipState live = ship.State;
            HullPart[] hull = live.GetHullPartObjects().ToArray();
            StatefulPart[] parts = live.GetStatefulParts().ToArray();
            partCount = hull.Length + parts.Length;
            if (partCount == 0)
                return null;
            foreach (HullPart part in hull)
                used.Add(part.Settings);
            foreach (StatefulPart part in parts)
            {
                if (part != null)
                    used.Add(part.Settings);
            }
            var snapshot = new ShipStateSnapshot
            {
                Id = live.Id,
                RelativeSpaceId = live.RelativeSpaceId,
                SpaceId = ship.SpaceId,
                Position = ship.Position,
                Rotation = ship.Rotation,
                Velocity = Vector3.zero,
                RoomGasState = Array.Empty<Ships.Interface.Model.Gases.RoomGasState>(),
                HullParts = hull,
                StatefulParts = parts
            };
            SerializableShip serializable = Converter.ConvertToSerializable(new SavedShipStates(new IShipState[] { snapshot }))[0];
            FillRotorTargets(serializable, ship);
            ClearTargets(serializable);
            RetargetLinks(serializable, id => kept.Contains(id) ? id : 0u);
            StripContents(serializable);
            return serializable;
        }

        static SchematicFile FindByShip(uint shipId, uint relativeSpaceId)
        {
            if (shipId == 0 || relativeSpaceId == 0)
                return null;
            foreach (SchematicFile entry in Entries)
            {
                if (ContainsPair(entry, shipId, relativeSpaceId))
                    return entry;
            }
            return null;
        }

        // A pair (ship id, relative space) marks a ship in the world as built from a schematic, so saving it again
        // offers Overwrite. Each pair belongs to one schematic. The claim goes to the library entry with the owner's
        // id: a blueprint may hold an older copy of the entry, and a deleted schematic must not be written back.
        public static void ClaimShips(SchematicFile owner, IEnumerable<(uint ship, uint space)> pairs)
        {
            SchematicFile entry = owner == null || string.IsNullOrEmpty(owner.id) ? null : Entries.Find(e => e != null && e.id == owner.id);
            if (entry == null || pairs == null)
                return;
            var changed = new HashSet<SchematicFile>();
            foreach ((uint ship, uint space) in pairs)
            {
                if (ship == 0 || space == 0)
                    continue;
                foreach (SchematicFile other in Entries)
                {
                    if (other != entry && RemovePair(other, ship, space))
                        changed.Add(other);
                }
                if (!ContainsPair(entry, ship, space))
                {
                    entry.shipIds = AppendShip(entry.shipIds, ship);
                    entry.shipSpaces = AppendShip(entry.shipSpaces, space);
                    changed.Add(entry);
                }
            }
            foreach (SchematicFile file in changed)
                Write(file);
        }

        public static bool SaveNew(SchematicFile file)
        {
            file.name = (file.name ?? "").Trim();
            if (file.name.Length == 0 || (TryDefaultNumber(file.name, out _) && NameTaken(file.name)))
                file.name = NextName();
            file.savedUtc = DateTime.UtcNow.ToString("o");
            if (!Write(file))
                return false;
            Entries.Add(file);
            SortEntries();
            ClaimShips(file, Pairs(file.shipIds, file.shipSpaces));
            return true;
        }

        public static bool Overwrite(SchematicFile existing, SchematicFile incoming)
        {
            List<(uint ship, uint space)> captured = Pairs(incoming.shipIds, incoming.shipSpaces);
            incoming.id = existing.id;
            if (string.IsNullOrEmpty(incoming.name))
                incoming.name = existing.name;
            incoming.shipIds = existing.shipIds;
            incoming.shipSpaces = existing.shipSpaces;
            foreach ((uint ship, uint space) in captured)
            {
                if (!ContainsPair(incoming, ship, space))
                {
                    incoming.shipIds = AppendShip(incoming.shipIds, ship);
                    incoming.shipSpaces = AppendShip(incoming.shipSpaces, space);
                }
            }
            incoming.savedUtc = DateTime.UtcNow.ToString("o");
            if (!Write(incoming))
                return false;
            int index = Entries.FindIndex(entry => entry.id == existing.id);
            if (index >= 0)
                Entries[index] = incoming;
            else
                Entries.Add(incoming);
            SortEntries();
            ClaimShips(incoming, captured);
            return true;
        }

        static List<(uint ship, uint space)> Pairs(uint[] ids, uint[] spaces)
        {
            var pairs = new List<(uint ship, uint space)>();
            if (ids == null || spaces == null)
                return pairs;
            int count = Math.Min(ids.Length, spaces.Length);
            for (int i = 0; i < count; i++)
            {
                if (ids[i] != 0 && spaces[i] != 0)
                    pairs.Add((ids[i], spaces[i]));
            }
            return pairs;
        }

        // Parts are matched to the installed ones once here. ToLive does not match them again, because a second pass
        // could move an id that the first pass already moved onto another saved part.
        public static SerializableShip ReadSerializable(string json, PartCatalog catalog)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            SerializableShip serializable = JsonUtility.FromJson<SerializableShip>(json);
            if (serializable == null)
                return null;
            EnsureArrays(serializable);
            DropDuplicateHull(serializable);
            if (catalog != null)
                ResolveParts(serializable, catalog);
            ClearTargets(serializable);
            StripContents(serializable);
            return serializable;
        }

        public static IShipState FromShipJson(string json, PartCatalog catalog)
        {
            return ToLive(ReadSerializable(json, catalog));
        }

        public static IShipState ToLive(SerializableShip serializable)
        {
            if (serializable == null)
                return null;
            EnsureArrays(serializable);
            return Converter.ConvertToState(new[] { serializable }).States[0];
        }

        // Runs every ship of the schematic through the catalog, so the missing parts are known before anything is built.
        public static PartCatalog Survey(SchematicFile file)
        {
            PartCatalog catalog = PartCatalog.For(file);
            if (file == null)
                return catalog;
            try
            {
                ReadSerializable(file.shipJson, catalog);
                if (file.childShipJson != null)
                {
                    foreach (string child in file.childShipJson)
                        ReadSerializable(child, catalog);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not check the parts of " + file.name + ". " + ex.Message);
            }
            return catalog;
        }

        static void ResolveParts(SerializableShip ship, PartCatalog catalog)
        {
            ResolveArray(ship, "hullParts", "partId", true, catalog);
            ResolveArray(ship, "statefulFloatingParts", "settingsId", false, catalog);
            ResolveArray(ship, "statefulHullParts", "settingsId", false, catalog);
        }

        static void ResolveArray(SerializableShip ship, string arrayName, string idName, bool hullPanel, PartCatalog catalog)
        {
            FieldInfo field = ship.GetType().GetField(arrayName);
            if (field == null || !(field.GetValue(ship) is Array items) || items.Length == 0)
                return;
            Type elementType = items.GetType().GetElementType();
            FieldInfo idField = elementType.GetField(idName);
            if (idField == null)
                return;
            var kept = new List<object>(items.Length);
            bool changed = false;
            for (int i = 0; i < items.Length; i++)
            {
                object item = items.GetValue(i);
                if (item == null)
                    continue;
                ushort saved = Convert.ToUInt16(idField.GetValue(item));
                if (!catalog.Resolve(saved, hullPanel, out ushort id))
                {
                    changed = true;
                    continue;
                }
                if (id != saved)
                {
                    idField.SetValue(item, Convert.ChangeType(id, idField.FieldType));
                    changed = true;
                }
                kept.Add(item);
            }
            if (!changed)
                return;
            Array clean = Array.CreateInstance(elementType, kept.Count);
            for (int i = 0; i < kept.Count; i++)
                clean.SetValue(kept[i], i);
            field.SetValue(ship, clean);
        }

        public static bool Rename(SchematicFile file, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || file == null)
                return false;
            string old = file.name;
            file.name = name;
            if (!Write(file))
            {
                file.name = old;
                return false;
            }
            SortEntries();
            return true;
        }

        public static bool Delete(SchematicFile file)
        {
            if (file == null || string.IsNullOrEmpty(file.id))
                return false;
            try
            {
                string path = Path.Combine(Folder, file.id + ".json");
                if (File.Exists(path))
                    File.Delete(path);
                string preview = PreviewPath(file);
                if (File.Exists(preview))
                    File.Delete(preview);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MelonLogger.Warning("Could not delete " + file.name + ". " + ex.Message);
                return false;
            }
            Entries.RemoveAll(entry => entry.id == file.id);
            return true;
        }

        public static IShipState Restore(SchematicFile file)
        {
            PartCatalog catalog = PartCatalog.For(file);
            IShipState ship = Restore(file, catalog);
            if (catalog.HasMissing)
                MelonLogger.Warning(file.name + " uses parts that are not installed: " + catalog.MissingText() + ". They were left out.");
            return ship;
        }

        public static IShipState Restore(SchematicFile file, PartCatalog catalog)
        {
            return FromShipJson(file.shipJson, catalog);
        }

        // The fingerprint only changes with the ship, so a rename or a claim does not hash the ship again.
        static bool Write(SchematicFile file)
        {
            if (file == null || string.IsNullOrEmpty(file.id))
                return false;
            file.version = FileVersion;
            file.cableData = FormatCables(file.cables);
            if (string.IsNullOrEmpty(file.fingerprint))
                file.fingerprint = ComputeFingerprint(file.shipJson, file.cables, file.childShipJson, file.childCableData, file.linkData);
            try
            {
                File.WriteAllText(Path.Combine(Folder, file.id + ".json"), JsonUtility.ToJson(file, true));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MelonLogger.Warning("Could not write " + file.name + ". " + ex.Message);
                return false;
            }
        }

        static string FormatLinks(IEnumerable<LinkRecord> links)
        {
            var entries = new List<string>();
            if (links != null)
            {
                foreach (LinkRecord link in links)
                {
                    if (link == null)
                        continue;
                    entries.Add(link.type + "," + link.aShip + "," + link.aPart + "," + link.aSocket + "," + link.bShip + "," + link.bPart + "," + link.bSocket);
                }
            }
            entries.Sort(StringComparer.Ordinal);
            return string.Join(";", entries);
        }

        public static List<LinkRecord> ParseLinks(string text)
        {
            var links = new List<LinkRecord>();
            if (string.IsNullOrEmpty(text))
                return links;
            foreach (string entry in text.Split(';'))
            {
                string[] fields = entry.Split(',');
                if (fields.Length != 7)
                    continue;
                var values = new int[7];
                bool ok = true;
                for (int i = 0; i < 7 && ok; i++)
                    ok = int.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]);
                if (!ok)
                    continue;
                links.Add(new LinkRecord
                {
                    type = values[0],
                    aShip = values[1],
                    aPart = values[2],
                    aSocket = values[3],
                    bShip = values[4],
                    bPart = values[5],
                    bSocket = values[6]
                });
            }
            return links;
        }

        public static string FormatCables(CableRecord[] cables)
        {
            if (cables == null || cables.Length == 0)
                return "";
            var text = new StringBuilder();
            for (int i = 0; i < cables.Length; i++)
            {
                CableRecord cable = cables[i];
                if (cable == null)
                    continue;
                if (text.Length > 0)
                    text.Append(';');
                text.Append(cable.type);
                text.Append(',');
                text.Append(cable.aPart);
                text.Append(',');
                text.Append(cable.aSocket);
                text.Append(',');
                text.Append(cable.bPart);
                text.Append(',');
                text.Append(cable.bSocket);
            }
            return text.ToString();
        }

        public static CableRecord[] PreferCables(CableRecord[] cables, string text)
        {
            if (cables != null && cables.Length > 0)
                return cables;
            return ParseCables(text);
        }

        public static CableRecord[] ParseCables(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<CableRecord>();
            string[] parts = text.Split(';');
            var cables = new List<CableRecord>();
            var values = new int[5];
            for (int i = 0; i < parts.Length; i++)
            {
                string[] fields = parts[i].Split(',');
                if (fields.Length != 5)
                    continue;
                bool ok = true;
                for (int n = 0; n < 5 && ok; n++)
                    ok = int.TryParse(fields[n], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[n]);
                if (!ok)
                    continue;
                cables.Add(new CableRecord
                {
                    type = values[0],
                    aPart = values[1],
                    aSocket = values[2],
                    bPart = values[3],
                    bSocket = values[4]
                });
            }
            return cables.ToArray();
        }

        static string Fingerprint(SchematicFile file)
        {
            if (file == null)
                return "";
            if (string.IsNullOrEmpty(file.fingerprint))
                file.fingerprint = ComputeFingerprint(file.shipJson, file.cables, file.childShipJson, file.childCableData, file.linkData);
            return file.fingerprint;
        }

        static SchematicFile FindByFingerprint(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
                return null;
            foreach (SchematicFile entry in Entries)
            {
                if (entry != null && Fingerprint(entry) == fingerprint)
                    return entry;
            }
            return null;
        }

        public static SchematicPackage ToPackage(SchematicFile file)
        {
            // An old raw picture is several megabytes. It is left out; the receiving list draws its own.
            string preview = "";
            string thumb = PreviewPath(file);
            if (File.Exists(thumb))
            {
                byte[] data = File.ReadAllBytes(thumb);
                if (IsPng(data))
                    preview = Convert.ToBase64String(data);
            }
            return new SchematicPackage
            {
                version = 1,
                name = file.name,
                savedUtc = file.savedUtc,
                partCount = file.partCount,
                shipJson = file.shipJson,
                childShipJson = file.childShipJson ?? Array.Empty<string>(),
                childCableData = file.childCableData ?? Array.Empty<string>(),
                childPoses = file.childPoses ?? Array.Empty<string>(),
                cables = file.cables ?? Array.Empty<CableRecord>(),
                cableData = FormatCables(file.cables),
                linkData = file.linkData ?? "",
                partNames = file.partNames ?? "",
                fingerprint = Fingerprint(file),
                preview = preview
            };
        }

        public static string ToNetJson(SchematicFile file)
        {
            if (file == null || string.IsNullOrEmpty(file.shipJson))
                return "";
            SchematicPackage package = ToPackage(file);
            package.preview = "";
            return JsonUtility.ToJson(package);
        }

        public static SchematicFile FromNetJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            SchematicPackage package;
            try
            {
                package = JsonUtility.FromJson<SchematicPackage>(json);
            }
            catch (Exception)
            {
                return null;
            }
            if (package == null || string.IsNullOrEmpty(package.shipJson))
                return null;
            CableRecord[] cables = PreferCables(package.cables, package.cableData);
            return new SchematicFile
            {
                name = package.name,
                shipJson = package.shipJson,
                childShipJson = package.childShipJson ?? Array.Empty<string>(),
                childCableData = package.childCableData ?? Array.Empty<string>(),
                childPoses = package.childPoses ?? Array.Empty<string>(),
                cables = cables,
                cableData = FormatCables(cables),
                linkData = package.linkData ?? "",
                partNames = package.partNames ?? "",
                fingerprint = package.fingerprint ?? ""
            };
        }

        public static ImportOutcome ImportPackage(SchematicPackage package)
        {
            var outcome = new ImportOutcome();
            if (package == null || string.IsNullOrEmpty(package.shipJson))
            {
                outcome.Message = "That file is not a ship schematic.";
                return outcome;
            }

            EnsureLoaded();
            CableRecord[] cables = PreferCables(package.cables, package.cableData);
            string fingerprint = ComputeFingerprint(package.shipJson, cables, package.childShipJson, package.childCableData, package.linkData);
            SchematicFile existing = FindByFingerprint(fingerprint);
            if (existing != null)
            {
                if (ApplyPreview(existing, package.preview))
                {
                    outcome.Kind = ImportKind.Picture;
                    outcome.File = existing;
                    outcome.Message = existing.name + " is already in the library. Added its picture.";
                    return outcome;
                }
                outcome.Kind = ImportKind.Already;
                outcome.File = existing;
                outcome.Message = existing.name + " is already in the library.";
                return outcome;
            }

            var file = new SchematicFile
            {
                version = FileVersion,
                id = Guid.NewGuid().ToString("N"),
                name = UniqueName(package.name),
                savedUtc = string.IsNullOrEmpty(package.savedUtc) ? DateTime.UtcNow.ToString("o") : package.savedUtc,
                partCount = package.partCount,
                shipJson = package.shipJson,
                childShipJson = package.childShipJson ?? Array.Empty<string>(),
                childCableData = package.childCableData ?? Array.Empty<string>(),
                childPoses = package.childPoses ?? Array.Empty<string>(),
                cables = cables,
                cableData = FormatCables(cables),
                linkData = package.linkData ?? "",
                partNames = package.partNames ?? "",
                shipIds = Array.Empty<uint>(),
                shipSpaces = Array.Empty<uint>(),
                fingerprint = fingerprint
            };
            PartCatalog catalog;
            try
            {
                catalog = Survey(file);
                if (Restore(file, catalog) == null)
                {
                    outcome.Message = "That file is not a ship schematic.";
                    return outcome;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not read a schematic. " + ex.Message);
                outcome.Message = "That file is not a ship schematic.";
                return outcome;
            }

            if (!Write(file))
            {
                outcome.Message = "Could not save " + file.name + ".";
                return outcome;
            }
            Entries.Add(file);
            SortEntries();
            ApplyPreview(file, package.preview);
            outcome.Kind = ImportKind.Added;
            outcome.File = file;
            outcome.MissingParts = catalog.HasMissing;
            outcome.Message = catalog.HasMissing
                ? "Imported " + file.name + ". It uses parts that are not installed: " + catalog.MissingText() + "."
                : "Imported " + file.name + ".";
            return outcome;
        }

        static bool ApplyPreview(SchematicFile file, string preview)
        {
            if (file == null || string.IsNullOrEmpty(preview) || File.Exists(PreviewPath(file)))
                return false;
            byte[] data;
            try
            {
                data = Convert.FromBase64String(preview);
            }
            catch (FormatException)
            {
                return false;
            }
            if (!IsPng(data) || data.Length > MaxPictureBytes)
                return false;
            try
            {
                File.WriteAllBytes(PreviewPath(file), data);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MelonLogger.Warning("Could not save the picture of " + file.name + ". " + ex.Message);
                return false;
            }
        }

        static string UniqueName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
                name = NextName();
            if (!NameTaken(name))
                return name;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = name + " (" + i + ")";
                if (!NameTaken(candidate))
                    return candidate;
            }
            return NextName();
        }

        static string ComputeFingerprint(string shipJson, CableRecord[] cables, string[] children, string[] childCables, string links)
        {
            var builder = new StringBuilder();
            builder.Append(shipJson ?? "");
            builder.Append('\n');
            AppendSorted(builder, children);
            AppendSorted(builder, childCables);
            if (!string.IsNullOrEmpty(links))
                builder.Append(links).Append('\n');
            if (cables != null)
            {
                CableRecord[] ordered = cables.Where(cable => cable != null).ToArray();
                Array.Sort(ordered, CompareCables);
                foreach (CableRecord cable in ordered)
                {
                    builder.Append(cable.type).Append(',');
                    builder.Append(cable.aPart).Append(',');
                    builder.Append(cable.aSocket).Append(',');
                    builder.Append(cable.bPart).Append(',');
                    builder.Append(cable.bSocket).Append(';');
                }
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }

        static void AppendSorted(StringBuilder builder, string[] values)
        {
            if (values == null || values.Length == 0)
                return;
            var ordered = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrEmpty(values[i]))
                    ordered.Add(values[i]);
            }
            ordered.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ordered.Count; i++)
            {
                builder.Append(ordered[i]);
                builder.Append('\n');
            }
        }

        static int CompareCables(CableRecord left, CableRecord right)
        {
            int type = left.type.CompareTo(right.type);
            if (type != 0)
                return type;
            int aPart = left.aPart.CompareTo(right.aPart);
            if (aPart != 0)
                return aPart;
            int aSocket = left.aSocket.CompareTo(right.aSocket);
            if (aSocket != 0)
                return aSocket;
            int bPart = left.bPart.CompareTo(right.bPart);
            if (bPart != 0)
                return bPart;
            return left.bSocket.CompareTo(right.bSocket);
        }

        public static string SuggestName()
        {
            return NextName();
        }

        static bool NameTaken(string name)
        {
            foreach (SchematicFile entry in Entries)
            {
                if (entry != null && string.Equals(entry.name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // "Ship 7": the default names, numbered after the highest one in the library.
        static bool TryDefaultNumber(string name, out int number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            name = name.Trim();
            if (!name.StartsWith("ship ", StringComparison.OrdinalIgnoreCase))
                return false;
            return int.TryParse(name.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0;
        }

        static string NextName()
        {
            int number = 1;
            foreach (SchematicFile entry in Entries)
            {
                if (TryDefaultNumber(entry.name, out int used) && used >= number)
                    number = used + 1;
            }
            return "Ship " + number;
        }

        static string PreviewPath(SchematicFile file)
        {
            return Path.Combine(Folder, file.id + ".thumb");
        }

        // Without a picture the old one is removed, so the ship list draws the new ship instead of showing the old one.
        public static void SavePreview(SchematicFile file, byte[] png)
        {
            if (file == null || string.IsNullOrEmpty(file.id))
                return;
            try
            {
                if (png != null && png.Length > 0)
                    File.WriteAllBytes(PreviewPath(file), png);
                else if (File.Exists(PreviewPath(file)))
                    File.Delete(PreviewPath(file));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MelonLogger.Warning("Could not save the picture of " + file.name + ". " + ex.Message);
            }
        }

        // Pictures are PNG files. Version 1.0.1 and older wrote raw pixels, megabytes per ship; those load as missing,
        // and the ship list draws them again.
        public static Texture2D LoadPreview(SchematicFile file)
        {
            string path = PreviewPath(file);
            if (!File.Exists(path))
                return null;
            try
            {
                return DecodePng(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                return null;
            }
        }

        public static Texture2D DecodePng(byte[] data)
        {
            if (!IsPng(data) || LoadImage == null)
                return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (LoadImage(texture, data, false))
                return texture;
            Object.Destroy(texture);
            return null;
        }

        // Unity 6 added a ReadOnlySpan overload of ImageConversion.LoadImage. Any call to that method group makes the
        // compiler look for ReadOnlySpan in mscorlib, which the net472 reference assemblies do not have.
        static Func<Texture2D, byte[], bool, bool> _loadImage;
        static bool _loadImageBound;

        static Func<Texture2D, byte[], bool, bool> LoadImage
        {
            get
            {
                if (_loadImageBound)
                    return _loadImage;
                _loadImageBound = true;
                MethodInfo method = typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                if (method != null)
                    _loadImage = (Func<Texture2D, byte[], bool, bool>)Delegate.CreateDelegate(typeof(Func<Texture2D, byte[], bool, bool>), method);
                else
                    MelonLogger.Warning("ImageConversion.LoadImage(Texture2D, byte[], bool) is missing. Ship pictures will not load.");
                return _loadImage;
            }
        }

        static bool IsPng(byte[] data)
        {
            return data != null && data.Length > 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
        }

        public static bool PreviewLooksEmpty(Texture2D texture)
        {
            if (texture == null)
                return true;
            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0)
                return true;
            int step = Math.Max(1, pixels.Length / 4000);
            byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i += step)
            {
                Color32 pixel = pixels[i];
                if (pixel.a < 16)
                    continue;
                count++;
                if (pixel.r < minR) minR = pixel.r;
                if (pixel.r > maxR) maxR = pixel.r;
                if (pixel.g < minG) minG = pixel.g;
                if (pixel.g > maxG) maxG = pixel.g;
                if (pixel.b < minB) minB = pixel.b;
                if (pixel.b > maxB) maxB = pixel.b;
            }
            if (count == 0)
                return true;
            return maxR - minR < 18 && maxG - minG < 18 && maxB - minB < 18;
        }

        // Cables of every ship of the schematic. Both ends on one ship go to that ship; ends on two of its ships become
        // links between ships. A cable to a ship outside the schematic is left out.
        static CableRecord[][] ReadCables(List<TrackedShip> cluster, Dictionary<uint, int> index, Dictionary<uint, int> spaceIndex, out List<LinkRecord> links)
        {
            var states = new List<CableState>();
            try
            {
                var worlds = GameServices.Get<Saving.Interface.ServiceClient.IWorldStateProvider>();
                var world = worlds == null ? null : worlds.GetWorldState();
                CableData[] worldCables = world == null || world.CableStates == null ? null : world.CableStates.Cables;
                if (worldCables != null)
                {
                    foreach (CableData cable in worldCables)
                        states.Add(cable.State);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not read the world cable list. " + ex.Message);
            }
            CablesServerTracker server = GameServices.Find<CablesServerTracker>();
            CablesClientTracker client = GameServices.Find<CablesClientTracker>();
            foreach (TrackedShip ship in cluster)
            {
                ShipState live = ship.State;
                if (server != null)
                {
                    foreach (CableData cable in server.GetShipCables(live.Id))
                        states.Add(cable.State);
                    foreach (StatefulPart part in live.GetStatefulParts())
                    {
                        var context = new PartContext { ShipId = live.Id, PartId = part.Id };
                        foreach (CableData cable in server.GetCablesConnectedToPart(context))
                            states.Add(cable.State);
                    }
                }
                if (client != null)
                {
                    foreach (CableData cable in client.GetShipCables(live.Id))
                        states.Add(cable.State);
                }
            }

            var perShip = new List<CableRecord>[cluster.Count];
            for (int i = 0; i < perShip.Length; i++)
                perShip[i] = new List<CableRecord>();
            links = new List<LinkRecord>();
            var seen = new HashSet<string>();
            foreach (CableState state in states)
            {
                if (state.PlugA.IsHeldByPlayer || state.PlugB.IsHeldByPlayer)
                    continue;
                if (!Locate(state.PlugA.EntityId, index, spaceIndex, out int aShip) || !Locate(state.PlugB.EntityId, index, spaceIndex, out int bShip))
                    continue;
                int aPart = state.PlugA.PartSocket.PartId;
                int aSocket = state.PlugA.PartSocket.SocketId;
                int bPart = state.PlugB.PartSocket.PartId;
                int bSocket = state.PlugB.PartSocket.SocketId;
                string left = aPart + ":" + aSocket;
                string right = bPart + ":" + bSocket;
                if (aShip > bShip || (aShip == bShip && string.CompareOrdinal(left, right) > 0))
                {
                    (aShip, bShip) = (bShip, aShip);
                    (aPart, bPart) = (bPart, aPart);
                    (aSocket, bSocket) = (bSocket, aSocket);
                    (left, right) = (right, left);
                }
                if (!seen.Add((int)state.Type + "|" + aShip + ":" + left + "|" + bShip + ":" + right))
                    continue;
                if (aShip == bShip)
                {
                    perShip[aShip].Add(new CableRecord
                    {
                        type = (int)state.Type,
                        aPart = aPart,
                        aSocket = aSocket,
                        bPart = bPart,
                        bSocket = bSocket
                    });
                    continue;
                }
                links.Add(new LinkRecord
                {
                    type = (int)state.Type,
                    aShip = aShip,
                    aPart = aPart,
                    aSocket = aSocket,
                    bShip = bShip,
                    bPart = bPart,
                    bSocket = bSocket
                });
            }

            var result = new CableRecord[cluster.Count][];
            for (int i = 0; i < perShip.Length; i++)
                result[i] = perShip[i].ToArray();
            return result;
        }

        static bool Locate(uint entity, Dictionary<uint, int> index, Dictionary<uint, int> spaceIndex, out int ship)
        {
            return index.TryGetValue(entity, out ship) || spaceIndex.TryGetValue(entity, out ship);
        }

        // Fuel, fluids, charge and stored items stay with the world, not with the schematic. Most entries are structs,
        // so each changed copy is written back into its array.
        static void StripContents(SerializableShip ship)
        {
            if (ship == null)
                return;
            foreach (FieldInfo field in ShipArrays)
            {
                if (field.Name == "storageStates" || field.Name == "beaconStates")
                {
                    field.SetValue(ship, Array.CreateInstance(field.FieldType.GetElementType(), 0));
                    continue;
                }
                ContentFields contents = ContentFields.Of(field.FieldType.GetElementType());
                if (contents.Empty || !(field.GetValue(ship) is Array items))
                    continue;
                for (int i = 0; i < items.Length; i++)
                {
                    object item = items.GetValue(i);
                    if (item != null && contents.Clear(item))
                        items.SetValue(item, i);
                }
            }
        }

        static readonly FieldInfo[] ShipArrays = typeof(SerializableShip).GetFields().Where(field => field.FieldType.IsArray && field.Name != "rooms").ToArray();

        // The fields of one part state type that hold contents: amounts, a warp flag and item slots.
        sealed class ContentFields
        {
            static readonly Dictionary<Type, ContentFields> Known = new Dictionary<Type, ContentFields>();

            readonly List<FieldInfo> _amounts = new List<FieldInfo>();
            readonly List<FieldInfo> _flags = new List<FieldInfo>();
            readonly List<FieldInfo> _slots = new List<FieldInfo>();
            readonly List<FieldInfo> _slotArrays = new List<FieldInfo>();

            public bool Empty => _amounts.Count + _flags.Count + _slots.Count + _slotArrays.Count == 0;

            public static ContentFields Of(Type type)
            {
                if (Known.TryGetValue(type, out ContentFields known))
                    return known;
                var fields = new ContentFields();
                foreach (FieldInfo field in type.GetFields())
                {
                    switch (field.Name)
                    {
                        case "fluidQuantity":
                        case "charge":
                        case "energy":
                        case "storedEnergy":
                        case "partialConsumption":
                            if (field.FieldType == typeof(float) || field.FieldType == typeof(double))
                                fields._amounts.Add(field);
                            break;
                        case "isWarping":
                            if (field.FieldType == typeof(bool))
                                fields._flags.Add(field);
                            break;
                        case "slot":
                            fields._slots.Add(field);
                            break;
                        case "slots":
                            if (field.FieldType.IsArray)
                                fields._slotArrays.Add(field);
                            break;
                    }
                }
                Known[type] = fields;
                return fields;
            }

            public bool Clear(object item)
            {
                foreach (FieldInfo field in _amounts)
                    field.SetValue(item, field.FieldType == typeof(float) ? (object)0f : 0d);
                foreach (FieldInfo field in _flags)
                    field.SetValue(item, false);
                foreach (FieldInfo field in _slots)
                    field.SetValue(item, EmptySlot(field.FieldType));
                foreach (FieldInfo field in _slotArrays)
                {
                    if (!(field.GetValue(item) is Array slots))
                        continue;
                    Type slotType = slots.GetType().GetElementType();
                    Array empty = Array.CreateInstance(slotType, slots.Length);
                    for (int i = 0; i < slots.Length; i++)
                        empty.SetValue(EmptySlot(slotType), i);
                    field.SetValue(item, empty);
                }
                return !Empty;
            }

            static object EmptySlot(Type type)
            {
                try
                {
                    return Activator.CreateInstance(type);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        static void ClearTargets(SerializableShip ship)
        {
            ClearField(ship, "targeterStates", "hasTarget", false);
        }

        // A vanilla rotor can have a live joint while RotorState.AnchoredShipId is 0. Write the joint target so place
        // can join the head again.
        static void FillRotorTargets(SerializableShip serial, TrackedShip ship)
        {
            ShipsJointsChangeProvider joints = Joints();
            if (serial == null || joints == null || ship == null || ship.State == null)
                return;
            ChangeEntries(serial, "rotorStates", item =>
            {
                FieldInfo anchored = item.GetType().GetField("anchoredShipId");
                FieldInfo partId = item.GetType().GetField("partId");
                FieldInfo offset = item.GetType().GetField("anchoredShipOffset");
                if (anchored == null || partId == null)
                    return false;
                if ((uint)anchored.GetValue(item) != 0)
                    return false;
                ushort id = Convert.ToUInt16(partId.GetValue(item));
                foreach (RotorJointData joint in joints.GetShipRotorJoints(ship.State.Id))
                {
                    if (joint.RotorPart.ShipId != ship.State.Id || joint.RotorPart.PartId != id || joint.OtherShipId == 0)
                        continue;
                    anchored.SetValue(item, joint.OtherShipId);
                    if (offset != null)
                        offset.SetValue(item, joint.OtherShipPosition);
                    return true;
                }
                return false;
            });
        }

        // Rotor, piston and docking links get the id that resolve returns for the ship they point to; 0 cuts the link.
        public static void RetargetLinks(SerializableShip ship, Func<uint, uint> resolve)
        {
            if (ship == null)
                return;
            ChangeEntries(ship, "rotorStates", item =>
            {
                FieldInfo anchored = item.GetType().GetField("anchoredShipId");
                if (anchored == null)
                    return false;
                uint old = (uint)anchored.GetValue(item);
                uint target = old == 0 ? 0u : resolve(old);
                if (target == old)
                    return false;
                anchored.SetValue(item, target);
                return true;
            });
            ChangeEntries(ship, "dockingDoorStates", item =>
            {
                FieldInfo docked = item.GetType().GetField("isDocked");
                FieldInfo shipId = item.GetType().GetField("dockedShipId");
                FieldInfo partId = item.GetType().GetField("dockedPartId");
                if (docked == null || shipId == null)
                    return false;
                bool isDocked = (bool)docked.GetValue(item);
                uint old = (uint)shipId.GetValue(item);
                if (!isDocked && old == 0)
                    return false;
                uint target = isDocked && old != 0 ? resolve(old) : 0u;
                if (target != 0)
                {
                    if (target == old)
                        return false;
                    shipId.SetValue(item, target);
                    return true;
                }
                docked.SetValue(item, false);
                shipId.SetValue(item, 0u);
                if (partId != null)
                    partId.SetValue(item, Convert.ChangeType(0, partId.FieldType));
                return true;
            });
        }

        static void ChangeEntries(object owner, string arrayField, Func<object, bool> change)
        {
            FieldInfo field = owner.GetType().GetField(arrayField);
            if (field == null || !(field.GetValue(owner) is Array items))
                return;
            for (int i = 0; i < items.Length; i++)
            {
                object item = items.GetValue(i);
                if (item != null && change(item))
                    items.SetValue(item, i);
            }
        }

        static readonly FieldInfo HullArray = typeof(SerializableShip).GetField("hullParts");
        static readonly FieldInfo[] HullCell = HullArray == null
            ? new FieldInfo[0]
            : new[] { "x", "y", "z", "voxelPartTypeId" }.Select(name => HullArray.FieldType.GetElementType().GetField(name)).ToArray();

        // The game keys hull plates by cell and plate type; a second plate on the same key fails the whole ship.
        static void DropDuplicateHull(SerializableShip ship)
        {
            if (ship == null || HullArray == null || HullCell.Length != 4 || HullCell.Any(field => field == null))
                return;
            if (!(HullArray.GetValue(ship) is Array items) || items.Length < 2)
                return;
            var seen = new HashSet<(int, int, int, int)>();
            var kept = new List<object>(items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                object item = items.GetValue(i);
                if (item == null)
                    continue;
                var key = (CellValue(0, item), CellValue(1, item), CellValue(2, item), CellValue(3, item));
                if (seen.Add(key))
                    kept.Add(item);
            }
            if (kept.Count == items.Length)
                return;
            Array clean = Array.CreateInstance(items.GetType().GetElementType(), kept.Count);
            for (int i = 0; i < kept.Count; i++)
                clean.SetValue(kept[i], i);
            HullArray.SetValue(ship, clean);
            MelonLogger.Warning("Skipped " + Plural(items.Length - kept.Count, "hull plate") + " that occupied the same cell.");
        }

        static int CellValue(int index, object item)
        {
            object value = HullCell[index].GetValue(item);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        static void ClearField(object owner, string arrayField, string valueField, object value)
        {
            FieldInfo field = owner.GetType().GetField(arrayField);
            if (field == null || !(field.GetValue(owner) is Array items))
                return;
            for (int i = 0; i < items.Length; i++)
            {
                object item = items.GetValue(i);
                FieldInfo target = item == null ? null : item.GetType().GetField(valueField);
                if (target == null || target.FieldType != value.GetType())
                    continue;
                target.SetValue(item, value);
                items.SetValue(item, i);
            }
        }

        static void EnsureArrays(object target)
        {
            if (target == null)
                return;
            foreach (FieldInfo field in target.GetType().GetFields())
            {
                if (field.FieldType.IsArray && field.GetValue(target) == null)
                    field.SetValue(target, Array.CreateInstance(field.FieldType.GetElementType(), 0));
            }
        }

        // Files from before ship pairs existed get the pair of the ship they were saved from, unless another schematic
        // owns it now. Runs once per file: the write marks it as the current version.
        static void BackfillIdentity(SchematicFile entry)
        {
            if (entry == null || entry.version >= FileVersion)
                return;
            if (TryReadPair(entry.shipJson, out uint id, out uint space) && FindByShip(id, space) == null)
            {
                entry.shipIds = AppendShip(entry.shipIds, id);
                entry.shipSpaces = AppendShip(entry.shipSpaces, space);
            }
            Write(entry);
        }

        static bool TryReadPair(string shipJson, out uint id, out uint space)
        {
            id = 0;
            space = 0;
            if (string.IsNullOrEmpty(shipJson))
                return false;
            Match idMatch = Regex.Match(shipJson, "\"id\"\\s*:\\s*(\\d+)");
            Match spaceMatch = Regex.Match(shipJson, "\"relativeSpaceId\"\\s*:\\s*(\\d+)");
            if (!idMatch.Success || !spaceMatch.Success)
                return false;
            return uint.TryParse(idMatch.Groups[1].Value, out id)
                && uint.TryParse(spaceMatch.Groups[1].Value, out space)
                && id != 0
                && space != 0;
        }

        static bool ContainsPair(SchematicFile file, uint shipId, uint relativeSpaceId)
        {
            if (file.shipIds == null || file.shipSpaces == null)
                return false;
            int count = Math.Min(file.shipIds.Length, file.shipSpaces.Length);
            for (int i = 0; i < count; i++)
            {
                if (file.shipIds[i] == shipId && file.shipSpaces[i] == relativeSpaceId)
                    return true;
            }
            return false;
        }

        static bool RemovePair(SchematicFile file, uint shipId, uint relativeSpaceId)
        {
            if (!ContainsPair(file, shipId, relativeSpaceId))
                return false;
            var ids = new List<uint>();
            var spaces = new List<uint>();
            int count = Math.Min(file.shipIds.Length, file.shipSpaces.Length);
            for (int i = 0; i < count; i++)
            {
                if (file.shipIds[i] == shipId && file.shipSpaces[i] == relativeSpaceId)
                    continue;
                ids.Add(file.shipIds[i]);
                spaces.Add(file.shipSpaces[i]);
            }
            file.shipIds = ids.ToArray();
            file.shipSpaces = spaces.ToArray();
            return true;
        }

        static uint[] AppendShip(uint[] ids, uint shipId)
        {
            var list = new List<uint>();
            if (ids != null)
                list.AddRange(ids);
            list.Add(shipId);
            return list.ToArray();
        }
    }
}
