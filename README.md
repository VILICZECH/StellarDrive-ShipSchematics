# Ship Schematics

Built the perfect ship? Save it once and build it again anywhere.

- Hold P to save the ship you are standing on, as a new schematic or over an old one. Wiring, rotor and piston heads, docked ships and the wires between them are all included
- Tap P to open your collection: search, sort by name, date or size, rename and delete
- Share ships with friends by importing and exporting .schematic files
- Creative world (with the Creative mode mod): the ship appears instantly
- Survival: place a hologram blueprint and fill it with materials, Shift-click delivers a whole stack
- Changed your mind? The recycler scraps a blueprint and gives back everything you delivered
- Blueprints stay in the world when you save it

In multiplayer only the host can place ships.

MelonLoader mod for Stellar Drive.

## Installation

Install it from the Stellar Mod Manager, or copy `ShipSchematics.dll` from the latest release into the game's `Mods` folder. The game needs MelonLoader.

Schematics are stored in `%USERPROFILE%\AppData\LocalLow\CuriousOwlGames\StellarDrive\Schematics`. Exported `.schematic` files default to `Documents\StellarDrive Schematics`.

## Building from source

You need the .NET SDK (any version that can build `net472` projects) and Stellar Drive with MelonLoader installed. The project references the game's own assemblies, so nothing from the game is included in this repository.

```
dotnet build src/ShipSchematics.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\StellarDrive Demo"
```

The DLL ends up in `src/bin/Release`. Add `-p:CopyToMods=true` to copy it into the game's `Mods` folder.

## How the code is organised

| File | What it does |
| --- | --- |
| `ShipSchematics.cs` | Mod entry point: the P key, placement mode and status messages |
| `GameServices.cs` | Cached lookups for the game's services, so a frame does not scan the scene again |
| `ScreenMessages.cs` | Status, placement hint and warning lines on a canvas above the game UI |
| `SaveHoldHint.cs` | The "hold P" hint, a copy of the game's own hold hint |
| `SchematicFile.cs` | The saved formats: library entries and `.schematic` files |
| `SchematicLibrary.cs` | Captures a ship tree from the world, stores schematics, imports and exports them |
| `Subships.cs` | Ships held by rotors and pistons or docked to the main ship: poses, holograms, placement |
| `PartCatalog.cs` | Matches saved part ids to the parts installed now, including parts from other mods |
| `SchematicScreen.cs`, `ShipCards.cs` | The collection and save windows: the scrolling ship list, renaming and the pictures of the ships |
| `ScreenKit.cs` | Window parts cloned from the game's own screens: the build menu frame, buttons, text fields and dialogs |
| `SchematicPreview.cs` | Draws a ship hologram into a picture |
| `SchematicPlacer.cs` | The placement hologram and building the ship in a creative world |
| `SchematicBuildSite.cs`, `BlueprintPartPoint.cs`, `BlueprintSupplyMenu.cs`, `BlueprintRecycle.cs` | Survival blueprints: material delivery and recycling |
| `BlueprintWorld.cs` | Saves blueprints with the world and restores them on load |
| `CreativeWorlds.cs` | Tells creative worlds by the `creative.mode` file that the Creative mode mod writes |
| `OutlineGuard.cs` | Keeps the game's outline effect from failing on hologram meshes |
| `SchematicExchange.cs` | Windows file dialogs for import and export |
| `GameplayLock.cs`, `Shared/GameKeys.cs` | Keeps the mod's keys away from the chat, text fields and game menus |
| `ModMatch.cs` | Warns in multiplayer when the host and a player run different mods |

## License

MIT, see [LICENSE](LICENSE).
