using System;
using System.Collections.Generic;

namespace RandomHowl
{
    /// Where one cave mouth now leads.
    public struct Entrance
    {
        public string Area;
        public string Spawn;
    }

    /// How far the card gift shuffle is allowed to go.
    public enum GrantShuffle
    {
        Off,
        On,
        /// The elder spirit gifts and the Fylge cards join the pool, and the
        /// events that handed them out become rewards too.
        Everything,
    }

    /// How far the enemy shuffle is allowed to go.
    public enum EnemyShuffle
    {
        Off,
        On,
        /// Elites only turn up in elite combats. Bosses never move in any mode.
        Restricted,
    }

    public class Rng
    {
        ulong state;

        public Rng(string seed)
        {
            ulong hash = 14695981039346656037UL;        // FNV-1a
            foreach (var ch in seed)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            state = hash;
        }

        public ulong Next()                              // SplitMix64
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int Below(int bound)
        {
            return (int)(Next() % (ulong)bound);
        }
    }

    /// The shuffle, done once at startup and then looked up as scenes load.
    public class Plan
    {
        public readonly Dictionary<string, string> Ingredients = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Totems = new Dictionary<string, string>();
        public readonly Dictionary<string, Entrance> Entrances = new Dictionary<string, Entrance>();
        public readonly Dictionary<string, string[]> Enemies = new Dictionary<string, string[]>();
        public readonly Dictionary<string, string> Cards = new Dictionary<string, string>();
        public readonly Dictionary<string, string[]> Recipes = new Dictionary<string, string[]>();
        public readonly Dictionary<string, string> Grants = new Dictionary<string, string>();
        public readonly List<string> Spoiler = new List<string>();

        public static string Key(string scene, string key)
        {
            return scene + "\u0001" + key;
        }

        /// Every shuffle is a permutation: the same values, in other places.
        /// Each category gets its own seed, so turning one off leaves the rest
        /// exactly where they were.
        public static Plan Build(World world, string seed, Func<string, bool> enabled,
                                 EnemyShuffle enemies, int elitePercent, GrantShuffle grants)
        {
            var plan = new Plan();

            if (enabled("ingredients"))
                foreach (var moved in Permute(world.Ingredients, seed, "ingredients"))
                {
                    plan.Ingredients[Key(moved.Slot.Scene, moved.Slot.Key)] = moved.Value;
                    plan.Note("ingredient", world, "item", moved);
                }

            if (enabled("totems"))
                foreach (var moved in Permute(TotemSlots(world), seed, "totems"))
                {
                    plan.Totems[Key(moved.Slot.Scene, moved.Slot.Key)] = moved.Value;
                    plan.Note("totem", world, "item", moved);
                }

            if (enabled("entrances"))
                foreach (var moved in Permute(world.Entrances, seed, "entrances"))
                {
                    plan.Entrances[Key(moved.Slot.Scene, moved.Slot.Key)] =
                        new Entrance { Area = moved.Value, Spawn = moved.Spawn };
                    plan.Note("entrance", world, "area", moved);
                }

            plan.BuildEnemies(world, seed, enemies, elitePercent);

            // Cards are the odd one out: a realm lives on the shared card
            // asset, which stays changed for the rest of the session. So when
            // the toggle is off we still write — the vanilla realms, back.
            foreach (var moved in Permute(world.Cards, seed, "card_realms",
                                          enabled("card_realms")))
            {
                plan.Cards[moved.Slot.Key] = moved.Value;
                plan.Note("card", world, "cardtype", moved);
            }

            // Recipes sit on the same assets, so they are written on the same
            // terms — off means vanilla written back, not nothing written.
            plan.BuildRecipes(world, seed, enabled("recipes"));
            plan.BuildGrants(world, seed, grants);

            return plan;
        }

        // A skill node's two rewards, told apart by the index it was scanned
        // under.
        const int CardNode = 0, TotemNode = 1;

