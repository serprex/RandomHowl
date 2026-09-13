using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace RandomHowl
{
    /// Finds the game's objects at runtime.
    ///
    /// Values are named in a way that survives restarts: a GUID for cards,
    /// items and totems, an asset name for areas and spirit prefabs. This turns
    /// them back into live objects. Nothing is loaded; it searches what Unity
    /// already has in memory, which covers everything the persistent manager
    /// scene brings in.
    public static class Registry
    {
        public static readonly Type ItemType = AccessTools.TypeByName("ItemData");
        public static readonly Type CardType = AccessTools.TypeByName("CardData");
        public static readonly Type CardTypeType = AccessTools.TypeByName("CardTypeData");
        public static readonly Type AreaType = AccessTools.TypeByName("AreaData");
        public static readonly Type EnemyType = AccessTools.TypeByName("Enemy");
        public static readonly Type CharacterType = AccessTools.TypeByName("Character");
        public static readonly Type PlayerType = AccessTools.TypeByName("Player");
        public static readonly Type UuidType = AccessTools.TypeByName("UUID");
        public static readonly Type NodeType = AccessTools.TypeByName("ProgressionData");

        static readonly Lookup items = new Lookup(() => ItemType, GuidOf);
        static readonly Lookup cards = new Lookup(() => CardType, GuidOf);
        static readonly Lookup cardTypes = new Lookup(() => CardTypeType, GuidOf);
        static readonly Lookup areas = new Lookup(() => AreaType, o => o.name, true);
        static readonly Lookup spirits = new Lookup(() => EnemyType, o => Root(o).name);
        static readonly Lookup nodes = new Lookup(() => NodeType, GuidOf, true);

        public static UnityEngine.Object Item(string guid) { return items.Find(guid); }
        public static UnityEngine.Object Card(string guid) { return cards.Find(guid); }
        public static UnityEngine.Object Realm(string guid) { return cardTypes.Find(guid); }
        public static UnityEngine.Object Area(string name) { return areas.Find(name); }

        public static GameObject Spirit(string name)
        {
            var found = spirits.Find(name);
            return found == null ? null : Root(found);
        }

        public static IEnumerable<UnityEngine.Object> AllCards() { return cards.All(); }
        public static IEnumerable<UnityEngine.Object> AllItems() { return items.All(); }
        public static IEnumerable<UnityEngine.Object> AllRealms() { return cardTypes.All(); }

        /// The skill tree's nodes. They are assets, so like the areas they are
        /// loaded on demand rather than waited for.
        public static IEnumerable<UnityEngine.Object> AllNodes() { return nodes.All(); }

        /// The uniqueIdentifier the save files and this mod both go by.
        public static string GuidOf(UnityEngine.Object obj)
        {
            var field = AccessTools.Field(obj.GetType(), "uniqueIdentifier");
            return field == null ? null : field.GetValue(obj) as string;
        }

        /// The id of the UUID component the game puts on items and arenas.
        public static string Uuid(GameObject go)
        {
            if (UuidType == null) return null;
            var component = go.GetComponent(UuidType);
            if (component == null) return null;
            var property = AccessTools.Property(component.GetType(), "ID");
            if (property == null) return null;
            return property.GetValue(component, null) as string;
        }

        static GameObject Root(UnityEngine.Object obj)
        {
            var component = obj as Component;
            return component == null ? obj as GameObject : component.gameObject;
        }

        public static void Forget()
        {
            items.Forget();
            cards.Forget();
            cardTypes.Forget();
            areas.Forget();
            spirits.Forget();
            nodes.Forget();
        }

        /// A name-to-object index over loaded objects, rebuilt on a miss, since
        /// a newly loaded scene may have the answer.
        class Lookup
        {
            readonly Func<Type> type;
            readonly Func<UnityEngine.Object, string> key;
            readonly bool alsoResources;
            Dictionary<string, UnityEngine.Object> index;
            int builtOnFrame = -1;

            public Lookup(Func<Type> type, Func<UnityEngine.Object, string> key,
                          bool alsoResources = false)
            {
                this.type = type;
                this.key = key;
                this.alsoResources = alsoResources;
            }

            public void Forget() { index = null; }

            public IEnumerable<UnityEngine.Object> All()
            {
                // An empty index counts as a miss. One built during boot,
                // before managers loaded anything, would otherwise stay empty
                // all run.
                if (index == null || (index.Count == 0 && builtOnFrame != Time.frameCount))
                    Build();
                return index.Values;
            }

            public UnityEngine.Object Find(string wanted)
            {
                if (wanted == null) return null;
                if (index == null) Build();
                UnityEngine.Object found;
                if (index.TryGetValue(wanted, out found) && found != null) return found;
                if (builtOnFrame == Time.frameCount) return null;
                Build();
                return index.TryGetValue(wanted, out found) ? found : null;
            }

            void Build()
            {
                index = new Dictionary<string, UnityEngine.Object>();
                builtOnFrame = Time.frameCount;
                var what = type();
                if (what == null) return;
                Add(Resources.FindObjectsOfTypeAll(what));
                if (alsoResources) Add(Resources.LoadAll("", what));
            }

            void Add(UnityEngine.Object[] found)
            {
                foreach (var obj in found)
                {
                    if (obj == null) continue;
                    string name;
                    try { name = key(obj); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!index.ContainsKey(name) || Prefer(obj, index[name])) index[name] = obj;
                }
            }

            /// Prefer the asset over a copy of it sitting in a loaded scene.
            static bool Prefer(UnityEngine.Object candidate, UnityEngine.Object current)
            {
                var root = Root(candidate);
                var held = Root(current);
                if (root == null || held == null) return false;
                return !root.scene.IsValid() && held.scene.IsValid();
            }
        }
    }

    /// How a slot is named. Discovery writes these keys and the patches look
    /// them up again, so both sides have to spell them the same way.
    public static class Keys
    {
        /// Scene plus the game's own UUID — pickups and arenas carry one.
        public static string Uuid(Component component)
        {
            return Plan.Key(component.gameObject.scene.name,
                            Registry.Uuid(component.gameObject));
        }

        /// Scene plus hierarchy path, for the event components with no UUID.
        public static string PathOf(Component component)
        {
            return Plan.Key(component.gameObject.scene.name, Path(component.transform));
        }

        public static string Path(Transform transform)
        {
            var path = new StringBuilder(transform.name);
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                path.Insert(0, parent.name + "/");
            return path.ToString();
        }

        /// Skill tree nodes aren't in a scene and their asset names repeat, so
        /// a node is named by its realm plus what it gives. Both can be read
        /// off the node when it's unlocked.
        public const string NodeScene = "skill tree";

        public static string NodeKey(UnityEngine.Object node)
        {
            var realm = Fields.Get(node, "cardType") as UnityEngine.Object;
            var reward = Fields.Get(node, "rewardType");
            return "node:" + (realm == null ? "-" : Registry.GuidOf(realm))
                   + ":" + (reward == null ? "-" : reward.ToString());
        }

        public static string Node(UnityEngine.Object node)
        {
            return Plan.Key(NodeScene, NodeKey(node));
        }
    }

    /// Cached field access, since every patch pokes the same few fields.
    public static class Fields
    {
        static readonly Dictionary<string, FieldInfo> cache = new Dictionary<string, FieldInfo>();

        public static FieldInfo Of(object instance, string name)
        {
            var type = instance.GetType();
            var key = type.FullName + "." + name;
            FieldInfo field;
            if (!cache.TryGetValue(key, out field))
                cache[key] = field = AccessTools.Field(type, name);
            return field;
        }

        public static object Get(object instance, string name)
        {
            var field = Of(instance, name);
            return field == null ? null : field.GetValue(instance);
        }

        /// A read-only property, for the few values the game computes instead
        /// of storing.
        public static object Property(object instance, string name)
        {
            var getter = AccessTools.PropertyGetter(instance.GetType(), name);
            return getter == null ? null : getter.Invoke(instance, null);
        }

        public static bool Set(object instance, string name, object value)
        {
            var field = Of(instance, name);
            if (field == null) return false;
            field.SetValue(instance, value);
            return true;
        }
    }
}
