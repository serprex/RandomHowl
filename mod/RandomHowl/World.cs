using System.Collections.Generic;
using System.IO;

namespace RandomHowl
{
    /// One shufflable position, named the way the mod can find it at runtime.
    public class Slot
    {
        public string Scene;
        public string Key;      // a UUID for pickups and arenas, a path for events
        public int Index;       // which enemy of an arena, which card of a
                                // skill node; 0 for everything else
        public string Value;    // what vanilla puts here
        public string Spawn;    // entrances only: the spawn point in the target area
        public int Arena;       // enemies only: the arena's own type, 1 being elite

        public static Slot Of(string scene, string key, string value)
        {
            return new Slot { Scene = scene, Key = key, Value = value };
        }
    }

    /// Every shufflable slot in the game, and what vanilla puts in it.
    /// Discovery fills this in at startup by walking the levels.
    public class World
    {
        public readonly List<Slot> Ingredients = new List<Slot>();
        public readonly List<Slot> Totems = new List<Slot>();
        public readonly List<Slot> Entrances = new List<Slot>();
        public readonly List<Slot> Enemies = new List<Slot>();
        public readonly List<Slot> Cards = new List<Slot>();
        public readonly List<Slot> Recipes = new List<Slot>();
        public readonly List<Slot> Grants = new List<Slot>();
        public readonly List<Slot> Nodes = new List<Slot>();

        /// The cards outside every realm but still in the player's pool — the
        /// elder spirit gifts and the Fylge cards live here.
        public readonly HashSet<string> Realmless = new HashSet<string>();

        /// Cards only the alternate card set uses. Kept out of card pool.
        public readonly HashSet<string> PlusOnly = new HashSet<string>();

        /// Enemy prefab name to how the game ranks it: 0 common, 1 elite,
        /// 2 boss. Filled in as the arenas are scanned.
        public readonly Dictionary<string, int> Rarity = new Dictionary<string, int>();

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
