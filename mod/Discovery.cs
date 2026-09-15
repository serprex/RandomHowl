using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using BepInEx.Logging;

namespace RandomHowl
{
    /// Scans the world at startup, before any shuffling.
    ///
    /// Loads each level scene additively, records every shufflable slot and its
    /// vanilla value, then starts unloading it and moves straight on to loading
    /// the next level. Only yields after 20 ms, so menu stays responsive
    public static class Discovery
    {
        static ManualLogSource log;

        // Time since we last gave a frame back to the game.
        static readonly Stopwatch sinceYield = new Stopwatch();
        const long YieldBudgetMs = 20;

        public static IEnumerator Scan(ManualLogSource logger, Action<World> done)
        {
            log = logger;
            var world = new World();
            var clock = Stopwatch.StartNew();

            ResolveKinds();

            var count = SceneManager.sceneCountInBuildSettings;
            log.LogInfo("discovery: scanning " + count + " build scenes");

            // By default Unity only spends a few ms per frame finishing a scene
            // load, so most of the scan is waiting. Let it use more while we
            // scan.
            var priority = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = ThreadPriority.High;
            sinceYield.Restart();
            for (var i = 0; i < count; i++)
            {
                if (!SkipScenes.Contains(SceneNameAt(i)))
                {
                    // Step through by hand rather than yielding the inner
                    // coroutine, so moving between the two costs no frame.
                    var inner = LoadAndScan(i, world);
                    while (inner.MoveNext()) yield return inner.Current;
                }
                if (sinceYield.ElapsedMilliseconds > YieldBudgetMs)
                {
                    yield return null;   // keep the splash screen responsive
                    sinceYield.Restart();
                }
            }

            // Let the last level finish unloading before the game carries on.
            if (unloading != null)
                while (!unloading.isDone) yield return null;
            unloading = null;

            // Put it back, unless the game changed it meanwhile.
            if (Application.backgroundLoadingPriority == ThreadPriority.High)
                Application.backgroundLoadingPriority = priority;

            log.LogInfo(string.Format(
                        "discovery: {0} pickups, {1} totems, {2} cave mouths, {3} arena slots "
                        + "over {4} spirits in {5:0.0}s",
                        world.Ingredients.Count, world.Totems.Count, world.Entrances.Count,
                        world.Spirits.Count, world.Rarity.Count, clock.ElapsedMilliseconds / 1000f));

            try { ReadSpawns(world); }
            catch (Exception e) { log.LogWarning("could not read the spawn points: " + e.Message); }

            done(world);
        }

        // --- values -------------------------------------------------------

        /// Cards and spoiler log names come from the game's data manager, which
        /// only has them once it starts, after the scan. Plugin calls this
        /// then.
        public static void CollectValues(World world, object manager)
        {
            // Drop any index built during boot, when little was loaded yet.
            Registry.Forget();

            var items = ListOf(manager, "allItems", Registry.AllItems());
            var cards = ListOf(manager, "allCards", Registry.AllCards());
            var types = ListOf(manager, "allCardTypes", Registry.AllRealms());

            foreach (var item in items)
                world.names["item\t" + Registry.GuidOf(item)] = item.name;
            foreach (var card in cards)
                world.names["card\t" + Registry.GuidOf(card)] = card.name;
            foreach (var type in types)
                world.names["cardtype\t" + Registry.GuidOf(type)] = type.name;

            CollectCards(world, cards, types);
            CollectNodes(world);
        }

        /// Skill tree nodes hand over a card or totem like an event does, so
        /// they join those pools. Keys.Node names each node, since four of them
        /// are just called "Card".
        static void CollectNodes(World world)
        {
            world.Nodes.Clear();
            var cards = 0;
            var totems = 0;
            foreach (var node in Registry.AllNodes())
            {
                if (node == null) continue;
                var slot = Slot.Of(Keys.NodeScene, Keys.NodeKey(node), null);
                var card = Fields.Get(node, "card") as UnityEngine.Object;
                var totem = card == null ? Fields.Get(node, "totem") as UnityEngine.Object
                    : null;
                if (card != null)
                {
                    slot.Value = Registry.GuidOf(card);
                    cards++;
                }
                else if (totem != null)
                {
                    slot.Value = Registry.GuidOf(totem);
                    slot.Index = 1;
                    totems++;
                }
                else continue;
                if (slot.Value == null) continue;
                world.Nodes.Add(slot);
            }
            log.LogInfo("discovery: " + cards + " skill nodes give a card, "
                    + totems + " a totem");
        }

