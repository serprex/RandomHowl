using System.Collections.Generic;
using System.IO;

namespace RandomHowl
{
    /// One shufflable position, named the way the mod can find it at runtime.
    public class Slot
    {
        public string Scene;
        public string Key;      // a UUID for pickups and arenas, a path for events
        public int Index;       // which spirit of an arena, which card of a
                                // skill node; 0 for everything else
        public string Value;    // what vanilla puts here
        public string Spawn;    // entrances only: the spawn point in the target area
        public float X, Y;      // entrances only: where the cave mouth stands
        public int Arena;       // spirits: arena type, 1 means elder spirit fight
        public int Amount;      // spirits: times it spawns in its fight;
                                // recipes: how many of the ingredient it takes

        public static Slot Of(string scene, string key, string value)
        {
            return new Slot { Scene = scene, Key = key, Value = value };
        }
    }

    /// One line of a recipe: which ingredient, and how many of it.
    public struct Ingredient
    {
        public string Item;
        public int Amount;
    }

    /// A nest: its blood tears, and the items it holds in order.
    public class Nest
    {
        public string Scene;
        public string Key;          // the nest's UUID
        public int Tears;
        public string[] Items;      // item ids; null where an item has no id
    }

    /// Where a spawn point stands: its scene, and its spot in that scene.
    public struct Place
    {
        public string Scene;
        public float X, Y;
    }

    /// Every shufflable slot in the game, and what vanilla puts in it.
    /// Discovery fills this in at startup by walking the levels.
    public class World
    {
        public readonly List<Slot> Ingredients = new List<Slot>();
        public readonly List<Slot> Totems = new List<Slot>();
        public readonly List<Slot> Entrances = new List<Slot>();
        public readonly List<Slot> Spirits = new List<Slot>();
        public readonly List<Slot> Cards = new List<Slot>();
        public readonly List<Slot> Recipes = new List<Slot>();
        public readonly List<Slot> Grants = new List<Slot>();
        public readonly List<Slot> Nodes = new List<Slot>();

        /// Spawn point ID to where it stands, read off the game's own table.
        /// Pairing caves needs to know which scene a cave mouth leads into.
        public readonly Dictionary<string, Place> Spawns = new Dictionary<string, Place>();

        /// Cards outside every realm that are still in the player's pool, like
        /// elder spirit gifts and Fylge cards. Card to its type.
        public readonly Dictionary<string, string> Realmless = new Dictionary<string, string>();

        /// What each of those cards is crafted from. Mostly nothing.
        public readonly Dictionary<string, Ingredient[]> RealmlessRecipes =
            new Dictionary<string, Ingredient[]>();

        /// Cards the game only hands over and never lets you craft: skill tree
        /// cards, elder spirit gifts, the aurora's card.
        public readonly HashSet<string> Rewards = new HashSet<string>();

        /// Cards only the alternate card set uses. Kept out of card pool
        public readonly HashSet<string> PlusOnly = new HashSet<string>();

        ///Surprise fights named StagArena. Scene plus arena UUID, as Plan.Key
        public readonly HashSet<string> StagArenas = new HashSet<string>();

        /// Every nest that holds something. Its items are also in Ingredients
        /// or Totems, keyed by Keys.TreasureKey.
        public readonly List<Nest> Nests = new List<Nest>();

        /// Spirit prefab name to how the game ranks it: 0 common, 1 elder
        /// spirit, 2 boss. Filled in as the arenas are scanned.
        public readonly Dictionary<string, int> Rarity = new Dictionary<string, int>();

        /// Spirit prefab name to what it can drop. Each spawn drops one of
        /// these at random.
        public readonly Dictionary<string, string[]> Loot = new Dictionary<string, string[]>();

        /// Card to how many copies the game allows by default: 2 for common, 1
        /// for rare. Reward cards get a number too, in case one becomes
        /// craftable.
        public readonly Dictionary<string, int> Copies = new Dictionary<string, int>();

        public readonly Dictionary<string, string> names = new Dictionary<string, string>();

        /// Human name for a value, for the spoiler log. Falls back to the id.
        public string Name(string kind, string id)
        {
            string label;
            return names.TryGetValue(kind + "\t" + id, out label) ? label : id;
        }

        static Slot Read(string[] c, string value)
        {
            return new Slot { Scene = c[1], Key = c[3], Value = value };
        }
    }
}
