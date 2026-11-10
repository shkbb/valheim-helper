# Valheim Helper

**English** · [Українська](README.uk.md)

A mod for Valheim (your own world and co-op with friends): an extended map, a built-in wiki guide in Ukrainian, English
and other languages, a free camera, a base helper (smelters, torches, chest sorting), a cheat menu and an achievement helper.
The interface is in English and Ukrainian.

## Features
- **Map** — biomes, points of interest, item sources, your own waypoints and sketches.
- **Wiki guide** — the Valheim Fandom wiki inside the game (Ukrainian, English, German, French, Polish, Portuguese,
  Czech, Chinese), with pictures; it updates itself from the wiki while open.
- **Free camera** and photo mode.
- **Base helper** — fills smelters, kilns and torches, sorts chests, drops items into the right chests, crafts from nearby chests.
- **Achievements** — the list of all achievements with their state in the game and in Steam, and a button that unlocks one
  or all of them through the game's own call; shows what blocks achievements and cleans flagged saves.
- **Cheat menu** — god mode, flying, items, skills, time and weather, world modifiers, creatures and more.

## Installation
Close the game, unpack the archive from [Releases](../../releases) and run `install.bat` (it finds Valheim in Steam by itself).
Menu: F7 or Insert. `uninstall.bat` removes the mod.

## Building
Requires the .NET SDK and an installed Valheim (the game's assemblies are referenced in `ValheimHelper/ValheimHelper.csproj`).
Unpack BepInExPack for Valheim (5.4.2350) into `deps/BepInExPack`: `winhttp.dll` and Harmony are taken from it.

```powershell
.\build.ps1            # build, tests, archive in dist\
.\pack_lite.ps1        # small archive without the wiki packs
```

How the mod gets around the game's achievement blocks and how everything is organised: `ARCHITECTURE.md` (Ukrainian).