        /// The manager's own list, or whatever Unity has loaded if it has none.
        static List<UnityEngine.Object> ListOf(object manager, string field,
                IEnumerable<UnityEngine.Object> spare)
        {
            var found = new List<UnityEngine.Object>();
            var list = manager == null ? null : Fields.Get(manager, field) as IEnumerable;
            if (list != null)
                foreach (var entry in list)
                {
                    var obj = entry as UnityEngine.Object;
                    if (obj != null) found.Add(obj);
                }
            if (found.Count == 0)
                foreach (var obj in spare)
                    if (obj != null) found.Add(obj);
            return found;
        }

        /// Each card asset stores its realm. Only realms are shuffled; the
        /// basic, curse and key item piles stay put.
        static void CollectCards(World world, List<UnityEngine.Object> cards,
                List<UnityEngine.Object> types)
        {
            world.Cards.Clear();
            world.Recipes.Clear();
            world.PlusOnly.Clear();
            world.Realmless.Clear();
            world.RealmlessRecipes.Clear();
            world.Rewards.Clear();
            world.Copies.Clear();
            var realms = new HashSet<string>();
            var realmless = new HashSet<string>();
            foreach (var type in types)
            {
                var guid = Registry.GuidOf(type);
                if (guid == null) continue;
                if (IsRealm(type)) realms.Add(guid);
                else if (IsRealmless(type)) realmless.Add(guid);
            }

            foreach (var card in cards)
            {
                var guid = Registry.GuidOf(card);
                var type = Fields.Get(card, "type") as UnityEngine.Object;
                if (guid == null || type == null) continue;
                // Mode 2 is the alternate card set's own version of a card.
                if (Number(Fields.Get(card, "mode")) == 2) world.PlusOnly.Add(guid);
                if (Flag(card, "isProgressionCard") || Flag(card, "isEnvironmentalReward")
                    || Flag(card, "isGreatSpiritReward"))
                    world.Rewards.Add(guid);
                var realm = Registry.GuidOf(type);
                if (realm == null) continue;
                if (realmless.Contains(realm))
                {
                    world.Realmless[guid] = realm;
                    world.RealmlessRecipes[guid] = Recipe(card);
                    world.Copies[guid] = Copies(world, card, guid);
                }
                if (!realms.Contains(realm)) continue;
                world.Cards.Add(new Slot { Key = guid, Value = realm });
                world.Copies[guid] = Copies(world, card, guid);
                CollectRecipe(world, card, guid);
            }

            log.LogInfo("discovery: " + realms.Count + " realms over "
                    + world.Cards.Count + " cards, " + world.Recipes.Count
                    + " recipe slots");
        }

        /// What a card is crafted from, one slot per ingredient. Only called
        /// for realm cards, since spirits' cards carry recipes too and nobody
        /// crafts them.
        static void CollectRecipe(World world, UnityEngine.Object card, string guid)
        {
            var recipe = Fields.Get(card, "recipe") as IList;
            if (recipe == null) return;
            for (var i = 0; i < recipe.Count; i++)
            {
                var data = recipe[i] == null ? null
                    : Fields.Get(recipe[i], "data") as UnityEngine.Object;
                if (data == null) continue;
                var item = Registry.GuidOf(data);
                if (item == null) continue;
                var slot = Slot.Of(null, guid, item);
                slot.Index = i;
                slot.Amount = Number(Fields.Get(recipe[i], "quantity"));
                world.Recipes.Add(slot);
            }
        }