        /// A node of the skill tree hands a totem over like any other reward,
        /// so it takes its turn in the same permutation as the pickups.
        static List<Slot> TotemSlots(World world)
        {
            var slots = new List<Slot>(world.Totems);
            foreach (var node in world.Nodes)
                if (node.Index == TotemNode) slots.Add(node);
            return Ordered(slots);
        }

        /// Which card each reward hands over: the aurora, the blood tears that
        /// grant a card, the skill nodes that grant one. Vanilla gives the same
        /// card every run; here each reward takes a different one out of the
        /// whole realm card list, and no card is handed over twice. The pool is
        /// much bigger than the rewards, so most realm cards are never one.
        ///
        /// Only rewards that give a realm card move. The others give a quest
        /// card the game looks for by name further along the event, and those
        /// stay where they are. Everything takes in the elder spirit gifts and
        /// the Fylge cards as well, pool and events both; the alternate card
        /// set's own versions stay out of it, since in an ordinary run one
        /// reads as a broken card rather than a surprise.
        void BuildGrants(World world, string seed, GrantShuffle mode)
        {
            if (mode == GrantShuffle.Off) return;

            var realms = new HashSet<string>();
            var pool = new List<string>();
            foreach (var slot in world.Cards)
            {
                realms.Add(slot.Key);
                if (!world.PlusOnly.Contains(slot.Key) && !pool.Contains(slot.Key))
                    pool.Add(slot.Key);
            }

            var slots = new List<Slot>();
            foreach (var slot in world.Grants)
            {
                if (!realms.Contains(slot.Value))
                {
                    if (mode != GrantShuffle.Everything
                        || !world.Realmless.Contains(slot.Value)
                        || world.PlusOnly.Contains(slot.Value)) continue;
                    // The gift joins the pool too, so it can turn up anywhere
                    // a gift goes, and its own event moves off it.
                    if (!pool.Contains(slot.Value)) pool.Add(slot.Value);
                }
                slots.Add(slot);
            }
            // The nodes are scanned off the loaded assets, whose order Unity
            // picks; the key settles it the same way every run.
            slots = Ordered(slots);
            pool.Sort(string.CompareOrdinal);

            if (slots.Count == 0 || slots.Count > pool.Count) return;
            var rng = new Rng(seed + ":card_grants");
            var order = new int[pool.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Shuffle(order, null, rng);
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var value = pool[order[i]];
                Grants[Key(slot.Scene, slot.Key)] = value;
                Note("gift", world, "card", slot, value);
            }
        }

        /// Slots in a fixed order, so a run's layout doesn't hinge on the order
        /// the game happened to hand its assets over in.
        static List<Slot> Ordered(List<Slot> slots)
        {
            slots.Sort((a, b) => string.CompareOrdinal(Key(a.Scene, a.Key),
                                                       Key(b.Scene, b.Key)));
            return slots;
        }

        /// Moves the ingredients a card is crafted from about, one at a time.
        /// The number beside each one stays where it is, so a card costs as
        /// many of as many things as it always did, just of other things.
        void BuildRecipes(World world, string seed, bool shuffle)
        {
            var slots = world.Recipes;
            var moved = Permute(slots, seed, "recipes", shuffle);
            var values = new string[slots.Count];
            for (var i = 0; i < slots.Count; i++) values[i] = moved[i].Value;
            if (shuffle) Unrepeat(slots, values);

            var sizes = Sizes(slots, slot => slot.Key);
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                string[] recipe;
                if (!Recipes.TryGetValue(slot.Key, out recipe))
                    Recipes[slot.Key] = recipe = new string[sizes[slot.Key]];
                if (slot.Index < recipe.Length) recipe[slot.Index] = values[i];
                Note("recipe", world.Name("card", slot.Key) + " #" + slot.Index,
                     world, "item", slot.Value, values[i]);
            }
        }

