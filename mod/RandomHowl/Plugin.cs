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
    /// A no-logic randomizer for Death Howl. It shuffles things; it does not
    /// check that the result is beatable.
    ///
    /// The game's own files are never touched. The mod maps the world itself at
    /// startup, the seed says where each thing goes, and Harmony puts it there
    /// as the game asks for it. No python, no pre-dumped table — drop the
    /// plugin in and set a seed from the title screen.
    [BepInPlugin(Id, "RandomHowl", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Id = "randomhowl";
        public const string Version = "1.0.0";
        public static Plugin Instance { get; private set; }

        // Live config entries, read by the screen and by Plan.Build.
        public ConfigEntry<string> Seed;
        public Dictionary<string, ConfigEntry<bool>> Shuffles;
        public ConfigEntry<EntranceShuffle> EntranceMode;
        public ConfigEntry<EnemyShuffle> EnemyMode;
        public ConfigEntry<GrantShuffle> GrantMode;
        public ConfigEntry<int> ElitePercent;
        public ConfigEntry<int> PlayerEnergy;
        public ConfigEntry<bool> Scarce;
        public ConfigEntry<bool> RevealCards;
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

        void Awake()
        {
            Instance = this;

            Seed = Config.Bind("randomizer", "seed", "", "same seed same shuffle");

            // Each toggle is seeded on its own, so turning one off leaves the
            // others exactly where they were.
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
                + "every cave mouth and exit somewhere random. The way into a "
                + "Fylge memory and back out never moves");
            EnemyMode = Config.Bind("shuffle", "enemies", EnemyShuffle.On,
                "On puts any non-boss enemy anywhere. Bosses never move. "
                + "Restricted leaves elites in elite combats");
            GrantMode = Config.Bind("shuffle", "card_gifts", GrantShuffle.On,
                "which card each reward hands over: the aurora, the blood "
                + "tears that grant a card, the skill nodes that grant one. "
                + "Everything takes in the elder spirit gifts and the Fylge "
                + "cards too. A reward card nothing hands over any more can "
                + "be crafted, from the recipe of a card that took its place");

            ElitePercent = Config.Bind("shuffle", "elite_percent", -1,
                new ConfigDescription(
                    "how many of the enemies that have an elite form are elite. "
                    + "One plain and one elite of every kind is always left in "
                    + "the world, so both sets of ingredients stay findable. "
                    + "Vanilla is ~23%",
                    new AcceptableValueRange<int>(-1, 100)));

            PlayerEnergy = Config.Bind("combat", "player_energy", 5,
                new ConfigDescription(
                    "Ro's energy per turn before totems and skills add to it",
                    new AcceptableValueRange<int>(1, 20)));

            Scarce = Config.Bind("rules", "scarce_howls", false,
                "howls survive death, a combat you have already beaten pays "
                + "no howls or ingredients the second time, and crafting is "
                + "free. Howls become a finite pool spent only on skill points. "
                + "Enemies and their drops are set by the seed so first wins "
                + "drop enough ingredients to craft every card up to its default "
                + "copy limit, and drops are picked up automatically");
            RevealCards = Config.Bind("rules", "all_cards_revealed", false,
                "every craftable card is in the book from the start, instead of "
                + "four at a time as the ingredient juice fills up");

            SkipLogos = Config.Bind("extras", "skip_logos", true,
                "jump straight past the publisher logos to the title screen");
            SkipIntro = Config.Bind("extras", "skip_intro", true,
                "cut the 83 second opening cutscene down to nothing");
            AutoText = Config.Bind("extras", "auto_text", false,
                "dialogue boxes show each line in full and move on without "
                + "a click. Choices still wait for you to pick one");
            SpoilerLog = Config.Bind("extras", "spoiler_log", true,
                "write spoiler-<seed>.txt next to this plugin");

            // The logo skip and the menu screen go on now, and on their own
            // Harmony — Rebuild unpatches the shuffle and puts it back, and
            // neither of these has any business in that.
            var extras = new Harmony(Id + ".extras");
            Patches.InstallExtras(extras, Logger, SkipLogos.Value);
            Options.Install(extras, Logger);

            // Map the world, then shuffle and patch. The scan runs as a
            // coroutine during the splash screen so it never stalls the game.
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

        /// The game's card list only exists once its data manager starts, and
        /// that is usually after the scan. Pick the cards up then and roll
        /// again — the first roll had no cards in it.
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

        /// Rebuild the plan from the current config and reinstall patches.
        /// The screen calls this after a seed or toggle change. Every
        /// patch writes on scene load, so the next new game is the new seed.
        public void Rebuild()
        {
            if (harmony != null) harmony.UnpatchSelf();
            harmony = new Harmony(Id);

            plan = Plan.Build(world, Seed.Value, name => Shuffles[name].Value,
                              EntranceMode.Value, EnemyMode.Value, ElitePercent.Value, GrantMode.Value,
                              Scarce.Value);
            Patches.Install(harmony, Logger, plan, SkipIntro.Value);

            // Realms, recipes and the reveal sit on the shared card assets,
            // not on a scene, so they are written here rather than by a scene
            // patch. Before the cards are in the plan these are no-ops.
            Patches.SetCardRealms();
            Patches.SetRecipes();
            Patches.RevealAllCards();

            Logger.LogInfo(string.Format(
                "seed {0}: {1} pickups, {2} totems, {3} cave mouths, {4} arenas, "
                + "{5} cards, {6} recipes, {7} card gifts",
                Seed.Value, plan.Ingredients.Count, plan.Totems.Count,
                plan.Entrances.Count, plan.Enemies.Count, plan.Cards.Count,
                plan.Recipes.Count, plan.Grants.Count));

            if (SpoilerLog.Value) Spoil(Seed.Value, plan);
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