        /// Same, for a card outside the realms: every line in order, not as
        /// slots.
        static Ingredient[] Recipe(UnityEngine.Object card)
        {
            var found = new List<Ingredient>();
            var recipe = Fields.Get(card, "recipe") as IList;
            if (recipe == null) return found.ToArray();
            foreach (var line in recipe)
            {
                var data = line == null ? null : Fields.Get(line, "data") as UnityEngine.Object;
                var item = data == null ? null : Registry.GuidOf(data);
                if (item == null) continue;
                found.Add(new Ingredient { Item = item, Amount = Number(Fields.Get(line, "quantity")) });
            }
            return found.ToArray();
        }

        /// How many copies of a card can be crafted under the game's default
        /// limit, which goes by rarity. Custom mode's fixed 1, 2 or 3 is
        /// ignored. Reward cards get a number too, since the gift shuffle can
        /// make one craftable.
        static int Copies(World world, UnityEngine.Object card, string guid)
        {
            if (world.PlusOnly.Contains(guid)) return 0;
            switch (Number(Fields.Get(card, "rarity")))
            {
                case 0: return 4;       // basic
                case 1: return 2;       // common
                default: return 1;      // uncommon, shown as rare
            }
        }

        /// A card type that gates a card behind a region, not a special pile.
        internal static bool IsRealm(UnityEngine.Object type)
        {
            return (Fields.Get(type, "index") as int?) >= 0
                   && !Flag(type, "isDev") && !Flag(type, "isBasic")
                   && !Flag(type, "isSpiritCard") && !Flag(type, "isCurse")
                   && !Flag(type, "isKeyItemType") && !Flag(type, "isSpecialTileRewardType");
        }

        /// The pile outside every realm that the player still draws from: index
        /// 0, basic. Not the OVERWHELMED or special tile piles, which share the
        /// flag.
        static bool IsRealmless(UnityEngine.Object type)
        {
            return (Fields.Get(type, "index") as int?) == 0
                   && Flag(type, "isBasic") && !Flag(type, "isDev")
                   && !Flag(type, "isSpecialTileRewardType");
        }

        static bool Flag(object obj, string name)
        {
            return Fields.Get(obj, name) as bool? ?? false;
        }

        // --- level discovery ----------------------------------------------

        static readonly HashSet<string> SkipScenes = new HashSet<string>
        {
            "CompanyLogos", "TitleMenu", "Credits", "Main (Managers)",
            "CaveTemplate", "CaveCBackup", "PostGameClearingScene", "PostGameHutScene",
        };

        /// Scene name for a build index, before it is loaded.
        static string SceneNameAt(int index)
        {
            var path = SceneUtility.GetScenePathByBuildIndex(index);
            if (string.IsNullOrEmpty(path)) return "";
            var slash = path.LastIndexOf('/');
            if (slash >= 0) path = path.Substring(slash + 1);
            var dot = path.LastIndexOf('.');
            return dot > 0 ? path.Substring(0, dot) : path;
        }

        /// Loads one build scene additively, scans it, then unloads it. Loads
        /// by build index, since build settings names (TitleMenu, Main
        /// (Managers), ...) don't match the levelN files on disk. Runs as a
        /// coroutine so the splash screen doesn't freeze.
        static IEnumerator LoadAndScan(int index, World world)
        {
            var clock = Stopwatch.StartNew();
            var op = SceneManager.LoadSceneAsync(index, LoadSceneMode.Additive);
            if (op == null) yield break;
            while (!op.isDone)
            {
                yield return null;
                sinceYield.Restart();
            }
            var loaded = clock.ElapsedMilliseconds;

            // Look up by build index, not "last scene in the list". The game
            // loads its own scenes meanwhile, and unloading one of those would
            // break the title screen.
            var scene = SceneManager.GetSceneByBuildIndex(index);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                log.LogWarning("build scene " + index + " did not load — skipped");
                yield break;
            }

            var name = scene.name;
            var before = Count(world);
            clock.Restart();
            try
            {
                ScanScene(scene, world);
            }
            catch (Exception e)
            {
                log.LogWarning("scan of " + name + " failed: " + e.Message);
            }
            log.LogInfo(string.Format("scanned {0}: +{1} slots (load {2} ms, walk {3} ms)",
                                      name, Count(world) - before, loaded,
                                      clock.ElapsedMilliseconds));

