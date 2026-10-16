using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Core.Values;
using FishNet;
using FishNet.Managing;
using HarmonyLib;
using Managers.Interface;
using MelonLoader;
using Players.Services;
using Saving.Interface.Model;
using Saving.Services;
using Ships.Visuals;
using UnityEngine;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using WorldTracking.Interface.Services;
using WorldTracking.Interface.Values;
using WorldTracking.Spaces;
using WorldTracking.Spaces.Model;

namespace ShipSchematics
{
    [Serializable]
    public class BlueprintBinRecord
    {
        public int index;
        public uint itemId;
        public int quantity;
    }

    [Serializable]
    public class BlueprintRecord
    {
        public string name;
        public string shipJson;
        public string[] childShipJson;
        public string[] childCableData;
        public string[] childPoses;
        public string linkData;
        public string partNames;
        public CableRecord[] cables;
        public string spaceKind;
        public string planetId;
        public string cx;
        public string cy;
        public string cz;
        public float px;
        public float py;
        public float pz;
        public float rx;
        public float ry;
        public float rz;
        public float rw;
        public int starX;
        public int starY;
        public int starZ;
        public int systemX;
        public int systemY;
        public int systemZ;
        public float cellX;
        public float cellY;
        public float cellZ;
        public BlueprintBinRecord[] bins;
    }

    public static class BlueprintWorld
    {
        const string FolderName = "SchematicBlueprints";
        const int MetaVersion = 2;

        static AccessTools.FieldRef<SpacesClientTracker, Dictionary<uint, ISpaceClientState>> _spaces;
        static bool _spacesBound;
        static bool _restored;
        static float _waited;

        public static void SceneLoaded()
        {
            _restored = false;
            _waited = 0f;
        }

        public static void Tick()
        {
            if (_restored)
                return;
            _restored = TryRestore();
        }

        public static void Write()
        {
            if (!IsHost())
                return;
            string folder = FolderForCurrentWorld();
            if (folder == null)
                return;

            var sites = new List<SchematicBuildSite>();
            foreach (SchematicBuildSite site in SchematicBuildSite.Active)
                sites.Add(site);
            SchematicBuildSite[] found = UnityEngine.Object.FindObjectsByType<SchematicBuildSite>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < found.Length; i++)
            {
                if (!sites.Contains(found[i]))
                    sites.Add(found[i]);
            }

