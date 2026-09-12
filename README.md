A randomizer mod for Death Howl. It shuffles enemies, pickups, cave entrances,
card realms, recipes and card rewards based on a seed.

It has no logic: nothing checks that a seed can be finished. Entrance and totem
shuffles in particular can lock you out of progress.

The mod is a single BepInEx plugin. It does its shuffling while the game runs
and never changes any game files.

## Requirements

- Death Howl (Steam)
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases), the **Windows x64**
  build (use it on Linux too, since the game runs under Proton)
- The [.NET SDK](https://dotnet.microsoft.com/download) (any recent version),
  to build the plugin

## 1. Install BepInEx

1. Extract BepInEx into the game folder, next to `Death Howl.exe`.
2. **Linux / Steam Deck only:** in Steam, set the game's launch options to:

   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

3. Start the game once and quit. This lets BepInEx create its folders.

BepInEx must be installed before you build, because the build uses the
BepInEx and Unity files from the game folder.

## 2. Build

Point the build at the game's `Death Howl_Data` folder:

```
dotnet build mod/RandomHowl -c Release -p:GameDir="/path/to/Death Howl/Death Howl_Data"
```

Instead of passing `GameDir` every time, you can set the `DEATH_HOWL_DATA`
environment variable, or create `mod/RandomHowl/Local.props`:

```xml
<Project>
  <PropertyGroup>
    <GameDir>/path/to/Death Howl/Death Howl_Data</GameDir>
    <!-- optional: copy the plugin here after each build -->
    <PluginDir>/path/to/Death Howl/BepInEx/plugins/RandomHowl</PluginDir>
  </PropertyGroup>
</Project>
```

`Local.props` is gitignored. If BepInEx is not in the usual place next to
`Death Howl_Data`, set `BepInExDir` as well.

The build stops with an error if it can't find the game or BepInEx.

## 3. Install

If you set `PluginDir`, the build already copied the plugin. Otherwise copy
`mod/RandomHowl/bin/Release/RandomHowl.dll` to:

```
Death Howl/BepInEx/plugins/RandomHowl/
```

## Playing

1. Start the game. The first launch maps the game world during the splash
   screens.
2. On the title screen, open **RANDOMIZER** (under SETTINGS).
3. Enter a seed, or leave it empty for a random one, and pick your options.
   Changes save right away.
4. Start a **new game**. Use a fresh save slot; older saves don't mix well with
   a randomized world.

If the spoiler log is on, it is written next to the plugin as
`spoiler-<seed>.txt`.

## Options

Everything on the in-game screen is also stored in
`BepInEx/config/randomhowl.cfg`, which you can edit by hand:

| Setting | Values | What it does |
| --- | --- | --- |
| `seed` | text | Same seed, same shuffle. Empty picks a random one |
| `enemies` | `Off` / `On` / `Restricted` | Shuffle enemies between fights. Bosses never move. `Restricted` keeps elites in elite fights |
| `card_gifts` | `Off` / `On` / `Everything` | Shuffle which card each reward gives. `Everything` adds elder spirit gifts and Fylge cards |
| `elite_percent` | `-1` to `100` | Share of enemies that appear in elite form. `-1` keeps the vanilla amount (about 23%) |
| `totems` | true / false | Shuffle totem pickups |
| `ingredients` | true / false | Shuffle ingredient pickups |
| `entrances` | `Off` / `On` / `Decoupled` | Shuffle where caves lead. `On` keeps caves in pairs, so walking back out of a cave puts you where you came in. `Decoupled` sends every cave mouth and exit somewhere random. The way into a Fylge memory and back out never moves |
| `card_realms` | true / false | Shuffle which realm each card belongs to |
| `recipes` | true / false | Shuffle which ingredients each card needs |
| `player_energy` | `1` to `20` | Ro's energy per turn, before totems and skills. Vanilla is 5 |
| `scarce_howls` | true / false | Howls are kept on death and crafting is free, but beaten fights pay no howls or ingredients again. Howls are only spent on skill points. Enemies are placed so there's enough ingredients to craft every card up to its default copy limit |
| `all_cards_revealed` | true / false | All craftable cards are in the card book from the start |
| `skip_logos` | true / false | Skip the publisher logos |
| `skip_intro` | true / false | Skip the opening cutscene |
| `spoiler_log` | true / false | Write a spoiler log |

## Uninstall

Delete `BepInEx/plugins/RandomHowl/`. No game files were changed.