        /// No vanilla recipe asks for the same ingredient twice, and a shuffled
        /// one shouldn't either — two identical slots on the crafting screen
        /// read as a bug. So a repeat trades places with a later slot that can
        /// take it. The last few slots may have nobody left to trade with,
        /// which leaves the repeat standing.
        static void Unrepeat(List<Slot> slots, string[] values)
        {
            var wanted = new Dictionary<string, HashSet<string>>();   // card -> what it asks for
            foreach (var slot in slots)
                if (!wanted.ContainsKey(slot.Key)) wanted[slot.Key] = new HashSet<string>();

            for (var i = 0; i < slots.Count; i++)
            {
                var mine = wanted[slots[i].Key];
                if (mine.Add(values[i])) continue;
                // Forward only: the slots behind us are settled, and the ones
                // ahead get their own turn at whatever we hand them.
                for (var j = i + 1; j < slots.Count; j++)
                {
                    var theirs = wanted[slots[j].Key];
                    if (theirs == mine || mine.Contains(values[j])
                        || theirs.Contains(values[i])) continue;
                    var swap = values[j];
                    values[j] = values[i];
                    values[i] = swap;
                    mine.Add(swap);
                    break;
                }
            }
        }

        // How the game ranks an enemy, and what an elite arena's type is.
        const int Elite = 1, Boss = 2, EliteArena = 1;

