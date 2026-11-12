# Valheim Helper

**English** · [Українська](README.uk.md)

A mod for Valheim (your own world and co-op with friends): an extended map, a built-in wiki guide in several languages,
a free camera and photo mode, a base helper (smelters, torches, chest sorting) and, in the full edition, a cheat menu and
an achievement helper. It comes in three editions, so you can pick how far it goes.

## Editions

| Feature | Legal | Helper | Full |
|---|:-:|:-:|:-:|
| **Map**: biomes, points of interest, item sources, your own waypoints and sketches | ✔ | ✔ | ✔ |
| **Map**: show unexplored areas (only drawn, never saved to the character) | ✔ | ✔ | ✔ |
| **Wiki guide** inside the game, with pictures, updating itself from the wiki | ✔ | ✔ | ✔ |
| **Free camera** and **photo mode** | ✔ | ✔ | ✔ |
| **Statistics** of the character and the world, compared with friends | ✔ | ✔ | ✔ |
| **Building view modes** (frame only, no roofs, walls only…) | ✔ | ✔ | ✔ |
| **Achievements**: the list with the state in the game and in Steam | ✔ | ✔ | ✔ |
| Key bindings, image (brightness, gamma), performance, menu language | ✔ | ✔ | ✔ |
| **Crafting**: choose the amount, take materials from nearby chests | | ✔ | ✔ |
| **Pick up** everything lying around | | ✔ | ✔ |
| **Chests**: stash into the right chests, base storage, "do not touch" marks, chest tags | | ✔ | ✔ |
| **Smelters and kilns**: fill them, finished goods straight into a chest | | ✔ | ✔ |
| **Torches and fires**: refill, highlight empty ones | | ✔ | ✔ |
| **Tamed animals**: list with health, hunger, pregnancy | | ✔ | ✔ |
| **World modifiers** of the game itself, changed in a running world | | ✔ | ✔ |
| **Character**: god mode, infinite stamina and eitr, no weight limit, no durability loss | | | ✔ |
| **Movement**: flying, fast run, walking underwater, ore through portals | | | ✔ |
| **Skills** up, **items** given, items sent to friends, status effects | | | ✔ |
| **Building** without resources and without support | | | ✔ |
| **World**: time and weather, loot multiplier, game speed, creatures | | | ✔ |
| **Map**: teleport, reveal the whole map for the character permanently | | | ✔ |
| **Achievements**: unlock one or all, ignore the game's cheat marks, clean flagged saves | | | ✔ |

The code of a higher edition is not part of a lower one at all: the Legal and Helper editions contain no cheats,
ignore cheat settings left by the Full edition and do not accept items sent by a friend with the Full edition.

## Languages

- **Menu**: English, Ukrainian, German, French, Spanish. Polish, Portuguese (Brazil), Chinese (Simplified) and Czech
  are being translated. "As the game's language" picks the language Valheim runs in; a missing text is shown in English.
- **Wiki guide**: Ukrainian, English, German, French, Polish, Portuguese (Brazil), Czech, Chinese, from the Valheim Fandom
  wikis in those languages.

## Installation
1. Go to [Releases](../../releases) and download **one** archive, the edition you want:
   `ValheimHelper-legal.zip`, `ValheimHelper-helper.zip` or `ValheimHelper-full.zip` (see the table above).
   The `*-update.zip` files are for the mod's own updater, you don't need them.
2. Close the game, unpack the archive and run `install.bat`. It finds Valheim in Steam by itself.
3. Start the game as usual. Menu: F7 or Insert. `uninstall.bat` removes the mod.

To switch to another edition, install its archive over the current one.

## Updates
The mod updates itself. When the game starts it asks GitHub whether a newer version is out; if so, it downloads the
update of your edition in the background and installs it the next time you start the game. You can turn the check off
or run it by hand in **Settings → Updates**. The wiki guide updates separately, straight from the wiki.

## Building
Requires the .NET SDK and an installed Valheim (the game's assemblies are referenced in `ValheimHelper/ValheimHelper.csproj`).
Unpack BepInExPack for Valheim (5.4.2350) into `deps/BepInExPack`: `winhttp.dll` and Harmony are taken from it.

```powershell
.\build.ps1                       # all three editions, tests, archives in dist\
.\build.ps1 -Editions full        # one edition
.\release.ps1 -Notes "What changed"   # build and publish a GitHub release (raise <Version> first)
dotnet build ValheimHelper\ValheimHelper.csproj -p:Edition=legal   # legal | helper | full
```
