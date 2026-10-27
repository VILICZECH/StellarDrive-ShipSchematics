using System;
using System.IO;
using Managers.Interface;
using MelonLoader;
using Players.Interface.Services;

namespace ShipSchematics
{
    // Same marker as mods/CreativeMode, read from disk. This mod does not reference that assembly,
    // so a world still loads when Creative mode is not installed.
    static class CreativeWorlds
    {
        public const string FileName = "creative.mode";

        // A creative world, or the game's own free-build cheat (dev tools). Either one builds the ship at once.
        public static bool InstantBuild()
        {
            if (IsCreative())
                return true;
            try
            {
                IPlayersProviderClient players = GameServices.Get<IPlayersProviderClient>();
                return players != null && players.IsLocalPlayerUsingFreeBuildCheat();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool IsCreative()
        {
            try
            {
                string save = MainGameSceneLoadData.SaveFileFullName;
                if (string.IsNullOrEmpty(save))
                    return false;

                string directory = Path.GetDirectoryName(save);
                if (string.IsNullOrEmpty(directory))
                    return false;

                string marker = Path.Combine(directory, FileName);
                if (!File.Exists(marker))
                    return false;

                string text = File.ReadAllText(marker);
                return text != null && text.Trim() == "creative";
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not read the creative world marker. This world is treated as survival. " + ex.Message);
                return false;
            }
        }
    }
}