            // Don't wait for the unload. Unity runs scene loads/unloads in order,
            // so next load starts immediately after
            unloading = SceneManager.UnloadSceneAsync(scene);
        }

        // The last scene we started unloading, which may not be done yet.
        static AsyncOperation unloading;

        static int Count(World w)
        {
            return w.Ingredients.Count + w.Totems.Count + w.Entrances.Count
                   + w.Spirits.Count + w.Grants.Count;
        }

        // --- slot scanning -------------------------------------------------

        delegate void Scanner(Component comp, string scene, World world);

        struct Kind
        {
            public Type Type;
            public Scanner Scan;
        }

        static readonly List<Kind> kinds = new List<Kind>();

        static void ResolveKinds()
        {
            kinds.Clear();
            AddKind("WorldItem", ScanIngredient);
            AddKind("EventComponentRecieveTotem", ScanTotem);
            AddKind("EventComponentRecieveCard", ScanGrant);
            AddKind("EnterOrExitCaveEvent", ScanEntrance);
            AddKind("CombatArena", ScanArena);
        }

        static void AddKind(string className, Scanner scan)
        {
            var type = AccessTools.TypeByName(className);
            if (type == null)
            {
                log.LogWarning("no " + className + " in this build — nothing of that "
                               + "kind will shuffle");
                return;
            }
            kinds.Add(new Kind { Type = type, Scan = scan });
        }

