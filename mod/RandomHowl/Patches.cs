using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RandomHowl
{
    /// Where the shuffle actually lands.
    ///
    /// Each patch runs just before the game reads the field it is about to
    /// use, so nothing on disk changes and nothing has to be timed against
    /// scene loading. Writing the same value twice is harmless — every patch
    /// writes what the plan says, never a relative change.
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

        // Every wait on a cutscene frame. Zero them all and the frame is over
        // in a tick. Frame audio is null-checked by the game, frame content is
        // not, so the images stay and only the waiting goes.
        static readonly string[] FrameTimings =
        {
            "imagesFadeInDuration", "durationWithoutText", "textFadeInDuration",
            "staticTextDuration", "textFadeOutDuration", "imagesFadeOutDuration",
        };

        /// The logo screens are gone before the world scan finishes, so this
        /// goes on at Awake and stays on — Install runs far too late for it.
        public static void InstallExtras(Harmony harmony, ManualLogSource logger, bool logos)
        {
            log = logger;
            // Ro is in the always-loaded manager scene, so his Awake can run
            // before the world scan is done. This one goes on early with the
            // rest of the extras and reads the config as it fires.
            Hook(harmony, "Player", "Awake", nameof(PlayerAwake), true);

            // The howl rules and the card reveal are settings, not shuffles:
            // they read the config as they fire, so they go on once and stay
            // on whatever the seed does. The reveal in particular has to be
            // here — the data manager loads the save before the world scan
            // that installs the shuffle has finished.
            Hook(harmony, "LiveGameDataManager", "OnGameLoaded", nameof(GameLoaded), true);
            // Cards come up with the data manager, whose Start can run before
            // the world scan finishes and installs the shuffle — so this goes
            // on early with the rest of the extras. CardsLoaded tolerates an
            // empty plan; the roll after the scan writes the realms.
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

            // The tutorial hides menu tabs and fast travel until you get far
            // enough. Shuffled caves can lead out of the first forest before
            // then, with no way back, so everything is open from the start.
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
            // Update, not Start: whatever frame the plugin loads on, the next
            // frame of the logo scene runs this.
            Hook(harmony, "LogoIntroHandler", "Update", nameof(LogosShowing));
        }

        public static void Install(Harmony harmony, ManualLogSource logger, Plan shuffled,
                                   bool intro)
        {
            plan = shuffled;
            log = logger;
            skipIntro = intro;

            Hook(harmony, "WorldItem", "OnEnable", nameof(ItemAppeared));
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

        public static void ItemAppeared(object __instance)
        {
            Guard("world item", () =>
            {
                var item = (Component)__instance;
                string guid;
                if (!plan.Ingredients.TryGetValue(Keys.Uuid(item), out guid)) return;
                var data = Registry.Item(guid);
                if (data == null) Missing("ingredient", guid);
                else Fields.Set(__instance, "data", data);
            });
        }

        public static void ArenaReady(object __instance)
        {
            Guard("arena", () =>
            {
                var arena = (Component)__instance;
                string[] wanted;
                if (!plan.Enemies.TryGetValue(Keys.Uuid(arena), out wanted)) return;
                var prefabs = Fields.Get(__instance, "enemyPrefabs") as IList;
                if (prefabs == null) return;
                for (var i = 0; i < wanted.Length && i < prefabs.Count; i++)
                {
                    if (wanted[i] == null) continue;
                    var prefab = Registry.Enemy(wanted[i]);
                    if (prefab == null) Missing("enemy", wanted[i]);
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

        /// The only caves whose fight sends you outside when you die, and
        /// their one way out. Every exit event in each leads to the same place.
        static readonly Dictionary<string, string> DeathExits = new Dictionary<string, string>
        {
            { "MoorsEggCave", "Root/ExitCave" },
            { "CliffsMiniBossCave", "Root/ExitCave" },
        };

        /// Dying in those fights puts you where the cave's exit now leads,
        /// not outside its vanilla mouth. Set as the fight starts, since the
        /// death flow reads it right after the fight ends.
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
        /// game looks for by name further along, and the plan leaves those out
        /// — so missing the lookup here is the ordinary case.
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

        /// A blood tear bought off the skill tree pays out in a card or a
        /// totem. Either came from the pool its kind is shuffled in, and the
        /// node is named by the realm it sits in and what it hands over.
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

        /// Card realms live on the card assets themselves, so they are set
        /// once, as soon as the card list is up. Assets are only changed in
        /// memory — quitting the game puts them back.
        public static void CardsLoaded(object __instance)
        {
            SetCardRealms();
            SetRecipes();
            Plugin.Instance.OnCardsLoaded(__instance);
        }

        /// Also called from the menu on Apply: the cards are already loaded by
        /// then, so nothing would put a new seed on them otherwise.
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

        /// What each card is crafted from sits on the card asset as well, so
        /// it goes on beside the realms and comes off the same way. Only the
        /// ingredient moves — the quantity beside it stays as it was.
        public static void SetRecipes()
        {
            if (plan == null) return;
            Guard("recipes", () =>
            {
                var moved = 0;
                foreach (var card in Registry.AllCards())
                {
                    var guid = Registry.GuidOf(card);
                    string[] wanted;
                    if (guid == null || !plan.Recipes.TryGetValue(guid, out wanted)) continue;
                    var recipe = Fields.Get(card, "recipe") as IList;
                    if (recipe == null) continue;
                    for (var i = 0; i < wanted.Length && i < recipe.Count; i++)
                    {
                        if (wanted[i] == null || recipe[i] == null) continue;
                        var data = Registry.Item(wanted[i]);
                        if (data == null) { Missing("ingredient", wanted[i]); continue; }
                        if (Fields.Set(recipe[i], "data", data)) moved++;
                    }
                }
                log.LogInfo("recipes: " + moved + " ingredients written");
            });
        }

        /// Ro's energy per turn is the base mana on his stats, which is what
        /// the game's own custom mode writes his health into. Totems and the
        /// skill tree still add on top, exactly as they do in vanilla.
        public static void PlayerAwake(object __instance)
        {
            Guard("player energy", () => SetEnergy(__instance));
        }

        /// Also called from the menu on Apply. Resources hands back the Player
        /// prefab as well as any Ro already standing in a scene, and both want
        /// writing — the prefab so the next one spawns with it.
        public static void SetPlayerEnergy()
        {
            if (Registry.PlayerType == null) return;
            Guard("player energy", () =>
            {
                foreach (var player in Resources.FindObjectsOfTypeAll(Registry.PlayerType))
                    if (player != null) SetEnergy(player);
            });
        }

        static void SetEnergy(object player)
        {
            var stats = Fields.Get(player, "stats");
            if (stats == null) return;
            Fields.Set(stats, "maxMana", Plugin.Instance.PlayerEnergy.Value);
        }

        // --- scarce howls ------------------------------------------

        /// Crafting is free, so the skill tree is the only thing howls buy.
        /// The getter is the one place the cost is read, so the craft button,
        /// the cost panel and the spend all see the same nothing.
        public static bool CardHowlCost(ref int __result)
        {
            if (!Plugin.Instance.Scarce.Value) return true;
            __result = 0;
            return false;
        }

        /// A blood tear costs a flat fifteen howls instead of starting at five
        /// and climbing. The world holds a fixed number of howls, and at this
        /// rate that is about a hundred tears against the 76 the skill tree wants.
        public static bool TearHowlCost(ref int __result)
        {
            if (!Plugin.Instance.Scarce.Value) return true;
            __result = 15;
            return false;
        }

        /// Nothing is paid out in a combat you have already beaten, so howls
        /// can't be farmed off the enemies a rest respawns. The arena's state
        /// is read when the fight begins, not when the howl lands, because an
        /// enemy with no death animation pays out particle by particle and
        /// the arena can be marked beaten before the last one arrives. Only
        /// combat is blocked: howls handed over by a world event still count.
        public static bool HowlGained(int amount)
        {
            if (!Plugin.Instance.Scarce.Value || amount <= 0) return true;
            var beaten = false;
            Guard("howl reward", () => beaten = InRefight());
            return !beaten;
        }

        /// No ingredients drop in a combat you have already beaten either, so
        /// the ingredients in the world are all there is. On the first win the
        /// plan says what drops: one item per enemy the fight spawned, whatever
        /// became of it. So a frozen enemy that never thawed, or one a card
        /// killed without its reward, still pays out. The drops the game rolled
        /// only lend their spot and look. This runs as the drop flow is started,
        /// before it reads the list.
        public static void LootLanding(object __instance, object arena)
        {
            if (!Plugin.Instance.Scarce.Value) return;
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
                        data, material, tile ?? Vector3Int.zero, planned[i].Elite,
                    }));
                }
                loot.Clear();
                foreach (var info in made) loot.Add(info);
            });
        }

        /// A drop is picked up as soon as it lands, so nothing is left lying
        /// about for the fight's next respawn to clear away.
        public static void LootLanded(object drop)
        {
            if (!Plugin.Instance.Scarce.Value) return;
            Guard("loot pickup", () =>
            {
                var item = drop as Behaviour;
                if (item != null && item.isActiveAndEnabled) Call(item, "PickUp");
            });
        }

        /// A fight that respawns clears away whatever it dropped before and is
        /// still lying there, saved or not. Leaving the area while drops are
        /// still landing can leave some behind, so add those to the collection
        /// first. Not through PickUp: the area may still be loading, and PickUp
        /// takes the item out of the world before the part that can fail.
        public static void LeftoversCollected(object __instance)
        {
            if (!Plugin.Instance.Scarce.Value) return;
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

        /// Snapshot at combat start: has this arena already been cleared?
        /// defeatedArenas is part of the game's save, so this still holds
        /// after quitting and loading the save again.
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
            // The arena is null between combats, but a howl can outlive the
            // fight that paid it, so fall back to the one just started.
            var combat = Manager("CombatEntityManager");
            var arena = combat == null ? null
                : Fields.Get(combat, "currentArena") as Component;
            var id = arena == null ? lastArena : Registry.Uuid(arena.gameObject);
            return id != null && refights.Contains(id);
        }

        /// Ro keeps the howls he came into the fight with, so there is no
        /// pile to run back for. What he won before dying is still lost, the
        /// way it is in vanilla: an arena respawns its enemies when he falls,
        /// so letting him keep the win and kill them again would be an endless
        /// supply. The death flow zeroes his count a moment later, so put the
        /// stash back next frame.
        public static bool HowlsDropped(ref int value)
        {
            if (!Plugin.Instance.Scarce.Value || value <= 0) return true;
            value = 0;
            var data = Manager("LiveGameDataManager");
            var player = Manager("Player");
            var kept = player == null ? 0
                : Fields.Get(player, "howlBeforeCombat") as int? ?? 0;
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

        /// The howls-fly-out-of-Ro animation, while they are staying put. The
        /// sacred space still plays its own — that loss is the point of it.
        public static bool HowlsFlyingAway()
        {
            return !keepingHowls;
        }

        // --- every card in the book ------------------------------------------

        /// Runs for a new game and for a loaded one, after the save has been
        /// read back in, which is the only moment that covers both.
        public static void GameLoaded()
        {
            // A different save means a different set of cleared arenas.
            refights.Clear();
            lastArena = null;
            RevealAllCards();
            UnlockMenus();
        }

        /// Every craftable card is known from the start, so the juice bar that
        /// reveals four at a time has nothing left to reveal. The realms are
        /// opened with them, or the cards would be behind a locked tab.
        public static void RevealAllCards()
        {
            if (!Plugin.Instance.RevealCards.Value) return;
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

        /// The game's own Esc handler, pressed for you on the first frame.
        /// escPressed is what stops its Update from doing it again.
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

        /// The tutorial's first event turns every tab off when its scene
        /// loads, including when a save made there is loaded again. Saving is
        /// left alone: the early events act differently while it is off.
        public static void UnlockMenus()
        {
            var data = Manager("LiveGameDataManager");
            if (data == null) return;
            Guard("menu unlock", () =>
            {
                foreach (var flag in MenuFlags) Fields.Set(data, flag, true);
            });
        }

        /// Hides tutorial tips. The ones that explain why something failed
        /// still show. The map tip is followed by a wait for the map button,
        /// so that button gets pressed instead.
        public static bool TipShown(string text)
        {
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
            return false;
        }

        /// The event waits for the totem button right after this tip.
        public static void TotemTip()
        {
            Press(TotemButton);
        }

        /// The event waits for the card menu to open right after this tip.
        public static void CraftingTip()
        {
            Press(CardButton);
        }

        static void Press(string button)
        {
            fakeButton = button;
            fakeFrame = -1;
        }

        /// A pressed button reads true for one frame, starting the first time
        /// anything checks it, the same as a real press.
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

        /// Holds the continue button down while a dialogue is up. The same
        /// press writes out the rest of a line and then moves to the next.
        /// Lines over a character's head have no box and show all at once,
        /// so those get two seconds to be read first. Choices are buttons,
        /// so they still wait for a pick.
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

        /// One of the game's manager singletons. Its Instance sits behind a
        /// generic base class, so it is less work to ask Unity for the object.
        /// Kept until the scene it lives in goes away and it turns null.
        static UnityEngine.Object Manager(string name)
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
