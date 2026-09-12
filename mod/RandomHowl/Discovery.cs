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
    /// Full world scan, run at startup before any shuffling.
    ///
    /// Loads every level scene additively, pulls out every shufflable slot
    /// and its vanilla value, then unloads. Yields a frame between levels so
    /// the splash screen stays responsive.
    ///
    /// This replaces the python tool that used to dump world.tsv — the mod now
    /// maps the world itself, so a fresh install with no TSV works out of the box.
    public static class Discovery
    {
        static ManualLogSource log;

        public static IEnumerator Scan(ManualLogSource logger, Action<World> done)
        {
            log = logger;
            var world = new World();
            var clock = Stopwatch.StartNew();

            ResolveKinds();

            var count = SceneManager.sceneCountInBuildSettings;
            log.LogInfo("discovery: scanning " + count + " build scenes");
            for (var i = 0; i < count; i++)
            {
                if (!SkipScenes.Contains(SceneNameAt(i)))
                    yield return LoadAndScan(i, world);
                yield return null;   // let the splash screen breathe
            }

            log.LogInfo(string.Format(
                        "discovery: {0} pickups, {1} totems, {2} cave mouths, {3} arena slots "
                        + "over {4} enemies in {5:0.0}s",
                        world.Ingredients.Count, world.Totems.Count, world.Entrances.Count,
                        world.Enemies.Count, world.Rarity.Count, clock.ElapsedMilliseconds / 1000f));

            done(world);
        }

        // --- values -------------------------------------------------------

        /// Cards and the spoiler log's names come off the game's own data
        /// manager, which only holds them once it has started — later than the
        /// scan. Plugin calls this when that happens.
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

        /// The skill tree hands over a card or a totem the way an event does,
        /// so its nodes join those pools. Which node is which is spelled out
        /// by Keys.Node, since four nodes are simply called "Card".
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

        /// The manager's own list, or whatever Unity happens to have loaded if
        /// it hasn't got one.
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

        /// Each card carries the realm it belongs to on the asset itself.
        /// Only the realms are shuffled — the basic, curse and key-item piles
        /// are left where they are.
        static void CollectCards(World world, List<UnityEngine.Object> cards,
                List<UnityEngine.Object> types)
        {
            world.Cards.Clear();
            world.Recipes.Clear();
            world.PlusOnly.Clear();
            world.Realmless.Clear();
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
                var realm = Registry.GuidOf(type);
                if (realm == null) continue;
                if (realmless.Contains(realm)) world.Realmless.Add(guid);
                if (!realms.Contains(realm)) continue;
                world.Cards.Add(new Slot { Key = guid, Value = realm });
                CollectRecipe(world, card, guid);
            }

            log.LogInfo("discovery: " + realms.Count + " realms over "
                    + world.Cards.Count + " cards, " + world.Recipes.Count
                    + " recipe slots");
        }

        /// What a card is crafted from — one slot per ingredient the recipe
        /// asks for. Only the cards you can craft have one, which is why this
        /// sits inside the realm walk: an enemy's cards carry a recipe too and
        /// nobody ever crafts them.
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
                world.Recipes.Add(slot);
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

        /// The pile outside every realm that the player still draws from —
        /// index 0 and basic, which is one type and not the OVERWHELMED or
        /// special-tile piles that share the flag.
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

        // Boot and global scenes. Their MonoBehaviours run on load and some use
        // DontDestroyOnLoad, so loading one to scan it would leak duplicate
        // managers into the running game. They hold no shufflable slots, so
        // skip them.
        static readonly HashSet<string> SkipScenes = new HashSet<string>
        {
            "CompanyLogos", "TitleMenu", "Credits", "Main (Managers)",
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

        /// Additively load one build scene, find every slot in it, then unload.
        /// Loaded by build index, not name — the scene names in the build
        /// settings (TitleMenu, Main (Managers), ...) aren't the levelN files
        /// on disk, and only LoadScene by index works without guessing.
        /// Runs as a coroutine so the load doesn't block the splash screen.
        static IEnumerator LoadAndScan(int index, World world)
        {
            var clock = Stopwatch.StartNew();
            var op = SceneManager.LoadSceneAsync(index, LoadSceneMode.Additive);
            if (op == null) yield break;
            while (!op.isDone) yield return null;
            var loaded = clock.ElapsedMilliseconds;

            // By build index, never "the last scene in the list". The game
            // keeps loading scenes of its own while we work, and unloading one
            // of those would take the title screen down with it.
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

            var unload = SceneManager.UnloadSceneAsync(scene);
            if (unload != null) while (!unload.isDone) yield return null;
        }

        static int Count(World w)
        {
            return w.Ingredients.Count + w.Totems.Count + w.Entrances.Count
                   + w.Enemies.Count + w.Grants.Count;
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

        /// Ask Unity for the four component types directly. A hand-written walk
        /// over every GameObject would do the same job, but this hands the
        /// search to the engine and skips touching the other ~99% of the scene.
        static void ScanScene(Scene scene, World world)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var kind in kinds)
                    foreach (var comp in root.GetComponentsInChildren(kind.Type, true))
                        if (comp != null) kind.Scan(comp, scene.name, world);
        }

        // Each scanner mirrors genmod.py: a UUID key for scene items, a
        // hierarchy path for events. Both are stable across runs.
        static void ScanIngredient(Component comp, string scene, World world)
        {
            var data = Fields.Get(comp, "data") as UnityEngine.Object;
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

        /// Which card an event hands over. Most of them give a quest card the
        /// game's own logic looks for by name, so only the realm-typed ones
        /// move — see Plan.BuildGrants.
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
            });
        }

        static void ScanArena(Component comp, string scene, World world)
        {
            var uuid = Registry.Uuid(comp.gameObject);
            if (uuid == null) return;
            var prefabs = Fields.Get(comp, "enemyPrefabs") as IList;
            if (prefabs == null) return;
            var type = Number(Fields.Get(comp, "arenaType"));
            for (var i = 0; i < prefabs.Count; i++)
            {
                var prefab = prefabs[i] as UnityEngine.Object;
                if (prefab == null) continue;
                var slot = Slot.Of(scene, uuid, prefab.name);
                slot.Index = i;
                slot.Arena = type;
                world.Enemies.Add(slot);
                Rank(prefab, world);
            }
        }

        /// How the game ranks an enemy — common, elite or boss. It sits on the
        /// prefab's own Character, which is what tells an Owl from an OwlElite.
        static void Rank(UnityEngine.Object prefab, World world)
        {
            if (world.Rarity.ContainsKey(prefab.name) || Registry.CharacterType == null) return;
            var go = prefab as GameObject;
            var character = go == null ? null : go.GetComponent(Registry.CharacterType);
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