            string temp = folder + ".writing";
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);
                var records = new List<BlueprintRecord>();
                for (int i = 0; i < sites.Count; i++)
                {
                    BlueprintRecord record = Describe(sites[i]);
                    if (record == null || string.IsNullOrEmpty(record.shipJson))
                    {
                        MelonLogger.Warning("Skipped a blueprint while saving.");
                        continue;
                    }
                    File.WriteAllText(Path.Combine(temp, records.Count + ".ship"), record.shipJson);
                    File.WriteAllText(Path.Combine(temp, records.Count + ".meta"), MetaText(record));
                    WriteSubs(Path.Combine(temp, records.Count + ".subs"), record.childShipJson, record.childCableData);
                    records.Add(record);
                }
                File.WriteAllText(Path.Combine(temp, "count.txt"), records.Count.ToString(CultureInfo.InvariantCulture));
                ReplaceFolder(folder, temp);
                string stale = Path.Combine(Path.GetDirectoryName(folder) ?? "", "schematic-blueprints.json");
                if (File.Exists(stale))
                    File.Delete(stale);
                MelonLogger.Msg("Saved " + records.Count + " blueprint" + (records.Count == 1 ? "" : "s") + ".");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not save blueprints. " + ex.Message);
                try
                {
                    if (Directory.Exists(temp))
                        Directory.Delete(temp, true);
                }
                catch (Exception)
                {
                }
            }
        }

        static bool TryRestore()
        {
            if (NetworkManager.Instances.Count == 0)
                return false;
            if (!InstanceFinder.IsServerStarted)
                return true;
            string folder = FolderForCurrentWorld();
            if (folder == null)
                return false;
            PlayersClientTracker players = GameServices.Get<PlayersClientTracker>();
            if (players == null || players.LocalPlayer == null || ClientOrigin.LocalVisualSpace == null)
                return false;
            string countPath = Path.Combine(folder, "count.txt");
            if (!File.Exists(countPath))
                return true;
            if (!int.TryParse(File.ReadAllText(countPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
                return true;

            var records = new List<BlueprintRecord>();
            for (int i = 0; i < count; i++)
            {
                string shipPath = Path.Combine(folder, i + ".ship");
                string metaPath = Path.Combine(folder, i + ".meta");
                if (!File.Exists(shipPath) || !File.Exists(metaPath))
                    continue;
                BlueprintRecord record = ReadMeta(File.ReadAllText(metaPath), File.ReadAllText(shipPath));
                string subsPath = Path.Combine(folder, i + ".subs");
                if (record != null && File.Exists(subsPath))
                    ReadSubs(record, File.ReadAllText(subsPath));
                if (record != null)
                    records.Add(record);
            }
            if (records.Count == 0)
                return true;

            _waited += Time.unscaledDeltaTime;
            if (!AnchorsReady(records) && _waited < 8f)
                return false;

            int placed = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (Spawn(records[i]))
                    placed++;
            }
            if (placed == 0 && _waited < 8f)
                return false;
            if (placed == 0)
                MelonLogger.Warning("The blueprint save was found, but none of the ships could be rebuilt.");
            else
                MelonLogger.Msg("Restored " + placed + " blueprint" + (placed == 1 ? "" : "s") + ".");
            return true;
        }

        static bool AnchorsReady(List<BlueprintRecord> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i] != null && records[i].spaceKind == "planet" && FindPlanet(records[i].planetId) == null)
                    return false;
            }
            return true;
        }

        static bool Spawn(BlueprintRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.shipJson))
                return false;
            ISpace space = Resolve(record);
            if (space == null)
                return false;
            var source = new SchematicFile
            {
                name = string.IsNullOrEmpty(record.name) ? "Ship" : record.name,
                shipJson = record.shipJson,
                childShipJson = record.childShipJson,
                childCableData = record.childCableData,
                childPoses = record.childPoses,
                linkData = record.linkData,
                partNames = record.partNames,
                cables = record.cables ?? Array.Empty<CableRecord>()
            };
            Ships.Interface.Model.State.IShipState ship;
            try
            {
                ship = SchematicLibrary.Restore(source);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not restore a blueprint. " + ex.Message);
                return false;
            }
            if (ship == null)
                return false;
            ShipPartVisuals visuals;
            try
            {
                visuals = SchematicPlacer.CreateHologram(ship);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not rebuild a blueprint hologram. " + ex.Message);
                return false;
            }
            if (visuals == null)
                return false;
            Vector3 local = new Vector3(record.px, record.py, record.pz);
            Quaternion rotation = new Quaternion(record.rx, record.ry, record.rz, record.rw);
            if (rotation == new Quaternion(0f, 0f, 0f, 0f))
                rotation = Quaternion.identity;
            SchematicBuildSite site = SchematicBuildSite.Create(ship, source, space, local, rotation, visuals);
            if (site == null)
                return false;
            if (record.bins != null)
            {
                for (int i = 0; i < record.bins.Length; i++)
                {
                    BlueprintBinRecord bin = record.bins[i];
                    if (bin != null)
                        site.RestoreBin(bin.index, bin.itemId, bin.quantity);
                }
            }
            site.CompleteIfReady();
            return true;
        }

        static BlueprintRecord Describe(SchematicBuildSite site)
        {
            if (site == null || site.Source == null || string.IsNullOrEmpty(site.Source.shipJson))
            {
                MelonLogger.Warning("A blueprint has no saved ship.");
                return null;
            }
            ISpace space = site.AnchorSpace != null ? site.AnchorSpace : ClientOrigin.LocalVisualSpace;
            Vector3 local = site.AnchorSpace != null ? site.AnchorPosition : site.transform.position;
            Quaternion rotation = site.AnchorSpace != null ? site.AnchorRotation : site.transform.rotation;
            if (space == null)
            {
                MelonLogger.Warning("A blueprint has no world space.");
                return null;
            }
            var record = new BlueprintRecord
            {
                name = site.Source.name,
                shipJson = site.Source.shipJson,
                childShipJson = site.Source.childShipJson,
                childCableData = site.Source.childCableData,
                childPoses = site.Source.childPoses,
                linkData = site.Source.linkData,
                partNames = site.Source.partNames,
                cables = site.Source.cables,
                px = local.x,
                py = local.y,
                pz = local.z,
                rx = rotation.x,
                ry = rotation.y,
                rz = rotation.z,
                rw = rotation.w
            };
            if (space is PlanetSurfaceSpace planet)
            {
                record.spaceKind = "planet";
                record.planetId = planet.PlanetId.ToString(CultureInfo.InvariantCulture);
                record.cx = planet.Center.x.ToString(CultureInfo.InvariantCulture);
                record.cy = planet.Center.y.ToString(CultureInfo.InvariantCulture);
                record.cz = planet.Center.z.ToString(CultureInfo.InvariantCulture);
            }
            else if (space is FixedWorldSpace pinned)
            {
                record.spaceKind = "fixed";
                PreciseWorldPosition origin = pinned.Position;
                record.starX = origin.worldCell.starCell.x;
                record.starY = origin.worldCell.starCell.y;
                record.starZ = origin.worldCell.starCell.z;
                record.systemX = origin.worldCell.systemCell.x;
                record.systemY = origin.worldCell.systemCell.y;
                record.systemZ = origin.worldCell.systemCell.z;
                record.cellX = origin.positionWithinSystemCell.x;
                record.cellY = origin.positionWithinSystemCell.y;
                record.cellZ = origin.positionWithinSystemCell.z;
            }
            else
            {
                MelonLogger.Warning("Blueprint space is " + space.GetType().Name + ".");
                return null;
            }

            var bins = new List<BlueprintBinRecord>();
            for (int i = 0; i < site.BinCount; i++)
            {
                SchematicBuildSite.Bin bin = site.BinAt(i);
                if (bin.Quantity <= 0)
                    continue;
                bins.Add(new BlueprintBinRecord { index = i, itemId = bin.ItemId, quantity = bin.Quantity });
            }
            record.bins = bins.ToArray();
            return record;
        }

        static ISpace Resolve(BlueprintRecord record)
        {
            if (record.spaceKind == "planet")
            {
                if (!ulong.TryParse(record.planetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong planetId))
                    return null;
                if (!double.TryParse(record.cx, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(record.cy, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(record.cz, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    return null;
                return new PlanetSurfaceSpace
                {
                    PlanetId = planetId,
                    Center = new Vector3Double(x, y, z)
                };
            }
            if (record.spaceKind == "fixed")
            {
                var cell = new WorldCell(new Vector3Int(record.starX, record.starY, record.starZ), new Vector3Int(record.systemX, record.systemY, record.systemZ));
                return new FixedWorldSpace
                {
                    Position = new PreciseWorldPosition(cell, new Vector3(record.cellX, record.cellY, record.cellZ))
                };
            }
            return null;
        }

        static ISpace FindPlanet(string planetId)
        {
            if (string.IsNullOrEmpty(planetId)
                || !ulong.TryParse(planetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id))
                return null;
            Dictionary<uint, ISpaceClientState> spaces = Spaces();
            if (spaces == null)
                return null;
            foreach (ISpaceClientState state in spaces.Values)
            {
                if (state != null && state.GenericSpace is PlanetSurfaceSpace planet && planet.PlanetId == id)
                    return planet;
            }
            return null;
        }

        static Dictionary<uint, ISpaceClientState> Spaces()
        {
            if (!_spacesBound)
            {
                _spacesBound = true;
                try
                {
                    _spaces = AccessTools.FieldRefAccess<SpacesClientTracker, Dictionary<uint, ISpaceClientState>>("_spaces");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Could not read the world's spaces. " + ex.Message);
                }
            }
            SpacesClientTracker tracker = GameServices.Find<SpacesClientTracker>();
            if (tracker == null || _spaces == null)
                return null;
            try
            {
                return _spaces(tracker);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void WriteSubs(string path, string[] ships, string[] cables)
        {
            if (ships == null || ships.Length == 0)
                return;
            var text = new StringBuilder();
            text.Append(ships.Length.ToString(CultureInfo.InvariantCulture));
            text.Append('\n');
            for (int i = 0; i < ships.Length; i++)
            {
                text.Append(ships[i] ?? "");
                text.Append('\n');
                string cable = cables != null && i < cables.Length ? cables[i] : "";
                text.Append(cable ?? "");
                text.Append('\n');
            }
            File.WriteAllText(path, text.ToString());
        }

        static void ReadSubs(BlueprintRecord record, string text)
        {
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            if (lines.Length == 0 || !int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
                return;
            var ships = new string[count];
            var cables = new string[count];
            for (int i = 0; i < count; i++)
            {
                int shipLine = 1 + i * 2;
                int cableLine = shipLine + 1;
                if (shipLine >= lines.Length)
                    break;
                ships[i] = lines[shipLine];
                cables[i] = cableLine < lines.Length ? lines[cableLine] : "";
            }
            record.childShipJson = ships;
            record.childCableData = cables;
        }

        static string MetaText(BlueprintRecord record)
        {
            var text = new StringBuilder();
            Line(text, "version", MetaVersion.ToString(CultureInfo.InvariantCulture));
            Line(text, "name", record.name);
            Line(text, "spaceKind", record.spaceKind);
            Line(text, "planetId", record.planetId);
            Line(text, "cx", record.cx);
            Line(text, "cy", record.cy);
            Line(text, "cz", record.cz);
            Line(text, "px", record.px.ToString(CultureInfo.InvariantCulture));
            Line(text, "py", record.py.ToString(CultureInfo.InvariantCulture));
            Line(text, "pz", record.pz.ToString(CultureInfo.InvariantCulture));
            Line(text, "rx", record.rx.ToString(CultureInfo.InvariantCulture));
            Line(text, "ry", record.ry.ToString(CultureInfo.InvariantCulture));
            Line(text, "rz", record.rz.ToString(CultureInfo.InvariantCulture));
            Line(text, "rw", record.rw.ToString(CultureInfo.InvariantCulture));
            Line(text, "starX", record.starX.ToString(CultureInfo.InvariantCulture));
            Line(text, "starY", record.starY.ToString(CultureInfo.InvariantCulture));
            Line(text, "starZ", record.starZ.ToString(CultureInfo.InvariantCulture));
            Line(text, "systemX", record.systemX.ToString(CultureInfo.InvariantCulture));
            Line(text, "systemY", record.systemY.ToString(CultureInfo.InvariantCulture));
            Line(text, "systemZ", record.systemZ.ToString(CultureInfo.InvariantCulture));
            Line(text, "cellX", record.cellX.ToString(CultureInfo.InvariantCulture));
            Line(text, "cellY", record.cellY.ToString(CultureInfo.InvariantCulture));
            Line(text, "cellZ", record.cellZ.ToString(CultureInfo.InvariantCulture));
            var bins = new StringBuilder();
            if (record.bins != null)
            {
                for (int i = 0; i < record.bins.Length; i++)
                {
                    if (i > 0)
                        bins.Append(';');
                    BlueprintBinRecord bin = record.bins[i];
                    bins.Append(bin.index.ToString(CultureInfo.InvariantCulture));
                    bins.Append(',');
                    bins.Append(bin.itemId.ToString(CultureInfo.InvariantCulture));
                    bins.Append(',');
                    bins.Append(bin.quantity.ToString(CultureInfo.InvariantCulture));
                }
            }
            Line(text, "bins", bins.ToString());
            var cables = new StringBuilder();
            if (record.cables != null)
            {
                for (int i = 0; i < record.cables.Length; i++)
                {
                    if (i > 0)
                        cables.Append(';');
                    CableRecord cable = record.cables[i];
                    if (cable == null)
                        continue;
                    cables.Append(cable.type.ToString(CultureInfo.InvariantCulture));
                    cables.Append(',');
                    cables.Append(cable.aPart.ToString(CultureInfo.InvariantCulture));
                    cables.Append(',');
                    cables.Append(cable.aSocket.ToString(CultureInfo.InvariantCulture));
                    cables.Append(',');
                    cables.Append(cable.bPart.ToString(CultureInfo.InvariantCulture));
                    cables.Append(',');
                    cables.Append(cable.bSocket.ToString(CultureInfo.InvariantCulture));
                }
            }
            Line(text, "cables", cables.ToString());
            Line(text, "links", record.linkData);
            Line(text, "parts", record.partNames);
            Line(text, "poses", record.childPoses == null ? "" : string.Join(";", record.childPoses));
            return text.ToString();
        }

        static void Line(StringBuilder text, string key, string value)
        {
            text.Append(key);
            text.Append('=');
            text.Append(Escape(value ?? ""));
            text.Append('\n');
        }

        static BlueprintRecord ReadMeta(string meta, string shipJson)
        {
            var map = new Dictionary<string, string>();
            string[] lines = (meta ?? "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                int cut = line.IndexOf('=');
                if (cut <= 0)
                    continue;
                map[line.Substring(0, cut)] = line.Substring(cut + 1);
            }
            bool escaped = Int(map, "version") >= 2;
            var record = new BlueprintRecord
            {
                name = Read(map, "name", escaped),
                shipJson = shipJson,
                spaceKind = Read(map, "spaceKind", escaped),
                planetId = Read(map, "planetId", escaped),
                cx = Read(map, "cx", escaped),
                cy = Read(map, "cy", escaped),
                cz = Read(map, "cz", escaped)
            };
            record.px = Float(map, "px");
            record.py = Float(map, "py");
            record.pz = Float(map, "pz");
            record.rx = Float(map, "rx");
            record.ry = Float(map, "ry");
            record.rz = Float(map, "rz");
            record.rw = Float(map, "rw");
            record.starX = Int(map, "starX");
            record.starY = Int(map, "starY");
            record.starZ = Int(map, "starZ");
            record.systemX = Int(map, "systemX");
            record.systemY = Int(map, "systemY");
            record.systemZ = Int(map, "systemZ");
            record.cellX = Float(map, "cellX");
            record.cellY = Float(map, "cellY");
            record.cellZ = Float(map, "cellZ");
            record.bins = ParseBins(Read(map, "bins", escaped));
            record.cables = ParseCables(Read(map, "cables", escaped));
            record.linkData = Read(map, "links", escaped);
            record.partNames = Read(map, "parts", escaped);
            string poses = Read(map, "poses", escaped);
            record.childPoses = poses.Length == 0 ? null : poses.Split(';');
            return record;
        }

        static BlueprintBinRecord[] ParseBins(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<BlueprintBinRecord>();
            string[] parts = text.Split(';');
            var bins = new List<BlueprintBinRecord>();
            for (int i = 0; i < parts.Length; i++)
            {
                string[] fields = parts[i].Split(',');
                if (fields.Length != 3)
                    continue;
                if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    continue;
                if (!uint.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint itemId))
                    continue;
                if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity))
                    continue;
                bins.Add(new BlueprintBinRecord { index = index, itemId = itemId, quantity = quantity });
            }
            return bins.ToArray();
        }

        static CableRecord[] ParseCables(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<CableRecord>();
            string[] parts = text.Split(';');
            var cables = new List<CableRecord>();
            for (int i = 0; i < parts.Length; i++)
            {
                string[] fields = parts[i].Split(',');
                if (fields.Length != 5)
                    continue;
                cables.Add(new CableRecord
                {
                    type = IntText(fields[0]),
                    aPart = IntText(fields[1]),
                    aSocket = IntText(fields[2]),
                    bPart = IntText(fields[3]),
                    bSocket = IntText(fields[4])
                });
            }
            return cables.ToArray();
        }

        static string Read(Dictionary<string, string> map, string key, bool escaped)
        {
            string value;
            if (!map.TryGetValue(key, out value))
                return "";
            return escaped ? Unescape(value) : value;
        }

        static float Float(Dictionary<string, string> map, string key)
        {
            float value;
            float.TryParse(Read(map, key, false), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return value;
        }

        static int Int(Dictionary<string, string> map, string key)
        {
            return IntText(Read(map, key, false));
        }

        static int IntText(string text)
        {
            int value;
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return value;
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var text = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    text.Append(value[i]);
                    continue;
                }
                char next = value[i + 1];
                if (next == 'n')
                    text.Append('\n');
                else if (next == 'r')
                    text.Append('\r');
                else if (next == '\\')
                    text.Append('\\');
                else
                {
                    text.Append(value[i]);
                    continue;
                }
                i++;
            }
            return text.ToString();
        }

        static void ReplaceFolder(string folder, string temp)
        {
            string backup = folder + ".old";
            if (Directory.Exists(backup))
                Directory.Delete(backup, true);
            if (Directory.Exists(folder))
                Directory.Move(folder, backup);
            try
            {
                Directory.Move(temp, folder);
            }
            catch (Exception)
            {
                if (Directory.Exists(backup) && !Directory.Exists(folder))
                    Directory.Move(backup, folder);
                throw;
            }
            if (Directory.Exists(backup))
                Directory.Delete(backup, true);
        }

        static bool IsHost()
        {
            return NetworkManager.Instances.Count > 0 && InstanceFinder.IsServerStarted;
        }

        static string FolderForCurrentWorld()
        {
            string save = MainGameSceneLoadData.SaveFileFullName;
            if (string.IsNullOrEmpty(save))
                return null;
            string directory = Path.GetDirectoryName(save);
            if (string.IsNullOrEmpty(directory))
                return null;
            return Path.Combine(directory, FolderName);
        }
    }

    [HarmonyPatch(typeof(GameSaver), "SaveGame", new Type[] { typeof(SaveGameParams) })]
    static class BlueprintSavePatch
    {
        static void Postfix()
        {
            try
            {
                BlueprintWorld.Write();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not save blueprints. " + ex.Message);
            }
        }
    }
}