        /// Enemies move as species, not as prefabs: an Owl and an OwlElite are
        /// the same spirit in two forms. So the shuffle moves the spirit about
        /// and the elite dial decides, afterwards, which of them are elite —
        /// with both forms of every spirit left somewhere in the world, so no
        /// ingredient goes missing. Off still runs, because the dial works on
        /// its own.
        void BuildEnemies(World world, string seed, EnemyShuffle mode, int percent)
        {
            var slots = world.Enemies;
            var elite = new Dictionary<string, string>();    // species -> elite form
            var plain = new Dictionary<string, string>();    // elite form -> species
            Pairs(world.Rarity, elite, plain);

            // Split what vanilla puts in each slot into a spirit and a form.
            var species = new string[slots.Count];
            var wasElite = new bool[slots.Count];
            for (var i = 0; i < slots.Count; i++)
            {
                string common;
                wasElite[i] = plain.TryGetValue(slots[i].Value, out common);
                species[i] = wasElite[i] ? common : slots[i].Value;
            }

            // Move the spirits. The boss-tier ones stay put, so every boss
            // fight is the fight vanilla put there.
            var order = new int[slots.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            if (mode != EnemyShuffle.Off)
            {
                var pinned = new bool[slots.Count];
                for (var i = 0; i < slots.Count; i++)
                    pinned[i] = IsBossTier(slots[i].Value, world.Rarity, plain);
                Shuffle(order, pinned, new Rng(seed + ":enemies"));
            }

            var moved = new string[slots.Count];
            var isElite = new bool[slots.Count];
            for (var i = 0; i < slots.Count; i++)
            {
                moved[i] = species[order[i]];
                isElite[i] = wasElite[order[i]];
            }

            // Then the dial. It runs whenever anything moves at all, since the
            // one-of-each-form seed it lays down first is what keeps every
            // ingredient in the world. A wholly vanilla run is the one case
            // that needs nothing: vanilla already has both forms of everything.
            if (percent >= 0 || mode != EnemyShuffle.Off)
            {
                var vanilla = 0;
                foreach (var one in wasElite) if (one) vanilla++;
                Promote(moved, isElite, slots, elite, mode, seed, percent, vanilla);
            }

            var sizes = Sizes(slots, slot => Key(slot.Scene, slot.Key));
            for (var i = 0; i < slots.Count; i++)
            {
                var name = isElite[i] ? elite[moved[i]] : moved[i];
                var key = Key(slots[i].Scene, slots[i].Key);
                string[] arena;
                if (!Enemies.TryGetValue(key, out arena))
                    Enemies[key] = arena = new string[sizes[key]];
                if (slots[i].Index < arena.Length) arena[slots[i].Index] = name;
                Note("enemy", world, "prefab", slots[i], name);
            }
        }

        /// Which enemies come in two forms. The game names the elite one after
        /// the common one — OwlElite, Owl — but only the rank on the prefab
        /// says which is which, and a few "Elite" names are ranked common.
        static void Pairs(Dictionary<string, int> rarity, Dictionary<string, string> elite,
                          Dictionary<string, string> plain)
        {
            var lower = new Dictionary<string, string>();
            foreach (var name in rarity.Keys) lower[name.ToLowerInvariant()] = name;
            foreach (var entry in rarity)
            {
                if (entry.Value != Elite) continue;
                string bare;
                if (!lower.TryGetValue(entry.Key.ToLowerInvariant().Replace("elite", ""),
                                       out bare)) continue;
                if (rarity[bare] != 0) continue;
                elite[bare] = entry.Key;
                plain[entry.Key] = bare;
            }
        }

        /// Bosses, and the elites with no common form — the Great Spirits' arms
        /// and the like, which only make sense in the fight they belong to.
        /// Anything we couldn't rank counts as boss tier and stays put.
        static bool IsBossTier(string value, Dictionary<string, int> rarity,
                               Dictionary<string, string> plain)
        {
            int rank;
            if (!rarity.TryGetValue(value, out rank)) return true;
            return rank == Boss || (rank == Elite && !plain.ContainsKey(value));
        }

        /// Give that share of the spawns that can be elite the elite form. Both
        /// forms drop ingredients of their own, so a seed goes down first: every
        /// species gets one spawn that is elite and one that is not. The dial
        /// only fills in the spawns left over, which makes 0% one elite of each
        /// kind and 100% one plain of each. Below zero the dial is off and
        /// vanilla's own number of elites stands, wherever the seed puts them.
        /// Restricted only counts spawns in an elite combat, so asking for all of
        /// them there is still far short of everything.
        static void Promote(string[] moved, bool[] isElite, List<Slot> slots,
                            Dictionary<string, string> elite, EnemyShuffle mode,
                            string seed, int percent, int vanilla)
        {
            // Where each species stands: which of its spawns can be elite at
            // all, and which stay plain whatever the dial says. Under restricted
            // only a spawn in an elite combat can be elite, so the spawns
            // outside one cover the plain half of a species by themselves.
            var canElite = new Dictionary<string, List<int>>();
            var plainOnly = new Dictionary<string, List<int>>();
            var open = new List<int>();
            for (var i = 0; i < moved.Length; i++)
            {
                isElite[i] = false;
                var name = moved[i];
                if (!elite.ContainsKey(name)) continue;
                if (!canElite.ContainsKey(name))
                {
                    canElite[name] = new List<int>();
                    plainOnly[name] = new List<int>();
                }
                if (mode == EnemyShuffle.Restricted && slots[i].Arena != EliteArena) plainOnly[name].Add(i);
                else { canElite[name].Add(i); open.Add(i); }
            }

            var rng = new Rng(seed + ":elites");
            if (mode == EnemyShuffle.Restricted) Even(canElite, plainOnly, moved, rng);

            // One of each form per species first, both out of the same draw, so
            // which spawn ends up which is the seed's business. A species with
            // a plain-only spawn already has its plain form in the world.
            Shuffle(open, null, rng);
            var hasElite = new HashSet<string>();
            var hasPlain = new HashSet<string>();
            var spare = new List<int>();
            foreach (var i in open)
            {
                var name = moved[i];
                var covered = plainOnly[name].Count > 0;
                if ((covered || canElite[name].Count > 1) && hasElite.Add(name)) isElite[i] = true;
                else if (covered || !hasPlain.Add(name)) spare.Add(i);
            }

            var want = percent >= 0 ? (open.Count * percent + 50) / 100 : vanilla;
            for (var i = 0; i < want - hasElite.Count && i < spare.Count; i++) isElite[spare[i]] = true;
        }

        /// Restricted can put every spawn of a species in an elite combat, or
        /// none of them in one, and then one of its two forms has nowhere to
        /// stand and its ingredients with it. Swap one of its spawns for a spawn
        /// of the kind it lacks, taken from a species with at least two of that
        /// kind, so the donor keeps both forms too. Arena sizes hold, and an
        /// elite form still only ever stands in an elite combat. A species with
        /// one spawn in total can only take one form either way.
        static void Even(Dictionary<string, List<int>> canElite, Dictionary<string, List<int>> plainOnly,
                         string[] moved, Rng rng)
        {
            var names = new List<string>(canElite.Keys);
            names.Sort(string.CompareOrdinal);
            foreach (var name in names)
            {
                var wantsElite = canElite[name].Count == 0;
                var need = wantsElite ? canElite : plainOnly;   // the kind it has none of
                var have = wantsElite ? plainOnly : canElite;   // the kind it gives one up from
                if (need[name].Count > 0 || have[name].Count < 2) continue;

                var donors = names.FindAll(other => other != name && need[other].Count >= 2);
                if (donors.Count == 0) continue;
                var donor = donors[rng.Below(donors.Count)];
                var give = need[donor][rng.Below(need[donor].Count)];
                var take = have[name][rng.Below(have[name].Count)];
                need[donor].Remove(give);
                need[name].Add(give);
                have[name].Remove(take);
                have[donor].Add(take);
                var swap = moved[give];
                moved[give] = moved[take];
                moved[take] = swap;
            }
        }

        /// Fisher-Yates over the order, skipping the pinned slots entirely so
        /// what vanilla put there is still there afterwards.
        static void Shuffle(IList<int> order, bool[] pinned, Rng rng)
        {
            var group = new List<int>();
            for (var i = 0; i < order.Count; i++)
                if (pinned == null || !pinned[i]) group.Add(i);
            for (var i = group.Count - 1; i > 0; i--)
            {
                var j = rng.Below(i + 1);
                var swap = order[group[i]];
                order[group[i]] = order[group[j]];
                order[group[j]] = swap;
            }
        }

        /// How many values each arena, or each recipe, holds — in one pass
        /// over the slots.
        static Dictionary<string, int> Sizes(List<Slot> slots, Func<Slot, string> keyed)
        {
            var sizes = new Dictionary<string, int>();
            foreach (var slot in slots)
            {
                var key = keyed(slot);
                int size;
                if (!sizes.TryGetValue(key, out size) || slot.Index >= size)
                    sizes[key] = slot.Index + 1;
            }
            return sizes;
        }

        void Note(string kind, World world, string family, Moved moved)
        {
            Note(kind, world, family, moved.Slot, moved.Value);
        }

        void Note(string kind, World world, string family, Slot slot, string value)
        {
            var where = slot.Scene == null ? slot.Key : slot.Scene + " " + slot.Key;
            Note(kind, where, world, family, slot.Value, value);
        }

        void Note(string kind, string where, World world, string family,
                  string was, string now)
        {
            if (was == now) return;
            Spoiler.Add(kind + "\t" + where + "\t" + world.Name(family, was)
                        + "\t" + world.Name(family, now));
        }

        public struct Moved
        {
            public Slot Slot;
            public string Value;
            public string Spawn;
        }

        /// Fisher-Yates over the values, leaving the slots where they are.
        /// shuffle false gives every slot its own value back, which is how a
        /// turned-off category is written out when it has to be written.
        static List<Moved> Permute(List<Slot> slots, string seed, string category,
                                   bool shuffle = true)
        {
            var rng = new Rng(seed + ":" + category);
            var order = new int[slots.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            if (shuffle) Shuffle(order, null, rng);

            var moved = new List<Moved>(slots.Count);
            for (var i = 0; i < slots.Count; i++)
            {
                var from = slots[order[i]];
                moved.Add(new Moved { Slot = slots[i], Value = from.Value, Spawn = from.Spawn });
            }
            return moved;
        }
    }
}
