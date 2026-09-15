using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace RandomHowl
{
    /// A no-logic randomizer for Death Howl: it shuffles, but doesn't check the
    /// result is beatable.
    ///
    /// Game files are never changed. The mod maps the world at startup, the
    /// seed decides where things go, and Harmony patches put them there as the
    /// game asks.
    [BepInPlugin(Id, "RandomHowl", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Id = "randomhowl";
        public const string Version = "1.0.0";
        public static Plugin Instance { get; private set; }

        // Live config entries, edited by the screen: the settings a new game
        // gets. A run reads them through Rule, so each save keeps its own copy.
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> Seed;
        public Dictionary<string, ConfigEntry<bool>> Shuffles;
        public ConfigEntry<EntranceShuffle> EntranceMode;
        public ConfigEntry<SpiritShuffle> SpiritMode;
        public ConfigEntry<GrantShuffle> GrantMode;
        public ConfigEntry<int> ElderPercent;
        public ConfigEntry<int> PlayerEnergy;
        public ConfigEntry<bool> Scarce;
        public ConfigEntry<bool> RevealCards;
        public ConfigEntry<bool> TearHealth;
        public ConfigEntry<bool> OpenWorld;
        public ConfigEntry<bool> SkipLogos;
        public ConfigEntry<bool> SkipIntro;
        public ConfigEntry<bool> AutoText;
        public ConfigEntry<bool> SpoilerLog;

        /// False until the world scan finishes; the screen waits on it.
        public bool Ready { get; private set; }

        Harmony harmony;
        World world;
        Plan plan;
        object gameData;
        bool cardsPending;

        // The settings file of the save being played. Null means the menu's
        // own settings.
        ConfigFile profile;
        const string ProfileExtension = ".randomhowl.cfg";

        // What each game setting is when a save isn't randomized.
        Dictionary<ConfigEntryBase, object> vanilla;

        void Awake()
        {
            Instance = this;

            Enabled = Config.Bind("randomizer", "enabled", true,
                "whether the game is randomized. Only a custom mode game can be. "
                + "This is the RANDOMIZER row on the custom mode screen");
            Seed = Config.Bind("randomizer", "seed", "", "same seed same shuffle");

            // Each toggle has its own seed, so turning one off leaves the rest
            // unchanged.
            Shuffles = new Dictionary<string, ConfigEntry<bool>>
            {
                { "totems", Config.Bind("shuffle", "totems", true,
                    "which totem each totem pickup gives") },
                { "ingredients", Config.Bind("shuffle", "ingredients", true,
                    "which ingredient each world pickup is") },
                { "card_realms", Config.Bind("shuffle", "card_realms", true,
                    "which realm each card belongs to") },
                { "recipes", Config.Bind("shuffle", "recipes", true,
                    "which ingredients each card is crafted from") },
            };

            EntranceMode = Config.Bind("shuffle", "entrances", EntranceShuffle.On,
                "where each cave mouth leads. On keeps caves in pairs, so "
                + "walking back out puts you where you came in. Decoupled sends "
                + "every cave mouth and exit somewhere random.");
            SpiritMode = Config.Bind("shuffle", "spirits", SpiritShuffle.On,
                "On puts any non-boss spirit anywhere. Environmental actors "
                + "shuffle with each other. Restricted swaps elder spirits only "
                + "with elder spirits, and plain spirits only with plain ones. "
                + "With elder_percent set, it keeps elder spirits in elder spirit fights instead");
            GrantMode = Config.Bind("shuffle", "card_gifts", GrantShuffle.On,
                "which card each reward gives: the aurora, and blood tears and "
                + "skill nodes that grant a card. Everything adds elder spirit "
                + "gifts and Fylge cards. A reward card nothing gives any more "
                + "can be crafted, using the recipe of a card that took its place");

            ElderPercent = Config.Bind("shuffle", "elder_percent", -1,
                new ConfigDescription(
                    "percent of spirits with an elder spirit form that appear as "
                    + "one. At least one plain and one elder spirit of each kind "
                    + "stay in the world, so both sets of ingredients can be found. "
                    + "With spirits set to Restricted, it only counts spirits in "
                    + "elder spirit fights. Vanilla is ~23%",
                    new AcceptableValueRange<int>(-1, 100)));

            PlayerEnergy = Config.Bind("combat", "player_energy", 5,
                new ConfigDescription(
                    "Ro's energy per turn in a custom mode game, before totems "
                    + "and skills add to it. This is the ENERGY row on the custom "
                    + "mode screen",
                    new AcceptableValueRange<int>(1, 20)));

            Scarce = Config.Bind("rules", "scarce_howls", false,
                "howls are kept on death, beaten fights pay no howls or "
                + "ingredients again, and crafting is free, so howls only buy "
                + "skill points. Spirits and drops are placed so first wins give "
                + "enough ingredients to craft every card up to its default copy "
                + "limit. Drops are picked up automatically");
            RevealCards = Config.Bind("rules", "all_cards_revealed", false,
                "every craftable card is in the book from the start, instead of "
                + "four at a time as the ingredient juice fills up");
            TearHealth = Config.Bind("rules", "tear_health", false,
                "enemies get 1% more health for each blood tear placed on the "
                + "skill tree, up to 75% more.");
            OpenWorld = Config.Bind("rules", "open_world", false,
                "paths from forest are open from the start");

            SkipLogos = Config.Bind("extras", "skip_logos", true,
                "jump straight past the publisher logos to the title screen");
            SkipIntro = Config.Bind("extras", "skip_intro", true,
                "skip the 83 second opening cutscene");
            AutoText = Config.Bind("extras", "auto_text", false,
                "dialogue boxes show each line in full and move on without "
                + "a click. Choices still wait for you to pick one");
            SpoilerLog = Config.Bind("extras", "spoiler_log", true,
                "write spoiler-<seed>.txt next to this plugin");

            vanilla = new Dictionary<ConfigEntryBase, object>
            {
                { Seed, "" },
                { EntranceMode, EntranceShuffle.Off },
                { SpiritMode, SpiritShuffle.Off },
                { GrantMode, GrantShuffle.Off },
                { ElderPercent, -1 },
                { Scarce, false },
                { RevealCards, false },
                { TearHealth, false },
                { OpenWorld, false },
            };
            foreach (var entry in Shuffles.Values) vanilla[entry] = false;

            // The extras go on now, on their own Harmony, so Rebuild's unpatch
            // doesn't remove them.
            var extras = new Harmony(Id + ".extras");
            Patches.InstallExtras(extras, Logger, SkipLogos.Value);
            Options.Install(extras, Logger);
            Custom.Install(extras, Logger);

            // Map the world, then shuffle and patch. The scan is a coroutine
            // during the splash screen, so the game doesn't stall.
            harmony = new Harmony(Id);
            StartCoroutine(Discovery.Scan(Logger, OnWorldReady));
        }

        /// Called once the world scan finishes. Builds the plan from the
        /// current seed and toggles, then installs the patches.
        void OnWorldReady(World scanned)
        {
            world = scanned;
            if (gameData != null) Discovery.CollectValues(world, gameData);
            Rebuild();
            Ready = true;
        }

        /// The card list only exists once the data manager starts, usually
        /// after the scan. Collect the cards then and roll again, since the
        /// first roll had none.
        public void OnCardsLoaded(object manager)
        {
            gameData = manager;
            if (cardsPending || world == null || world.Cards.Count != 0) return;
            cardsPending = true;
            StartCoroutine(AddCards());
        }

        IEnumerator AddCards()
        {
            yield return null;          // step out of the patch that called us
            Discovery.CollectValues(world, gameData);
            Rebuild();
        }

        /// Rebuilds the plan from the current save's settings (or the menu's,
        /// at the title screen) and reinstalls patches. Starting a run calls
        /// this; editing settings on the screen doesn't.
        public void Rebuild()
        {
            if (harmony != null) harmony.UnpatchSelf();
            harmony = new Harmony(Id);

            var seed = Rule(Seed);
            plan = Plan.Build(world, seed, name => Rule(Shuffles[name]),
                              Rule(EntranceMode), Rule(SpiritMode), Rule(ElderPercent), Rule(GrantMode),
                              Rule(Scarce));
            Patches.Install(harmony, Logger, plan, SkipIntro.Value);
            Materials.Install(harmony, Logger, world, plan);

            // Realms, recipes and the reveal live on shared card assets, not
            // scenes, so they are written here. They do nothing until cards are
            // in the plan.
            Patches.SetCardRealms();
            Patches.SetRecipes();
            Patches.RevealAllCards();

            Logger.LogInfo(string.Format(
                "seed {0}: {1} pickups, {2} totems, {3} cave mouths, {4} arenas, "
                + "{5} cards, {6} recipes, {7} card gifts",
                seed, plan.Ingredients.Count, plan.Totems.Count,
                plan.Entrances.Count, plan.Spirits.Count, plan.Cards.Count,
                plan.Recipes.Count, plan.Grants.Count));

            if (SpoilerLog.Value && Randomized) Spoil(seed, plan);
        }

        /// A setting as the current save has it. Each save keeps its own copy,
        /// so menu changes only affect new games. Saves that aren't randomized
        /// get the vanilla value.
        public static T Rule<T>(ConfigEntry<T> entry)
        {
            var plugin = Instance;
            var file = plugin.profile;
            if (file == null) return entry.Value;
            object off;
            if (!Randomized && plugin.vanilla.TryGetValue(entry, out off)) return (T)off;
            return file.Bind(entry.Definition, entry.Value, entry.Description).Value;
        }

        /// Whether the save being played is randomized. At the title screen
        /// the menu's settings are shown as a randomized game.
        public static bool Randomized
        {
            get
            {
                var plugin = Instance;
                return plugin.profile == null
                    || plugin.profile.Bind(plugin.Enabled.Definition, false,
                                           plugin.Enabled.Description).Value;
            }
        }

        /// Runs when a run starts, once the save slot is picked. A new
        /// randomized custom game copies the menu settings next to its save; a
        /// loaded game reads its copy back. A save with no copy isn't
        /// randomized.
        public void OpenProfile()
        {
            profile = null;
            try
            {
                var path = SlotPath(null);
                if (path == null)
                {
                    Logger.LogWarning("can't find the save folder, so every save "
                                      + "uses the menu settings");
                }
                else
                {
                    // No save yet means a new game, so any old copy is stale.
                    var fresh = !File.Exists(path + ".save");
                    if (fresh) File.Delete(path + ProfileExtension);
                    var file = new ConfigFile(path + ProfileExtension, false);
                    file.SaveOnConfigSet = false;
                    profile = file;
                    // A save with no settings file isn't randomized.
                    var randomized = file.Bind(Enabled.Definition, false, Enabled.Description);
                    if (fresh) randomized.Value = CustomMode() && Enabled.Value;
                    // Energy is on the custom mode screen, so every custom game
                    // has it. Older saves without it use 5.
                    var energy = file.Bind(PlayerEnergy.Definition, 5, PlayerEnergy.Description);
                    if (fresh) energy.Value = CustomMode() ? PlayerEnergy.Value : 5;
                    if (randomized.Value)
                    {
                        // Read every setting now, so the whole set is copied at
                        // once.
                        Rule(Seed);
                        foreach (var entry in Shuffles.Values) Rule(entry);
                        Rule(EntranceMode);
                        Rule(SpiritMode);
                        Rule(GrantMode);
                        Rule(ElderPercent);
                        Rule(Scarce);
                        Rule(RevealCards);
                        Rule(TearHealth);
                        Rule(OpenWorld);
                    }
                    file.Save();
                    Logger.LogInfo((randomized.Value ? "randomized" : "not randomized")
                                   + ", settings for this save: " + file.ConfigFilePath);
                }
            }
            catch (Exception e)
            {
                profile = null;
                Logger.LogWarning("could not read this save's settings, using the "
                                  + "menu's: " + e.Message);
            }
            Rebuild();
        }

        /// Whether the new game about to start is custom mode. Normal and
        /// rebirth games are never randomized.
        static bool CustomMode()
        {
            var type = AccessTools.TypeByName("TitleMenu");
            var settings = type == null ? null
                : AccessTools.Field(type, "lastSelcetedModeSettings")?.GetValue(null);
            return settings != null && Fields.Get(settings, "mode") as int? == 2;
        }

        /// Go back to the menu's settings.
        public void CloseProfile()
        {
            profile = null;
        }

        /// A deleted save takes its settings with it.
        public void ForgetProfile(int slot)
        {
            var path = SlotPath(slot);
            if (path == null) return;
            try { File.Delete(path + ProfileExtension); }
            catch (Exception e)
            {
                Logger.LogWarning("could not delete " + path + ProfileExtension + ": " + e.Message);
            }
        }

        /// Where the game keeps a save slot, without the extension. A null slot
        /// means the one the player picked. Null if the game's save manager
        /// isn't shaped the way we expect.
        static string SlotPath(int? slot)
        {
            var type = AccessTools.TypeByName("PersistantDataManager");
            var folder = type == null ? null
                : AccessTools.Field(type, "baseSavePath")?.GetValue(null) as string;
            if (folder == null) return null;
            if (slot == null)
            {
                var settings = AccessTools.Method(type, "GetSettings")?.Invoke(null, null);
                slot = settings == null ? null : Fields.Get(settings, "chosenProfile") as int?;
                if (slot == null) return null;
            }
            return Path.Combine(folder, "profile_" + slot.Value);
        }

        void Spoil(string seed, Plan plan)
        {
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var name = new StringBuilder("spoiler-");
            foreach (var ch in seed)
                name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) < 0 ? ch : '_');
            name.Append(".txt");

            try
            {
                var path = Path.Combine(folder, name.ToString());
                using (var file = new StreamWriter(path, false))
                {
                    file.WriteLine("# randomhowl seed " + seed);
                    file.WriteLine("# what\twhere\twas\tnow");
                    foreach (var line in plan.Spoiler) file.WriteLine(line);
                }
                Logger.LogInfo("spoiler log " + path);
            }
            catch (Exception e)
            {
                Logger.LogWarning("could not write the spoiler log: " + e.Message);
            }
        }
    }
}
