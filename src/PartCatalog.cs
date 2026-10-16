using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using UnityEngine;

namespace ShipSchematics
{
    // Matches the part ids saved in a schematic to the parts installed now. A part added by a mod is found by its
    // asset name when that mod gives it another id, and a part whose mod is missing is left out of the ship.
    public sealed class PartCatalog
    {
        readonly Dictionary<ushort, string> _savedNames = new Dictionary<ushort, string>();
        readonly Dictionary<ushort, string> _savedLabels = new Dictionary<ushort, string>();
        readonly SortedSet<string> _missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool HasMissing => _missing.Count > 0;

        PartCatalog(string partNames)
        {
            if (string.IsNullOrEmpty(partNames))
                return;
            foreach (string entry in partNames.Split(';'))
            {
                string[] fields = entry.Split('|');
                if (fields.Length < 2 || !ushort.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id))
                    continue;
                _savedNames[id] = fields[1];
                if (fields.Length > 2 && fields[2].Length > 0)
                    _savedLabels[id] = fields[2];
            }
        }

        public static PartCatalog For(SchematicFile file)
        {
            return new PartCatalog(file == null ? null : file.partNames);
        }

        // Mods add their parts while a world loads, so the part tables are read again in each scene.
        public static void SceneLoaded()
        {
            InstalledParts.Clear();
        }

        public static string Format(IEnumerable<PartSettings> parts)
        {
            var seen = new SortedDictionary<ushort, string>();
            foreach (PartSettings settings in parts)
            {
                if (settings == null || seen.ContainsKey(settings.id))
                    continue;
                seen[settings.id] = Clean(settings.name) + "|" + Clean(settings.fullLabel);
            }
            var text = new StringBuilder();
            foreach (KeyValuePair<ushort, string> pair in seen)
            {
                if (text.Length > 0)
                    text.Append(';');
                text.Append(pair.Key.ToString(CultureInfo.InvariantCulture));
                text.Append('|');
                text.Append(pair.Value);
            }
            return text.ToString();
        }

        public string MissingText()
        {
            var names = new List<string>(_missing);
            if (names.Count > 4)
                return string.Join(", ", names.GetRange(0, 4)) + " and " + (names.Count - 4) + " more";
            return string.Join(", ", names);
        }

        // Hull panels must stay HullPartSettings, because the game casts them while it loads the ship.
        public bool Resolve(ushort savedId, bool hullPanel, out ushort id)
        {
            _savedNames.TryGetValue(savedId, out string savedName);
            PartSettings current = InstalledParts.ById(savedId);
            bool fits = current != null && (!hullPanel || current is HullPartSettings);
            if (fits && (string.IsNullOrEmpty(savedName) || current.name == savedName))
            {
                id = savedId;
                return true;
            }
            PartSettings named = string.IsNullOrEmpty(savedName) ? null : InstalledParts.ByName(savedName, hullPanel);
            if (named != null)
            {
                id = named.id;
                return true;
            }
            // A game part keeps its id when an update renames its asset. Mod parts are named "Mod.Part", and another
            // mod's part on the same id is a different part.
            if (fits && !IsModPart(savedName) && !IsModPart(current.name))
            {
                id = savedId;
                return true;
            }

            id = 0;
            if (_savedLabels.TryGetValue(savedId, out string label))
                _missing.Add(label);
            else if (!string.IsNullOrEmpty(savedName))
                _missing.Add(savedName);
            else
                _missing.Add("part " + savedId.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        static bool IsModPart(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf('.') > 0;
        }

        static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return text.Replace('|', ' ').Replace(';', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        // Ids go through the game's PartSettingsProvider, which mods patch to add their parts. Its table throws for an
        // unknown id, and for every id when two parts share one; then the first part with that id is used.
        static class InstalledParts
        {
            static PartSettingsProvider _provider;
            static readonly Dictionary<ushort, PartSettings> Known = new Dictionary<ushort, PartSettings>();
            static Dictionary<ushort, PartSettings> _firstById;
            static Dictionary<string, PartSettings> _byName;

            public static void Clear()
            {
                _provider = null;
                Known.Clear();
                _firstById = null;
                _byName = null;
            }

            public static PartSettings ById(ushort id)
            {
                if (Known.TryGetValue(id, out PartSettings known))
                    return known;
                if (_provider == null)
                    _provider = new PartSettingsProvider();
                PartSettings settings;
                try
                {
                    settings = _provider.GetPartSettingById(id);
                }
                catch (Exception)
                {
                    settings = FirstById(id);
                }
                Known[id] = settings;
                return settings;
            }

            public static PartSettings ByName(string name, bool hullPanel)
            {
                if (_byName == null)
                {
                    _byName = new Dictionary<string, PartSettings>(StringComparer.Ordinal);
                    foreach (PartSettings settings in Resources.FindObjectsOfTypeAll<PartSettings>())
                    {
                        if (settings == null || string.IsNullOrEmpty(settings.name) || _byName.ContainsKey(settings.name))
                            continue;
                        if (ById(settings.id) == settings)
                            _byName[settings.name] = settings;
                    }
                }
                if (!_byName.TryGetValue(name, out PartSettings found))
                    return null;
                return !hullPanel || found is HullPartSettings ? found : null;
            }

            static PartSettings FirstById(ushort id)
            {
                if (_firstById == null)
                {
                    _firstById = new Dictionary<ushort, PartSettings>();
                    foreach (PartSettings settings in Resources.FindObjectsOfTypeAll<PartSettings>())
                    {
                        if (settings != null && !_firstById.ContainsKey(settings.id))
                            _firstById[settings.id] = settings;
                    }
                }
                _firstById.TryGetValue(id, out PartSettings found);
                return found;
            }
        }
    }
}
