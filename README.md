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
dotnet build mod -c Release -p:GameDir="/path/to/Death Howl/Death Howl_Data"
```

Instead of passing `GameDir` every time, you can set the `DEATH_HOWL_DATA`
environment variable, or create `mod/Local.props`:

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
`mod/bin/Release/RandomHowl.dll` to:

```
Death Howl/BepInEx/plugins/RandomHowl/
```

## Playing

1. Start the game. The first launch maps the game world during the splash
   screens.
2. On the title screen, open **RANDOMIZER** (under SETTINGS).
3. Enter a seed, or leave it empty for a random one, and pick your options.
   Changes save right away.
4. Start a **new game** in **custom mode**, with the **RANDOMIZER** row at the
   top of the custom mode screen on. Normal and rebirth games are never
   randomized, and neither is a custom game with that row off. The mod unlocks
   custom mode, so you don't have to beat rebirth mode first. Your settings
   are copied into that save, so changing them later only affects new games.

If the spoiler log is on, it is written next to the plugin as
`spoiler-<seed>.txt`.

The mod also changes the custom mode screen, in any custom mode game,
randomized or not:

- **ENERGY**, Ro's energy per turn, sits next to Ro's health.
- "These cards cost 1 more" is split into a toggle for each kind of card, so
  more than one can be on.
- "Realm cards cost 1 more" has an **ALL REALMS** choice: both foreign cards
  and cards from the realm you're in cost 1 more. Basic, quest, spirit and
  curse cards are left out, as the game's own realm choices do.

The custom mode screen remembers what you picked, so the next new custom game
starts from the same choices.

In a randomized game, every menu tab and fast travel are open from the start,
and the tutorial's "press ..." tips don't show. When a tutorial event waits
for you to press a key, the mod presses it for you.

## Options

Everything on the in-game screen is also stored in
`BepInEx/config/randomhowl.cfg`, which you can edit by hand. These are the
settings new games get.

When a new game starts, whether it is randomized is saved to
`profile_<N>.randomhowl.cfg` in the game's `saves_v_1_0` folder, next to that
save. For a randomized game, every setting except `skip_logos`, `skip_intro`,
`auto_text` and `spoiler_log` is copied there too. Loading the save uses that
copy, so edit it to change a game already in progress. A save with no copy
isn't randomized. Deleting a save in game deletes its copy too.

A game that isn't randomized plays like vanilla: nothing is shuffled, the rules
below are off, and the tutorial runs as normal. Only the extras still apply.

| Setting | Values | What it does |
| --- | --- | --- |
| `enabled` | true / false | Whether a new custom mode game is randomized. Same as the RANDOMIZER row on the custom mode screen |
| `seed` | text | Same seed, same shuffle. Empty picks a random one |
| `enemies` | `Off` / `On` / `Restricted` | Shuffle enemies between fights. Bosses never move. `Restricted` keeps elites in elite fights |
| `card_gifts` | `Off` / `On` / `Everything` | Shuffle which card each reward gives. `Everything` adds elder spirit gifts and Fylge cards. A reward card that nothing gives any more can be crafted instead, using the recipe of a card that took its place |
| `elite_percent` | `-1` to `100` | Share of enemies that appear in elite form. `-1` keeps the vanilla amount (about 23%) |
| `totems` | true / false | Shuffle totem pickups |
| `ingredients` | true / false | Shuffle ingredient pickups |
| `entrances` | `Off` / `On` / `Decoupled` | Shuffle where caves lead. `On` keeps caves in pairs, so walking back out of a cave puts you where you came in. `Decoupled` sends every cave mouth and exit somewhere random. The way into a Fylge memory and back out never moves |
| `card_realms` | true / false | Shuffle which realm each card belongs to |
| `recipes` | true / false | Shuffle which ingredients each card needs |
| `player_energy` | `1` to `20` | Ro's energy per turn in a custom mode game, before totems and skills. Same as the ENERGY row on the custom mode screen. Copied into every custom mode save, randomized or not. Vanilla is 5 |
| `scarce_howls` | true / false | Howls are kept on death and crafting is free, but beaten fights pay no howls or ingredients again. Howls are only spent on skill points. Enemies are placed so there's enough ingredients to craft every card up to its default copy limit |
| `all_cards_revealed` | true / false | All craftable cards are in the card book from the start |
| `skip_logos` | true / false | Skip the publisher logos |
| `skip_intro` | true / false | Skip the opening cutscene |
| `auto_text` | true / false | Dialogue boxes click through on their own, skipping the letter-by-letter text. Choices still wait |
| `spoiler_log` | true / false | Write a spoiler log |

The `[custom]` section holds the rest of the custom mode screen, as you last
left it. These are the game's own settings, so they are saved into a custom
mode save by the game itself, randomized or not.

| Setting | Values | What it does |
| --- | --- | --- |
| `rebirth_cards` | true / false | Use rebirth mode's cards |
| `rebirth_enemies` | true / false | Use rebirth mode's enemies |
| `enemy_health_percent` | number | Enemy health, as a percent of normal |
| `player_health` | number | Ro's health |
| `high_deck_minimum` | true / false | Decks need at least 20 cards instead of 15 |
| `crafting_limit` | number | Copies of each card that can be crafted. `-1` is the normal limit |
| `realm_cost` | `0` to `3` | Which realm cards cost 1 more: `0` foreign, `1` the realm you're in, `2` none, `3` all realms |
| `kind_cost` | number | Which kinds of card cost 1 more. Easier to set on the screen |
| `return_to_grove` | true / false | Go back to the grove when Ro dies |

## Uninstall

Delete `BepInEx/plugins/RandomHowl/`. No game files were changed.
