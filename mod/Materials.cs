using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace RandomHowl
{
    /// On the materials screen put a colored rim on materials that can still
    /// be found in current zone.
    public static class Materials
    {
        static ManualLogSource log;
        static World world;
        static Plan plan;

        static readonly Color HereColor = new Color(0.3125f, 0.5f, 0.625f);

        // Ingredients found in current region, worked out when the screen opens.
        static readonly HashSet<string> here = new HashSet<string>();

        // Every ingredient id, and the ingredient slots that sit in nests.
        static readonly HashSet<string> ingredients = new HashSet<string>();
        static readonly HashSet<string> nestItems = new HashSet<string>();

        // Scene to the zone it belongs to, from the game's region list.
        static Dictionary<string, UnityEngine.Object> zones;

        public static void Install(Harmony harmony, ManualLogSource logger, World scanned,
                                   Plan shuffled)
        {
            log = logger;
            world = scanned;
            plan = shuffled;
            ingredients.Clear();
            nestItems.Clear();
            foreach (var slot in world.Ingredients) ingredients.Add(slot.Value);
            foreach (var nest in world.Nests)
                for (var i = 0; i < nest.Items.Length; i++)
                    nestItems.Add(Plan.Key(nest.Scene, Keys.TreasureKey(nest.Key, i)));
            Patch(harmony, "IngredientsManager", "OnEnable", nameof(ScreenOpening), false);
            Patch(harmony, "IngredientSlot", "SetRim", nameof(RimSet), true);
            Patch(harmony, "IngredientsManager", "OnEndHoverOverSlot", nameof(HoverEnded), true);
        }

        static void Patch(Harmony harmony, string className, string methodName, string ours,
                          bool after)
        {
            try
            {
                var type = AccessTools.TypeByName(className);
                var original = type == null ? null : AccessTools.Method(type, methodName);
                if (original == null)
                {
                    log.LogWarning("no " + className + "." + methodName
                                   + " — the materials screen won't mark this zone's materials");
                    return;
                }
                var mine = new HarmonyMethod(typeof(Materials).GetMethod(
                    ours, BindingFlags.Public | BindingFlags.Static));
                harmony.Patch(original, after ? null : mine, after ? mine : null);
            }
            catch (Exception e)
            {
                log.LogError("could not patch " + className + "." + methodName + ": " + e.Message);
            }
        }

        // --- the patches ---------------------------------------------------

        /// Runs before the screen sets up its slots, so the rims below see
        /// this zone's list.
        public static void ScreenOpening()
        {
            here.Clear();
            if (!Plugin.Randomized) return;
            Guard("materials in zone", Collect);
        }

        /// The game turns every rim off when the screen opens or flips a page.
        /// Turn ours back on.
        public static void RimSet(object __instance, bool value)
        {
            if (!value) Ring(__instance);
        }

        /// Hover turns the rim off when the pointer leaves. Put ours back.
        public static void HoverEnded(object ingredientSlot)
        {
            Ring(ingredientSlot);
        }

        // --- helpers -------------------------------------------------------

        static void Ring(object slot)
        {
            if (here.Count == 0 || slot == null) return;
            Guard("material rim", () =>
            {
                // Crafting screens use the same slot. Only mark the materials grid.
                if (!(Fields.Get<bool?>(slot, "inCollectionGrid") ?? false)) return;
                var data = Fields.Get<UnityEngine.Object>(slot, "data");
                var rim = Fields.Get<Image>(slot, "rim");
                var guid = data == null ? null : Registry.GuidOf(data);
                if (rim == null || guid == null || !here.Contains(guid)) return;
                rim.enabled = true;
                rim.color = HereColor;
            });
        }

        static void Collect()
        {
            var scenes = ZoneScenes();
            if (scenes.Count == 0) return;

            var data = Patches.Manager("LiveGameDataManager");
            var items = Patches.Manager("GlobalWorldItemManager");
            var taken = items == null ? null
                : Fields.Get<ICollection<string>>(items, "removedSceneItems");
            var beaten = data == null ? null
                : Fields.Get<ICollection<string>>(data, "defeatedArenas");
            var closed = data == null ? null
                : Fields.Get<ICollection<string>>(data, "permanentlyDisabledArenas");

            foreach (var slot in world.Ingredients)
            {
                if (!scenes.Contains(slot.Scene) || nestItems.Contains(Plan.Key(slot.Scene, slot.Key))) continue;
                if (taken != null && taken.Contains(slot.Key)) continue;
                string item;
                if (!plan.Ingredients.TryGetValue(Plan.Key(slot.Scene, slot.Key), out item))
                    item = slot.Value;
                here.Add(item);
            }

            // Nests can move, so go by what each nest holds now.
            foreach (var entry in plan.Nests)
            {
                var parts = entry.Key.Split('\u0001');
                if (parts.Length != 2 || !scenes.Contains(parts[0])) continue;
                if (taken != null && taken.Contains(parts[1])) continue;
                foreach (var item in entry.Value.Items)
                    if (item != null && ingredients.Contains(item)) here.Add(item);
            }

            // Under scarce howls a fight only drops on its first win, and the
            // plan already picked what.
            if (Plugin.Rule(Plugin.Instance.Scarce))
            {
                foreach (var entry in plan.Drops)
                {
                    var parts = entry.Key.Split('\u0001');
                    if (parts.Length != 2 || !scenes.Contains(parts[0])) continue;
                    if (beaten != null && beaten.Contains(parts[1])) continue;
                    if (closed != null && closed.Contains(parts[1])) continue;
                    foreach (var drop in entry.Value) here.Add(drop.Item);
                }
                return;
            }

            // Otherwise fights come back after a rest, and each spirit drops
            // one of its drops each time.
            foreach (var slot in world.Spirits)
            {
                if (slot.Amount == 0 || !scenes.Contains(slot.Scene)) continue;
                if (closed != null && closed.Contains(slot.Key)) continue;
                var name = slot.Value;
                string[] arena;
                if (plan.Spirits.TryGetValue(Plan.Key(slot.Scene, slot.Key), out arena)
                    && slot.Index < arena.Length && arena[slot.Index] != null)
                    name = arena[slot.Index];
                string[] loot;
                if (!world.Loot.TryGetValue(name, out loot)) continue;
                foreach (var item in loot) here.Add(item);
            }
        }

        /// The scenes that make up the zone Ro is in: her own scene, and every
        /// scene the game lists under the same region, like its caves.
        static HashSet<string> ZoneScenes()
        {
            var scenes = new HashSet<string>();
            var loader = Patches.Manager("WorldLoadingSceneManager");
            var root = loader == null ? null : Fields.Get<Component>(loader, "currentRegion");
            if (root == null) return scenes;
            scenes.Add(root.gameObject.scene.name);
            var zone = Fields.Get<UnityEngine.Object>(root, "data");
            if (zone == null) return scenes;
            foreach (var entry in Zones())
                if (entry.Value == zone) scenes.Add(entry.Key);
            return scenes;
        }

        static Dictionary<string, UnityEngine.Object> Zones()
        {
            if (zones != null) return zones;
            var type = AccessTools.TypeByName("RegionDataManager");
            var instance = type == null ? null : AccessTools.Property(type, "Instance");
            var manager = instance == null ? null : instance.GetValue(null, null);
            var regions = manager == null ? null : Fields.Get<IList>(manager, "regions");
            if (regions == null)
            {
                log.LogWarning("no RegionDataManager.regions — only Ro's own scene "
                               + "counts as her zone");
                return new Dictionary<string, UnityEngine.Object>();
            }

            var found = new Dictionary<string, UnityEngine.Object>();
            var all = new List<object>();
            foreach (var region in regions) all.Add(region);
            all.Add(Fields.Get(manager, "cliffs"));
            foreach (var region in all)
            {
                var zone = region as UnityEngine.Object;
                if (zone == null) continue;
                foreach (var list in new[] { "arenas", "waypoints", "treasures", "caves" })
                {
                    var entries = Fields.Get<IList>(zone, list);
                    if (entries == null) continue;
                    foreach (var entry in entries)
                    {
                        var scene = entry == null ? null : Fields.Get<string>(entry, "scene");
                        if (!string.IsNullOrEmpty(scene) && !found.ContainsKey(scene))
                            found[scene] = zone;
                    }
                }
            }
            return zones = found;
        }

        static void Guard(string what, Action body)
        {
            try { body(); }
            catch (Exception e) { log.LogError(what + " patch failed: " + e); }
        }
    }
}
