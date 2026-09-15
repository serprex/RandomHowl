using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RandomHowl
{
    /// Where the shuffle is applied.
    ///
    /// Each patch runs just before the game reads a field and writes what the
    /// plan says, so nothing on disk changes and nothing depends on scene load
    /// timing. Writing the same value twice is harmless.
    public static class Patches
    {
        static Plan plan;
        static ManualLogSource log;
        static bool skipIntro;
        static MethodInfo skipLogos;
        static bool logosSkipped;
        static bool keepingHowls;
        static string lastArena;
        static readonly HashSet<string> refights = new HashSet<string>();

        static readonly Dictionary<string, UnityEngine.Object> managers =
            new Dictionary<string, UnityEngine.Object>();

        // Every wait in a cutscene frame. Zeroing them ends each frame in a
        // tick. The game null-checks frame audio but not frame content, so the
        // images stay and only the waits go.
        static readonly string[] FrameTimings =
        {
            "imagesFadeInDuration", "durationWithoutText", "textFadeInDuration",
            "staticTextDuration", "textFadeOutDuration", "imagesFadeOutDuration",
        };

        /// The logos are gone before the world scan finishes, so this goes on
        /// in Awake and stays on. Install runs too late.
        public static void InstallExtras(Harmony harmony, ManualLogSource logger, bool logos)
        {
            log = logger;
            // Ro is in the always-loaded manager scene, so her Awake can run
            // before the scan finishes. Hook it early; it reads the config when
            // it fires.
            Hook(harmony, "Player", "Awake", nameof(PlayerAwake), true);

            // The howl rules and card reveal are settings, not shuffles. They
            // read the config when they fire, so they go on once whatever the
            // seed. The reveal has to be here, since the data manager loads the
            // save before the scan finishes.
            Hook(harmony, "LiveGameDataManager", "OnGameLoaded", nameof(GameLoaded), true);
            // The data manager's Start can run before the scan finishes, so
            // hook it early too. CardsLoaded handles an empty plan; the roll
            // after the scan writes the realms.
            Hook(harmony, "LiveGameDataManager", "Start", nameof(CardsLoaded), true);
            Hook(harmony, "LiveGameDataManager", "AddSoul", nameof(HowlGained));
            Hook(harmony, "CombatArena", "OnCombatStarted", nameof(CombatStarted));
            Hook(harmony, "LootManager", "OnLootDropAnimationDone", nameof(LootLanded), true);
            Hook(harmony, "CombatArena", "SetLeftOverDeathhowls", nameof(HowlsDropped));
            Hook(harmony, "PointsParticleHandler", "RoLoseDeathHowlAnimation",
                 nameof(HowlsFlyingAway));
            Hook(harmony, "CardData", "CurrentDeathHowlsCost", nameof(CardHowlCost),
                 getter: true);
            Hook(harmony, "ProgressionDataManager", "NextSkillOrbSoulAmount",
                 nameof(TearHowlCost), getter: true);
            Hook(harmony, "CombatArena", "InstantiateEnemy", nameof(EnemySpawned), true);

            // Two pickups in different scenes share a UUID, so taking one hid
            // both. Give each its own id before the scan or save reads it.
            Hook(harmony, "UUIDAuto", "ID", nameof(IdRead), true, getter: true);
            Hook(harmony, "UUIDManual", "ID", nameof(IdRead), true, getter: true);

            // The tutorial hides menu tabs and fast travel for a while.
            // Shuffled caves can lead out of the first forest before then with
            // no way back, so open everything from the start.
            Hook(harmony, "TutorialStartEvent", "Awake", nameof(UnlockMenus), true);
            Hook(harmony, "GenericTutorialMessage", "ShowText", nameof(TipShown));
            Hook(harmony, "TutorialTextManager", "ShowOpenCraftingTipNoBorder", nameof(SkipTip));
            Hook(harmony, "TutorialTextManager", "ShowSpendRessourcesToCraftTip", nameof(SkipTip));
            Hook(harmony, "TutorialTextManager", "ShowOpenTotemTip", nameof(TotemTip));
            Hook(harmony, "TutorialTextManager", "ShowOpenCraftingTip", nameof(CraftingTip));
            foreach (var button in new[] { MapButton, TotemButton, CardButton })
                Hook(harmony, "InputManager", button, nameof(ButtonPressed), true);

            // Reads the config as it fires, so the menu toggle works mid-run.
            Hook(harmony, "InputManager", "IsContinuePressed", nameof(ContinuePressed), true);

            // A deleted save takes its randomizer settings with it.
            Hook(harmony, "PersistantDataManager", "DeleteProfile", nameof(ProfileDeleted), true);

            if (!logos) return;
            var type = AccessTools.TypeByName("LogoIntroHandler");
            skipLogos = type == null ? null
                : AccessTools.Method(type, "StopAndSkipVideoIntro");
            if (skipLogos == null)
            {
                log.LogWarning("no LogoIntroHandler.StopAndSkipVideoIntro — "
                               + "the logo screens will play");
                return;
            }
            // Update, not Start, so it runs on the logo scene's next frame
            // whenever the plugin loads.
            Hook(harmony, "LogoIntroHandler", "Update", nameof(LogosShowing));
        }

        public static void Install(Harmony harmony, ManualLogSource logger, Plan shuffled,
                                   bool intro)
        {
            plan = shuffled;
            log = logger;
            skipIntro = intro;

            Hook(harmony, "WorldItem", "OnEnable", nameof(ItemAppeared));
            Hook(harmony, "EventArea", "Start", nameof(StagRemoved));
            Hook(harmony, "CombatArena", "Start", nameof(StagRemoved));
            Hook(harmony, "CombatArena", "Start", nameof(ArenaReady));
            Hook(harmony, "CombatArena", "SpawnEnemies", nameof(ArenaReady));
            Hook(harmony, "CombatArena", "SpawnEnemies", nameof(LeftoversCollected));
            // DoLootDropFlow has two overloads; this is the one a won fight starts.
            Hook(harmony, "LootManager", "DoLootDropFlow", nameof(LootLanding),
                 args: new[] { AccessTools.TypeByName("CombatArena") });
            Hook(harmony, "EnterOrExitCaveEvent", "DoHandleMidPartOfEventFlow", nameof(CaveEntered));
            Hook(harmony, "CombatArena", "OnCombatStarted", nameof(DeathSpawn));
            Hook(harmony, "EventComponentRecieveTotem", "DoRun", nameof(TotemGiven));
            Hook(harmony, "EventComponentRecieveCard", "DoRun", nameof(CardGiven));
            Hook(harmony, "ProgressionData", "Unlock", nameof(SkillBought));
            Hook(harmony, "CardData", "allowUnlockByCrafting", nameof(CardCraftable),
                 true, getter: true);
            if (skipIntro)
                Hook(harmony, "CutSceneSequence", "Show", nameof(CutsceneStarted));
        }

        static void Hook(Harmony harmony, string className, string methodName, string ours,
                         bool after = false, bool getter = false, Type[] args = null)
        {
            try
            {
                var type = AccessTools.TypeByName(className);
                var original = type == null ? null
                    : getter ? AccessTools.PropertyGetter(type, methodName)
                             : (MethodBase)AccessTools.Method(type, methodName, args);
                if (original == null)
                {
                    log.LogWarning("no " + className + "." + methodName
                                   + " — skipping that part of the shuffle");
                    return;
                }
                var mine = new HarmonyMethod(typeof(Patches).GetMethod(
                    ours, BindingFlags.Public | BindingFlags.Static));
                harmony.Patch(original, after ? null : mine, after ? mine : null);
            }
            catch (Exception e)
            {
                log.LogError("could not patch " + className + "." + methodName + ": " + e.Message);
            }
        }

        // --- the patches ---------------------------------------------------

        /// The game data reuses one pickup UUID in two scenes. Dark Forest Cave
        /// A1 keeps it; Meadow Bush Cave A1's copy becomes UUID:scene.
        public static void IdRead(object __instance, ref string __result)
        {
            if (__result == "2ab366b1-2888-4d4d-b28b-acff7d31f974"
                && ((Component)__instance).gameObject.scene.name == "MeadowBushCaveA1")
                __result += ":MeadowBushCaveA1";
        }

        public static void ItemAppeared(object __instance)
        {
            Guard("world item", () =>
            {
                var item = (Component)__instance;
                string guid;
                if (!plan.Ingredients.TryGetValue(Keys.Uuid(item), out guid)) return;
                var data = Registry.Item(guid);
                if (data == null) { Missing("ingredient", guid); return; }
                Fields.Set(__instance, "data", data);
                // Show the new ingredient's sprite too.
                var renderer = Fields.Get(__instance, "spriteRenderer") as SpriteRenderer;
                var sprite = Fields.Get(data, "IllustrationWorld") as Sprite;
                if (renderer != null && sprite != null) renderer.sprite = sprite;
            });
        }

        public static void ArenaReady(object __instance)
        {
            Guard("arena", () =>
            {
                var arena = (Component)__instance;
                string[] wanted;
                if (!plan.Spirits.TryGetValue(Keys.Uuid(arena), out wanted)) return;
                var prefabs = Fields.Get(__instance, "enemyPrefabs") as IList;
                if (prefabs == null) return;
                for (var i = 0; i < wanted.Length && i < prefabs.Count; i++)
                {
                    if (wanted[i] == null) continue;
                    var prefab = Registry.Spirit(wanted[i]);
                    if (prefab == null) Missing("spirit", wanted[i]);
                    else prefabs[i] = prefab;
                }
            });
        }

        public static void CaveEntered(object __instance)
        {
            Guard("cave", () =>
            {
                var cave = (Component)__instance;
                Entrance exit;
                if (!plan.Entrances.TryGetValue(Keys.PathOf(cave), out exit)) return;
                var area = Registry.Area(exit.Area);
                if (area == null) { Missing("area", exit.Area); return; }
                Fields.Set(__instance, "exitAreaData", area);
                Fields.Set(__instance, "targetSpawnPointID", exit.Spawn);
            });
        }

        /// The only caves whose fight sends you outside on death, and the one
        /// place their exits lead.
        static readonly Dictionary<string, string> DeathExits = new Dictionary<string, string>
        {
            { "MoorsEggCave", "Root/ExitCave" },
            { "CliffsMiniBossCave", "Root/ExitCave" },
        };

        /// Dying in those fights puts you where the cave's exit now leads, not
        /// outside its vanilla mouth. Set when the fight starts, since the
        /// death flow reads it right after.
        public static void DeathSpawn(object __instance)
        {
            Guard("death spawn", () =>
            {
                var scene = ((Component)__instance).gameObject.scene.name;
                string path;
                Entrance exit;
                if (!DeathExits.TryGetValue(scene, out path)
                    || !plan.Entrances.TryGetValue(Plan.Key(scene, path), out exit)) return;
                var death = Fields.Get(__instance, "spawnPointInAnotherScene");
                if (death == null || !(Fields.Get(death, "isSet") as bool? ?? false)) return;
                // death is a boxed copy, so write it back after changing it.
                Fields.Set(death, "value", exit.Spawn);
                Fields.Set(__instance, "spawnPointInAnotherScene", death);
            });
        }

        public static void TotemGiven(object __instance)
        {
            Guard("totem", () =>
            {
                var give = (Component)__instance;
                string guid;
                if (!plan.Totems.TryGetValue(Keys.PathOf(give), out guid)) return;
                var data = Registry.Item(guid);
                if (data == null) Missing("totem", guid);
                else Fields.Set(__instance, "data", data);
            });
        }

        /// Which card an event hands over. Most events give a quest card the
        /// game looks up by name later, and the plan skips those, so a lookup
        /// miss here is normal.
        public static void CardGiven(object __instance)
        {
            Guard("card gift", () =>
            {
                var give = (Component)__instance;
                var key = Keys.Uuid(give) ?? Keys.PathOf(give);
                string guid;
                if (!plan.Grants.TryGetValue(key, out guid)) return;
                var data = Registry.Card(guid);
                if (data == null) Missing("card", guid);
                else Fields.Set(__instance, "data", data);
            });
        }

        /// A blood tear bought on the skill tree pays out a card or a totem,
        /// from that kind's shuffled pool. The node is named by its realm and
        /// what it gives.
        public static void SkillBought(object __instance)
        {
            Guard("skill reward", () =>
            {
                var node = (UnityEngine.Object)__instance;
                var key = Keys.Node(node);
                string guid;
                string field;
                if (plan.Grants.TryGetValue(key, out guid)) field = "card";
                else if (plan.Totems.TryGetValue(key, out guid)) field = "totem";
                else return;
                var data = Registry.Card(guid) ?? Registry.Item(guid);
                if (data == null) Missing(field, guid);
                else Fields.Set(node, field, data);
            });
        }

        /// A reward card no reward hands over any more becomes craftable and
        /// shows in the book like any other card. Both the book reveal and the
        /// juice bar read this.
        public static void CardCraftable(object __instance, ref bool __result)
        {
            if (__result || plan.Crafted.Count == 0) return;
            var guid = Fields.Get(__instance, "uniqueIdentifier") as string;
            if (guid == null || !plan.Crafted.Contains(guid)) return;
            __result = Fields.Property(__instance, "IsCorrectMode") as bool? ?? false;
        }

        /// Card realms live on the card assets, so they are set once, when the
        /// card list is up. Changes are in memory only; quitting the game
        /// undoes them.
        public static void CardsLoaded(object __instance)
        {
            SetCardRealms();
            SetRecipes();
            Plugin.Instance.OnCardsLoaded(__instance);
        }

        /// Also called from Plugin.Rebuild, since the cards may already be
        /// loaded by then.
        public static void SetCardRealms()
        {
            if (plan == null) return;
            Guard("card realms", () =>
            {
                var moved = 0;
                foreach (var card in Registry.AllCards())
                {
                    var guid = Registry.GuidOf(card);
                    string realm;
                    if (guid == null || !plan.Cards.TryGetValue(guid, out realm)) continue;
                    var type = Registry.Realm(realm);
                    if (type == null) { Missing("realm", realm); continue; }
                    if (Fields.Set(card, "type", type)) moved++;
                }
                log.LogInfo("card realms: " + moved + " cards re-realmed");
            });
        }

        /// Recipes also live on the card assets, so they are written alongside
        /// the realms and undone the same way. The whole recipe is written,
        /// amounts and length too, since a reward card can take another card's
        /// recipe (see Plan.Lend).
        public static void SetRecipes()
        {
            if (plan == null) return;
            Guard("recipes", () =>
            {
                var moved = 0;
                foreach (var card in Registry.AllCards())
                {
                    var guid = Registry.GuidOf(card);
                    Ingredient[] wanted;
                    if (guid == null || !plan.Recipes.TryGetValue(guid, out wanted)) continue;
                    var recipe = Fields.Get(card, "recipe") as IList;
                    if (recipe == null) continue;
                    var lineType = recipe.GetType().GetGenericArguments()[0];
                    for (var i = 0; i < wanted.Length; i++)
                    {
                        if (wanted[i].Item == null) continue;
                        var data = Registry.Item(wanted[i].Item);
                        if (data == null) { Missing("ingredient", wanted[i].Item); continue; }
                        var line = Activator.CreateInstance(lineType, new object[] { data, wanted[i].Amount });
                        if (i < recipe.Count) recipe[i] = line;
                        else recipe.Add(line);
                        moved++;
                    }
                    while (recipe.Count > wanted.Length) recipe.RemoveAt(recipe.Count - 1);
                }
                log.LogInfo("recipes: " + moved + " ingredients written");
            });
        }

        /// Ro's energy per turn is the base mana on her stats, the same stats
        /// custom mode writes her health into. Totems and skills still add on
        /// top, as in vanilla.
        public static void PlayerAwake(object __instance)
        {
            Guard("player energy", () => SetEnergy(__instance));
        }

        static void SetEnergy(object player)
        {
            var stats = Fields.Get(player, "stats");
            if (stats == null) return;
            Fields.Set(stats, "maxMana", Plugin.Rule(Plugin.Instance.PlayerEnergy));
        }

        // --- scarce howls ------------------------------------------

        /// Crafting is free, so howls only buy skill points. This getter is the
        /// only place the cost is read, so the craft button, cost panel and
        /// payment all see zero.
        public static bool CardHowlCost(ref int __result)
        {
            if (!Plugin.Rule(Plugin.Instance.Scarce)) return true;
            __result = 0;
            return false;
        }

        /// A blood tear costs a flat 15 howls instead of starting at 5 and
        /// rising. The world holds a fixed number of howls, about 100 tears'
        /// worth, against the 76 the skill tree needs.
        public static bool TearHowlCost(ref int __result)
        {
            if (!Plugin.Rule(Plugin.Instance.Scarce)) return true;
            __result = 15;
            return false;
        }

        /// Beaten fights pay no howls, so they can't be farmed from spirits
        /// that respawn after a rest. Whether the arena was beaten is read when
        /// the fight starts: spirits without a death animation pay out one
        /// particle at a time, and the arena can be marked beaten before the
        /// last one lands. Howls from world events still count.
        public static bool HowlGained(int amount)
        {
            if (!Plugin.Rule(Plugin.Instance.Scarce) || amount <= 0) return true;
            var beaten = false;
            Guard("howl reward", () => beaten = InRefight());
            return !beaten;
        }

        /// Beaten fights drop no ingredients either, so what's in the world is
        /// all there is. On a first win the plan picks the drops: one per
        /// spirit the fight spawned, whatever happened to it, so a frozen
        /// spirit or one killed by a card still pays out. The game's rolled
        /// drops only lend their position and look. Runs as the drop flow
        /// starts, before it reads the list.
        public static void LootLanding(object __instance, object arena)
        {
            if (!Plugin.Rule(Plugin.Instance.Scarce)) return;
            Guard("loot drop", () =>
            {
                var loot = Fields.Get(__instance, "loot") as IList;
                var area = arena as Component;
                if (loot == null || area == null) return;
                var id = Registry.Uuid(area.gameObject);
                if (id != null && refights.Contains(id))
                {
                    loot.Clear();
                    return;
                }

                List<Plan.Drop> planned;
                var infoType = AccessTools.TypeByName("LootDropInfo");
                if (infoType == null || !plan.Drops.TryGetValue(Keys.Uuid(area), out planned)) return;
                var visuals = Manager("CommonVisuals");
                var player = Manager("Player");
                var made = new List<object>();
                for (var i = 0; i < planned.Count; i++)
                {
                    var data = Registry.Item(planned[i].Item);
                    if (data == null) { Missing("ingredient", planned[i].Item); continue; }
                    var like = loot.Count == 0 ? null : loot[i % loot.Count];
                    var material = like != null ? Fields.Get(like, "material")
                        : visuals == null ? null : Fields.Get(visuals, "worldItem");
                    var tile = like != null ? Fields.Get(like, "tileOfDeath")
                        : player == null ? null : Fields.Property(player, "Cell");
                    made.Add(Activator.CreateInstance(infoType, new[]
                    {
                        data, material, tile ?? Vector3Int.zero, planned[i].Elder,
                    }));
                }
                loot.Clear();
                foreach (var info in made) loot.Add(info);
            });
        }

        /// Drops are picked up as they land, so the fight's next respawn has
        /// nothing to clear.
        public static void LootLanded(object drop)
        {
            if (!Plugin.Rule(Plugin.Instance.Scarce)) return;
            Guard("loot pickup", () =>
            {
                var item = drop as Behaviour;
                if (item != null && item.isActiveAndEnabled) Call(item, "PickUp");
            });
        }

        /// A respawning fight clears whatever it dropped earlier, saved or not.
        /// Leaving the area while drops are landing can leave some behind, so
        /// collect those first. Not through PickUp: the area may still be
        /// loading, and PickUp removes the item before the step that can fail.
        public static void LeftoversCollected(object __instance)
        {
            if (!Plugin.Rule(Plugin.Instance.Scarce)) return;
            Guard("loot leftovers", () =>
            {
                var id = Registry.Uuid(((Component)__instance).gameObject);
                var data = Manager("LiveGameDataManager");
                var itemType = AccessTools.TypeByName("WorldItem");
                var ingredientType = AccessTools.TypeByName("IngredientData");
                if (id == null || data == null || itemType == null || ingredientType == null) return;
                var add = AccessTools.Method(data.GetType(), "AddIngredientToCollection");
                if (add == null) return;
                foreach (var found in UnityEngine.Object.FindObjectsOfType(itemType))
                {
                    var item = found as Behaviour;
                    if (item == null || Fields.Get(item, "spawnerID") as string != id) continue;
                    var what = Fields.Get(item, "data");
                    if (!ingredientType.IsInstanceOfType(what)) continue;
                    add.Invoke(data, new[] { what, 1 });
                    item.gameObject.SetActive(false);
                }
            });
        }

        /// Under scarce howls the stag fights are left out
        public static bool StagRemoved(object __instance)
        {
            if (plan == null || !Plugin.Rule(Plugin.Instance.Scarce)) return true;
            var at = ((Component)__instance).transform;
            while (at != null && at.name != "StagEvents") at = at.parent;
            if (at == null) return true;
            at.gameObject.SetActive(false);
            return false;
        }

        /// Records at combat start whether this arena was already cleared.
        /// defeatedArenas is saved with the game, so this survives quitting and
        /// reloading.
        public static void CombatStarted(object __instance)
        {
            Guard("re-fight", () =>
            {
                var arena = (Component)__instance;
                var id = Registry.Uuid(arena.gameObject);
                if (id == null) return;
                lastArena = id;
                var data = Manager("LiveGameDataManager");
                var defeated = data == null ? null
                    : Fields.Get(data, "defeatedArenas") as ICollection<string>;
                if (defeated == null) return;
                if (defeated.Contains(id)) refights.Add(id);
                else refights.Remove(id);
            });
        }

        static bool InRefight()
        {
            // The arena is null between fights, but a howl can land after its
            // fight ends, so fall back to the one just started.
            var combat = Manager("CombatEntityManager");
            var arena = combat == null ? null
                : Fields.Get(combat, "currentArena") as Component;
            var id = arena == null ? lastArena : Registry.Uuid(arena.gameObject);
            return id != null && refights.Contains(id);
        }

        /// Ro keeps the howls she brought into the fight, so there's no pile to
        /// run back for. Howls won before dying are still lost, as in vanilla:
        /// the arena respawns its spirits when she falls, so keeping them would
        /// allow endless farming. The death flow zeroes her count right after,
        /// so restore it next frame.
        public static bool HowlsDropped(ref int value)
        {
            if (!Plugin.Rule(Plugin.Instance.Scarce) || value <= 0) return true;
            value = 0;
            var data = Manager("LiveGameDataManager");
            var player = Manager("Player");
            var kept = player == null ? 0
                : Fields.Get(player, "soulBeforeCombat") as int? ?? 0;
            if (data == null || kept <= 0) return true;
            keepingHowls = true;
            Plugin.Instance.StartCoroutine(GiveHowlsBack(data, kept));
            return true;
        }

        static IEnumerator GiveHowlsBack(object data, int kept)
        {
            yield return null;
            Fields.Set(data, "playerSoulAmount", kept);
            keepingHowls = false;
        }

        /// Skips the howls flying out of Ro while they stay. The sacred space
        /// still plays its own, since losing them is the point there.
        public static bool HowlsFlyingAway()
        {
            return !keepingHowls;
        }

        // --- difficulty ------------------------------------------------------

        const int MaxTearHealth = 75;

        /// The flag the moose at the waterfalls sets when his roar opens the
        /// paths out of the first forest.
        const int MooseExplainsGreatSpirits = 2;

        /// Enemy health grows 1% for each blood tear placed on the skill tree.
        /// Runs after the game applies the custom mode health percent, so the
        /// two stack. Allies and environmental objects are left alone, as the
        /// game does for its own percent.
        public static void EnemySpawned(object __result, bool playerAlly)
        {
            if (playerAlly || __result == null || !Plugin.Rule(Plugin.Instance.TearHealth)) return;
            Guard("tear health", () =>
            {
                if (Fields.Property(__result, "IsSpecialTile") as bool? ?? true) return;
                var extra = TearsPlaced();
                var stats = Fields.Get(__result, "stats");
                if (extra <= 0 || stats == null) return;
                foreach (var field in new[] { "health", "maxHealth" })
                {
                    var health = Fields.Get(stats, field) as int?;
                    if (health == null) continue;
                    Fields.Set(stats, field,
                               Mathf.Max(1, Mathf.RoundToInt(health.Value * (100f + extra) / 100f)));
                }
            });
        }

        /// Tears spent on skills, whole or part way. Unspent tears don't count.
        static int TearsPlaced()
        {
            var data = Manager("LiveGameDataManager");
            var slots = data == null ? null : Fields.Get(data, "skillSlotinfo") as IEnumerable;
            if (slots == null) return 0;
            var placed = 0;
            foreach (var slot in slots)
                if (slot != null) placed += Fields.Get(slot, "progression") as int? ?? 0;
            return Math.Min(placed, MaxTearHealth);
        }

        /// Sets the moose's flag, which the first forest checks every frame to
        /// clear its blockers and load the paths beyond. Runs on every load, so
        /// a save made before this was turned on gets it too.
        public static void OpenPassages()
        {
            if (!Plugin.Rule(Plugin.Instance.OpenWorld)) return;
            var data = Manager("LiveGameDataManager");
            if (data == null) return;
            Guard("open world", () =>
            {
                var flags = Fields.Get(data, "alterations");
                var type = AccessTools.TypeByName("PermanentAlteration");
                if (flags == null || type == null) return;
                Call(flags, "TryAdd", Enum.ToObject(type, MooseExplainsGreatSpirits));
            });
        }

        // --- every card in the book ------------------------------------------

        /// Runs after the save is read in, for both new and loaded games. It's
        /// the only hook that covers both.
        public static void GameLoaded()
        {
            // A different save means a different set of cleared arenas.
            refights.Clear();
            lastArena = null;
            RevealAllCards();
            UnlockMenus();
            OpenPassages();
        }

        public static void ProfileDeleted(int profileSaveSlot)
        {
            Plugin.Instance.ForgetProfile(profileSaveSlot);
        }

        /// Every craftable card is known from the start, so the juice bar has
        /// nothing left to reveal. Realms are unlocked too, or the cards would
        /// sit behind locked tabs.
        public static void RevealAllCards()
        {
            if (!Plugin.Rule(Plugin.Instance.RevealCards)) return;
            var data = Manager("LiveGameDataManager");
            if (data == null) return;
            Guard("card reveal", () =>
            {
                var known = Fields.Get(data, "unlockedCardBluePrints");
                var ingredients = Fields.Get(data, "knownIngredients");
                var all = Fields.Get(data, "allCards");
                var cards = all == null ? null : Fields.Get(all, "list") as IEnumerable;
                if (known == null || cards == null) return;

                var shown = 0;
                foreach (var card in cards)
                {
                    if (card == null) continue;
                    var type = Fields.Get(card, "type") as UnityEngine.Object;
                    if (type == null || !Discovery.IsRealm(type)) continue;
                    if (!(Fields.Property(card, "allowUnlockByCrafting") as bool? ?? false))
                        continue;
                    var info = Fields.Get(type, "persistantInfo");
                    if (info != null) Fields.Set(info, "cardsAreUnlocked", true);
                    if (!(Call(known, "TryAdd", card) as bool? ?? false)) continue;
                    shown++;
                    Learn(ingredients, card);
                }
                log.LogInfo("cards: " + shown + " blueprints revealed");
            });
        }

        /// What a revealed card is made of counts as known, the way it does
        /// when the game reveals one itself.
        static void Learn(object ingredients, object card)
        {
            var recipe = Fields.Get(card, "recipe") as IList;
            if (ingredients == null || recipe == null) return;
            foreach (var item in recipe)
            {
                var what = item == null ? null : Fields.Get(item, "data");
                if (what != null) Call(ingredients, "Add", what);
            }
        }

        static object Call(object target, string name, object argument)
        {
            var method = AccessTools.Method(target.GetType(), name);
            return method == null ? null : method.Invoke(target, new[] { argument });
        }

        static object Call(object target, string name)
        {
            var method = AccessTools.Method(target.GetType(), name, Type.EmptyTypes);
            return method == null ? null : method.Invoke(target, null);
        }

        /// Presses the game's own Esc handler on the first frame. Setting
        /// escPressed stops its Update from pressing it again.
        public static void LogosShowing(object __instance)
        {
            if (logosSkipped) return;
            logosSkipped = true;
            Guard("logo intro", () =>
            {
                Fields.Set(__instance, "escPressed", true);
                skipLogos.Invoke(__instance, null);
            });
        }

        public static void CutsceneStarted(object __instance)
        {
            Guard("cutscene", () =>
            {
                if (!IsIntro(__instance)) return;
                Fields.Set(__instance, "shaderTweenInDurationFirstFrame", 0f);
                var frames = Fields.Get(__instance, "frames") as IEnumerable;
                if (frames == null) return;
                foreach (var frame in frames)
                {
                    if (frame == null) continue;
                    foreach (var timing in FrameTimings) Fields.Set(frame, timing, 0f);
                    Fields.Set(frame, "audio", null);
                }
            });
        }

        // --- no tutorial ----------------------------------------------------

        static readonly string[] MenuFlags =
        {
            "collectionUnlocked", "totemMenuUnlocked", "progressionUnlocked",
            "mapUnlocked", "overworldMapUnlocked", "fastTravelUnlocked",
        };

        const string MapButton = "PlayerIsPressingMapButton";
        const string TotemButton = "PlayerIsPressingTotemButton";
        const string CardButton = "PlayerIsPressingCardManagerButton";

        // A button we press for the player, and the frame it was pressed on.
        static string fakeButton;
        static int fakeFrame;

        /// The tutorial's first event hides every tab when its scene loads,
        /// including when a save made there is loaded. Saving is left alone,
        /// since early events act differently while it's off. Games that aren't
        /// randomized keep the tutorial.
        public static void UnlockMenus()
        {
            if (!Plugin.Randomized) return;
            var data = Manager("LiveGameDataManager");
            if (data == null) return;
            Guard("menu unlock", () =>
            {
                foreach (var flag in MenuFlags) Fields.Set(data, flag, true);
            });
        }

        /// Hides tutorial tips, except ones explaining why something failed.
        /// The map tip is followed by a wait for the map button, so that button
        /// gets pressed instead.
        public static bool TipShown(string text)
        {
            if (!Plugin.Randomized) return true;
            var show = true;
            Guard("tutorial tip", () =>
            {
                var tips = Manager("TutorialTextManager");
                if (tips == null) return;
                // The fast travel tip's event blocks input, so the player
                // can't open the map without it. Keep it.
                if (text == Translate(tips, "cannotFastTravelWithKeyItemCardTip")
                    || text == Translate(tips, "notIncludedInDemoTip")
                    || text == Translate(tips, "openMapToFastTravelTip"))
                    return;
                show = false;
                var name = AccessTools.Method(tips.GetType(), "GetButtonName");
                var key = name == null ? null
                    : name.Invoke(null, new object[] { "OpenMap", "1" }) as string;
                if (key != null && text == Translate(tips, "rightClickToOpenMapTip", key))
                    Press(MapButton);
            });
            return show;
        }

        /// For tips that don't hold up an event.
        public static bool SkipTip()
        {
            return !Plugin.Randomized;
        }

        /// The event waits for the totem button right after this tip.
        public static void TotemTip()
        {
            if (Plugin.Randomized) Press(TotemButton);
        }

        /// The event waits for the card menu to open right after this tip.
        public static void CraftingTip()
        {
            if (Plugin.Randomized) Press(CardButton);
        }

        static void Press(string button)
        {
            fakeButton = button;
            fakeFrame = -1;
        }

        /// A faked press reads true for one frame, starting from the first
        /// check, like a real press.
        public static void ButtonPressed(MethodBase __originalMethod, ref bool __result)
        {
            if (fakeButton == null || __originalMethod.Name != fakeButton) return;
            if (fakeFrame < 0) fakeFrame = Time.frameCount;
            if (fakeFrame == Time.frameCount) __result = true;
            else fakeButton = null;
        }

        static string Translate(object tips, string field, string parameter = null)
        {
            var data = Fields.Get(tips, field);
            if (data == null) return null;
            var args = parameter == null ? Type.EmptyTypes : new[] { typeof(string) };
            var method = AccessTools.Method(data.GetType(), "Translation", args);
            if (method == null) return null;
            return method.Invoke(data, parameter == null ? null : new object[] { parameter }) as string;
        }

        // --- auto text -------------------------------------------------------

        // The dialogue line on screen, and when it showed up.
        static object shownLine;
        static float shownAt;

        /// Holds continue while dialogue is up; the same press finishes a line,
        /// then moves on. Lines over a character's head have no box and show at
        /// once, so they get two seconds first. Choices are buttons, so they
        /// still wait.
        public static void ContinuePressed(ref bool __result)
        {
            if (__result || !Plugin.Instance.AutoText.Value) return;
            var dialogue = Manager("DialogueManager");
            if (dialogue == null || Fields.Get(dialogue, "currentDialogue") == null) return;
            var line = Fields.Get(dialogue, "currentContent");
            if (line == null) return;
            var data = Manager("LiveGameDataManager");
            if (data != null && (Fields.Get(data, "gamePaused") as bool? ?? false)) return;

            if (line != shownLine)
            {
                shownLine = line;
                shownAt = Time.time;
            }
            var panel = Fields.Get(dialogue, "visualPanel") as Component;
            if ((panel != null && panel.gameObject.activeInHierarchy)
                || Time.time - shownAt >= 2f)
                __result = true;
        }

        // --- helpers ---------------------------------------------------------

        static bool IsIntro(object sequence)
        {
            var manager = Manager("CutsceneManager");
            if (manager == null) return false;
            return ReferenceEquals(Fields.Get(manager, "introSequence"), sequence);
        }

        /// One of the game's manager singletons. Instance is on a generic base
        /// class, so it's easier to ask Unity for the object. Cached until its
        /// scene unloads and it turns null.
        internal static UnityEngine.Object Manager(string name)
        {
            UnityEngine.Object found;
            if (managers.TryGetValue(name, out found) && found != null) return found;
            var type = AccessTools.TypeByName(name);
            if (type == null) return null;
            managers[name] = found = UnityEngine.Object.FindObjectOfType(type);
            return found;
        }

        static void Guard(string what, Action body)
        {
            try { body(); }
            catch (Exception e) { log.LogError(what + " patch failed: " + e); }
        }

        static readonly HashSet<string> complained = new HashSet<string>();

        static void Missing(string kind, string id)
        {
            if (!complained.Add(kind + " " + id)) return;
            log.LogWarning("no " + kind + " called " + id + " is loaded — left as it was");
        }
    }
}
