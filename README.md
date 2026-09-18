A no logic randomizer mod for Death Howl. Shuffles spirits, pickups, caves,
card realms, recipes and card rewards.

## Requirements

- [Death Howl](https://store.steampowered.com/app/2825880/Death_Howl)
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases), **Windows x64**
  build *(use on Linux too, since game runs under Proton)*
- [.NET SDK](https://dotnet.microsoft.com/download) (any recent version)

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

1. On title screen, open **RANDOMIZER**.
2. Enter seed, or leave it empty for a random one, and pick your options.
3. Start a **new game** in **custom mode**, with **RANDOMIZER** enabled.

If spoiler log is on, it is written next to plugin as `spoiler-<seed>.txt`.

Mod also expands custom mode, randomized or not:

- **ENERGY**, Ro's energy per turn.
- "These cards cost 1 more" is split into a toggle for each kind of card, so
  more than one can be on.
- "Realm cards cost 1 more" has an **ALL REALMS** choice: both foreign cards
  and cards from the realm you're in cost 1 more. Basic, quest, spirit and
  curse cards are left out, as the game's own realm choices do.

In a randomized game, every menu tab and fast travel are open from the start,
and the tutorial's "press ..." tips don't show.

Randomizer adds hints to materials screen: a material gets a bright rim when
it can still be found in current region. That means a pickup there she hasn't
taken, or a drop from a spirit there. With `scarce_howls` on, only fights with
uncollected spoiles count.

## Options

Everything on the in-game screen is also stored in
`BepInEx/config/randomhowl.cfg`. These are the settings new games get.
`skip_logos`, `skip_intro`, `auto_text`, `enable_cheats` and `faster_ro` are under **SETTINGS > GAMEPLAY**
instead.

When a new game starts, whether it is randomized is saved to
`profile_<N>.randomhowl.cfg` in the game's `saves_v_1_0` folder, next to that
save. For a randomized game, every setting except `skip_logos`, `skip_intro`,
`auto_text`, `enable_cheats`, `faster_ro` and `spoiler_log` is copied there too.

| Setting | Values | What it does |
| --- | --- | --- |
| `enabled` | true / false | Whether a new custom mode game is randomized. Same as the RANDOMIZER row on the custom mode screen |
| `seed` | text | Same seed, same shuffle. Empty picks a random one |
| `spirits` | `Off` / `On` / `Restricted` | Shuffle spirits between fights. Environmental objects shuffle with each other. `Restricted` swaps elder spirits only with elder spirits, and plain spirits only with plain ones. With `elder_percent` set, it keeps elder spirits in elder spirit fights instead |
| `card_gifts` | `Off` / `On` / `Everything` | Shuffle which card each reward gives. `Everything` adds elder spirit gifts and Fylge cards. A reward card that nothing gives any more can be crafted instead, using the recipe of a card that took its place |
| `elder_percent` | `-1` to `100` | Share of spirits that appear as elder spirits. With `spirits` set to `Restricted`, it only counts spirits in elder spirit fights. `-1` keeps the vanilla amount (about 23%) |
| `totems` | true / false | Shuffle totems |
| `ingredients` | true / false | Shuffle materials |
| `nests` | true / false | Shuffles nests. Nest contents are shuffled by other shuffles |
| `entrances` | `Off` / `On` / `Decoupled` | Shuffle where caves lead. `On` keeps caves in pairs, so walking back out of a cave puts you where you came in. `Decoupled` sends every cave mouth and exit somewhere random. |
| `card_realms` | true / false | Shuffle which realm each card belongs to |
| `recipes` | true / false | Shuffle which ingredients each card needs |
| `player_energy` | `1` to `20` | Ro's energy per turn in a custom mode game, before totems and skills. Same as the ENERGY row on the custom mode screen. Copied into every custom mode save, randomized or not. Vanilla is 5 |
| `scarce_howls` | true / false | Howls are kept on death and crafting is free, but beaten fights pay no howls or ingredients again. Howls are only spent on skill points. Spirits are placed so there's enough ingredients to craft every card up to its default copy limit |
| `all_cards_revealed` | true / false | All craftable cards are in the card book from the start |
| `tear_health` | true / false | Enemies get 1% more health for each blood tear placed on the skill tree, up to 75% more |
| `open_world` | true / false | Paths from forest are open from the start |
| `skip_logos` | true / false | Skip publisher logos |
| `skip_intro` | true / false | Skip intro and moose cutscenes. Walks in those scenes still happen, and blood tears still show their popup |
| `auto_text` | true / false | Dialogue boxes click through on their own, skipping the letter-by-letter text. Choices still wait |
| `enable_cheats` | true / false | Turn on the game's debug cheat keys, only in saves that aren't randomized |
| `faster_ro` | true / false | Ro moves twice as fast |
| `spoiler_log` | true / false | Write a spoiler log |

The `[custom]` section holds the rest of the custom mode screen.

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

## Cheats

With `enable_cheats` on, in a save that isn't randomized, hold Shift and press
a key. Pressing both Shifts at once keeps cheats on without holding Shift.

| Key | Does |
| --- | --- |
| `N` | Deal 100 damage to all enemies (in fight, on your turn) |
| `X` | End fight |
| `L` | Lightning at mouse cell (in fight) |
| `C` | Draw a card in fight. In cards view, craft hovered card |
| `H` | Full health |
| `M` | Full energy (in fight) |
| `R` | +1 energy. Outside fights, also respawn enemies and heal |
| `E` | +10 progress toward new recipes |
| `D` | +1 howl |
| `F` | Toggle 5x game speed |
| `I` | In cards view, add missing materials for hovered card |
| `Y` | In cards view, unlock 4 random recipes of this realm |
| `U` | In cards view, unlock all realms, press again for every card. In totems, get all totems. In skill tree, +10 blood tears |
| `A` | In cards view, no minimum deck size |
| `B` | In cards view, random 15 card deck of this realm |
| `G` + `1`–`5` | Hide UI, hover stats, cursor, dialogue, Ro |
| `K` + `1`–`3` | Input: keyboard & mouse, controller, both |

## Uninstall

Delete `BepInEx/plugins/RandomHowl/`.