        /// Ask Unity for the four component types directly, instead of walking
        /// every GameObject.
        static void ScanScene(Scene scene, World world)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var kind in kinds)
                    foreach (var comp in root.GetComponentsInChildren(kind.Type, true))
                        if (comp != null) kind.Scan(comp, scene.name, world);
        }

        static readonly Type IngredientType = AccessTools.TypeByName("IngredientData");

        // Scene items are keyed by UUID, events by hierarchy path. Both stay
        // the same across runs.
        static void ScanIngredient(Component comp, string scene, World world)
        {
            var data = Fields.Get(comp, "data") as UnityEngine.Object;
            // Some pickups hand over a card for an event, like Scale Shield.
            // Only ingredients move.
            if (IngredientType != null && !IngredientType.IsInstanceOfType(data)) return;
            var guid = data == null ? null : Registry.GuidOf(data);
            var uuid = Registry.Uuid(comp.gameObject);
            if (uuid == null || guid == null) return;
            world.Ingredients.Add(Slot.Of(scene, uuid, guid));
        }

        static void ScanTotem(Component comp, string scene, World world)
        {
            var data = Fields.Get(comp, "data") as UnityEngine.Object;
            var guid = data == null ? null : Registry.GuidOf(data);
            var uuid = Registry.Uuid(comp.gameObject) ?? Keys.Path(comp.transform);
            if (uuid == null || guid == null) return;
            world.Totems.Add(Slot.Of(scene, uuid, guid));
        }

        /// Which card an event hands over. Only realm cards move, since most
        /// events give a quest card the game looks up by name. See
        /// Plan.BuildGrants.
        static void ScanGrant(Component comp, string scene, World world)
        {
            var data = Fields.Get(comp, "data") as UnityEngine.Object;
            var guid = data == null ? null : Registry.GuidOf(data);
            var uuid = Registry.Uuid(comp.gameObject) ?? Keys.Path(comp.transform);
            if (uuid == null || guid == null) return;
            world.Grants.Add(Slot.Of(scene, uuid, guid));
        }

        static void ScanEntrance(Component comp, string scene, World world)
        {
            var area = Fields.Get(comp, "exitAreaData") as UnityEngine.Object;
            var spawn = Fields.Get(comp, "targetSpawnPointID") as string;
            if (area == null) return;
            world.Entrances.Add(new Slot
            {
                Scene = scene,
                Key = Keys.Path(comp.transform),
                Value = area.name,
                Spawn = spawn,
                X = comp.transform.position.x,
                Y = comp.transform.position.y,
            });
        }

        /// The game's spawn point list per region, with each point's scene and
        /// position. A cave mouth only names a spawn point, so this tells us
        /// which scene it leads into.
        static void ReadSpawns(World world)
        {
            var type = AccessTools.TypeByName("RegionDataManager");
            var instance = type == null ? null : AccessTools.Property(type, "Instance");
            var manager = instance == null ? null : instance.GetValue(null, null);
            var regions = manager == null ? null : Fields.Get(manager, "regions") as IList;
            if (regions == null)
            {
                log.LogWarning("no RegionDataManager.regions — caves can't be paired, "
                               + "so ON leaves them alone");
                return;
            }
            foreach (var region in regions)
            {
                if (region == null) continue;
                foreach (var list in new[] { "waypoints", "caves" })
                {
                    var entries = Fields.Get(region, list) as IList;
                    if (entries == null) continue;
                    foreach (var entry in entries)
                    {
                        var id = entry == null ? null : Fields.Get(entry, "ID") as string;
                        if (string.IsNullOrEmpty(id) || world.Spawns.ContainsKey(id)) continue;
                        var position = Fields.Get(entry, "position");
                        var at = position is Vector2 ? (Vector2)position : Vector2.zero;
                        world.Spawns[id] = new Place
                        {
                            Scene = Fields.Get(entry, "scene") as string,
                            X = at.x,
                            Y = at.y,
                        };
                    }
                }
            }
            log.LogInfo("discovery: " + world.Spawns.Count + " spawn points");
        }

        static void ScanArena(Component comp, string scene, World world)
        {
            var uuid = Registry.Uuid(comp.gameObject);
            if (uuid == null) return;
            var prefabs = Fields.Get(comp, "enemyPrefabs") as IList;
            if (prefabs == null) return;
            var type = Number(Fields.Get(comp, "arenaType"));
            if (comp.gameObject.name == "StagArena")
            {
                world.StagArenas.Add(Plan.Key(scene, uuid));
            }
            // The arena spawns one spirit per spawn point, up to its
            // difficulty, cycling through the prefab list. So a prefab can
            // spawn several times, or not at all.
            var points = Fields.Get(comp, "spawnPoints") as IList;
            var spawns = Math.Min(Number(Fields.Get(comp, "baseDifficulty")),
                                  points == null ? 0 : points.Count);
            for (var i = 0; i < prefabs.Count; i++)
            {
                var prefab = prefabs[i] as UnityEngine.Object;
                if (prefab == null) continue;
                var slot = Slot.Of(scene, uuid, prefab.name);
                slot.Index = i;
                slot.Arena = type;
                slot.Amount = i < spawns ? (spawns - 1 - i) / prefabs.Count + 1 : 0;
                world.Spirits.Add(slot);
                Rank(prefab, world);
            }
        }

        /// How the game ranks a spirit (common, elder spirit or boss) and what
        /// it drops. The rank is on the prefab's Character, which is what tells
        /// an Owl from an OwlElite.
        static void Rank(UnityEngine.Object prefab, World world)
        {
            var go = prefab as GameObject;
            Registry.Hold(go);
            if (world.Rarity.ContainsKey(prefab.name)) return;
            if (go == null) return;
            var spirit = Registry.EnemyType == null ? null : go.GetComponent(Registry.EnemyType);
            var drops = spirit == null ? null : Fields.Get(spirit, "lootDrops") as IList;
            if (drops != null)
            {
                var loot = new List<string>();
                foreach (var drop in drops)
                {
                    var data = drop as UnityEngine.Object;
                    var guid = data == null ? null : Registry.GuidOf(data);
                    if (guid != null) loot.Add(guid);
                }
                world.Loot[prefab.name] = loot.ToArray();
            }
            var character = Registry.CharacterType == null ? null
                : go.GetComponent(Registry.CharacterType);
            if (character == null) return;
            world.Rarity[prefab.name] = Number(Fields.Get(character, "rarityType"));
        }

        /// An enum field, as the int the game stores it as.
        static int Number(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch { return 0; }
        }
    }
}
