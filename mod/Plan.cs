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

    /// How caves are shuffled.
    public enum EntranceShuffle
    {
        Off,
        /// Caves move in pairs: walking back out of a cave puts you at the
        /// cave mouth you came in by.
        On,
        /// Every cave mouth and cave exit leads somewhere random on its own.
        Decoupled,
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

    /// How far the spirit shuffle is allowed to go.
    public enum SpiritShuffle
    {
        Off,
        On,
        /// Without elder_percent, elder and plain spirits only swap with their
        /// own kind. With it, elder spirits only appear in elder spirit fights,
        /// and the percent only counts those fights. Bosses never move in any
        /// mode.
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
        public readonly Dictionary<string, string[]> Spirits = new Dictionary<string, string[]>();
        public readonly Dictionary<string, string> Cards = new Dictionary<string, string>();
        public readonly Dictionary<string, Ingredient[]> Recipes = new Dictionary<string, Ingredient[]>();
        public readonly Dictionary<string, string> Grants = new Dictionary<string, string>();
        /// Reward cards no reward hands over any more, which can be crafted now.
        public readonly HashSet<string> Crafted = new HashSet<string>();
        /// Scarce howls only: what each fight drops on its first win, one entry
        /// per spirit it spawns.
        public readonly Dictionary<string, List<Drop>> Drops = new Dictionary<string, List<Drop>>();
        public readonly List<string> Spoiler = new List<string>();

        public static string Key(string scene, string key)
        {
            return scene + "\u0001" + key;
        }

        /// Every shuffle is a permutation: same values, new places. Each
        /// category has its own seed, so turning one off leaves the rest
        /// unchanged.
        public static Plan Build(World world, string seed, Func<string, bool> enabled,
                                 EntranceShuffle entrances, SpiritShuffle spirits, int elderPercent, GrantShuffle grants,
                                 bool scarce)
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

            if (entrances != EntranceShuffle.Off)
            {
                var caves = CaveSlots(world);
                var moves = entrances == EntranceShuffle.Decoupled
                            ? Permute(caves, seed, "entrances")
                            : Couple(world, caves, seed);
                // Many caves share one area, so the spoiler names the spawn
                // point, which says which cave it is.
                foreach (var moved in moves)
                {
                    plan.Entrances[Key(moved.Slot.Scene, moved.Slot.Key)] =
                        new Entrance { Area = moved.Value, Spawn = moved.Spawn };
                    plan.Note("entrance", moved.Slot.Scene + " " + moved.Slot.Key, world,
                              "spawn", moved.Slot.Spawn, moved.Spawn);
                }
            }

            // Realms live on shared card assets that stay changed all session,
            // so with the toggle off, write the vanilla realms back.
            foreach (var moved in Permute(world.Cards, seed, "card_realms",
                                          enabled("card_realms")))
            {
                plan.Cards[moved.Slot.Key] = moved.Value;
                plan.Note("card", world, "cardtype", moved);
            }

            // Recipes live on the same assets, so off also writes vanilla back.
            plan.BuildRecipes(world, seed, enabled("recipes"));
            plan.BuildGrants(world, seed, grants);

            // Spirits come after the recipes: under scarce howls, how many of
            // each spirit there are depends on what the cards are made of.
            plan.BuildSpirits(world, seed, spirits, elderPercent, scarce);

            return plan;
        }

        /// Story areas, not caves: the Fylge memory, entered after beating a
        /// Fylge. The way in and out never moves.
        static readonly HashSet<string> StoryAreas = new HashSet<string>
        {
            "FylgeMemoryAreaData",
        };

        /// The cave events that can move: all but the ways into a story area,
        /// and the ways out of the scenes those lead into.
        static List<Slot> CaveSlots(World world)
        {
            var story = new HashSet<string>();
            foreach (var slot in world.Entrances)
            {
                Place place;
                if (StoryAreas.Contains(slot.Value) && slot.Spawn != null
                    && world.Spawns.TryGetValue(slot.Spawn, out place))
                    story.Add(place.Scene);
            }
            var slots = new List<Slot>();
            foreach (var slot in world.Entrances)
                if (!StoryAreas.Contains(slot.Value) && !story.Contains(slot.Scene))
                    slots.Add(slot);
            return slots;
        }

        /// Caves in pairs. A side is every cave event in one scene leading to
        /// the same spawn point (a boss cave can have two exits to one place).
        /// Outer sides stay put and inner sides are shuffled between them, so
        /// leaving a cave returns you to the mouth you entered. Sides with no
        /// partner, like unused test scenes, stay vanilla.
        static List<Moved> Couple(World world, List<Slot> slots, string seed)
        {
            var sides = new List<List<Slot>>();
            var byKey = new Dictionary<string, List<Slot>>();
            var perScene = new Dictionary<string, int>();
            foreach (var slot in Ordered(new List<Slot>(slots)))
            {
                var key = Key(slot.Scene, slot.Spawn);
                List<Slot> side;
                if (!byKey.TryGetValue(key, out side))
                {
                    byKey[key] = side = new List<Slot>();
                    sides.Add(side);
                    int count;
                    perScene.TryGetValue(slot.Scene, out count);
                    perScene[slot.Scene] = count + 1;
                }
                side.Add(slot);
            }

            // A side's partner is in the scene it leads into, and leads back
            // here. When two caves join the same two scenes, take the spawn
            // point closest to the partner's mouth.
            var best = new int[sides.Count];
            for (var i = 0; i < sides.Count; i++)
            {
                best[i] = -1;
                var a = sides[i][0];
                Place there;
                if (a.Spawn == null || !world.Spawns.TryGetValue(a.Spawn, out there)) continue;
                var nearest = double.MaxValue;
                for (var j = 0; j < sides.Count; j++)
                {
                    var b = sides[j][0];
                    Place back;
                    if (j == i || b.Scene != there.Scene || b.Spawn == null
                        || !world.Spawns.TryGetValue(b.Spawn, out back)
                        || back.Scene != a.Scene) continue;
                    var gap = Gap(there, b) + Gap(back, a);
                    if (gap < nearest)
                    {
                        nearest = gap;
                        best[i] = j;
                    }
                }
            }

            // Only sides that pick each other. The side in the scene with more
            // cave events is outer, so cave mouths keep leading into caves.
            // Ties go by scene name.
            var outer = new List<List<Slot>>();
            var inner = new List<List<Slot>>();
            for (var i = 0; i < sides.Count; i++)
            {
                var j = best[i];
                if (j <= i || best[j] != i) continue;
                var here = perScene[sides[i][0].Scene];
                var there = perScene[sides[j][0].Scene];
                var iOuter = here != there ? here > there
                             : string.CompareOrdinal(sides[i][0].Scene, sides[j][0].Scene) < 0;
                outer.Add(iOuter ? sides[i] : sides[j]);
                inner.Add(iOuter ? sides[j] : sides[i]);
            }

            var order = new int[outer.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Shuffle(order, null, new Rng(seed + ":entrances"));

            var moved = new List<Moved>();
            for (var i = 0; i < order.Length; i++)
            {
                var j = order[i];
                // Mouth i now opens into cave j, and cave j's exit comes back
                // out at mouth i.
                Lead(moved, outer[i], outer[j][0]);
                Lead(moved, inner[j], inner[i][0]);
            }
            return moved;
        }

        static double Gap(Place spawn, Slot mouth)
        {
            double dx = spawn.X - mouth.X, dy = spawn.Y - mouth.Y;
            return dx * dx + dy * dy;
        }

        /// Point every event on one side where another event goes.
        static void Lead(List<Moved> moved, List<Slot> side, Slot to)
        {
            foreach (var slot in side)
                moved.Add(new Moved { Slot = slot, Value = to.Value, Spawn = to.Spawn });
        }

        // A skill node's two rewards, by the index they were scanned under.
        const int CardNode = 0, TotemNode = 1;

        /// Skill nodes hand over totems like any other reward, so they share
        /// the pickups' permutation.
        static List<Slot> TotemSlots(World world)
        {
            var slots = new List<Slot>(world.Totems);
            foreach (var node in world.Nodes)
                if (node.Index == TotemNode) slots.Add(node);
            return Ordered(slots);
        }

        /// Which card each reward hands over: the aurora, and the blood tears
        /// and skill nodes that grant a card. Each reward gets a different card
        /// from the whole realm card list, and no card is given twice.
        ///
        /// Only rewards that give a realm card move. Quest cards are looked up
        /// by name later in the event, so they stay. Everything adds elder
        /// spirit gifts and Fylge cards to both the pool and the rewards. The
        /// alternate card set's versions stay out, since in a normal run they
        /// look like broken cards.
        void BuildGrants(World world, string seed, GrantShuffle mode)
        {
            var realms = new HashSet<string>();
            var pool = new List<string>();
            foreach (var slot in world.Cards)
            {
                realms.Add(slot.Key);
                if (!world.PlusOnly.Contains(slot.Key) && !pool.Contains(slot.Key))
                    pool.Add(slot.Key);
            }

            var gifts = new List<Slot>(world.Grants);
            foreach (var node in world.Nodes)
                if (node.Index == CardNode) gifts.Add(node);

            // A gift card can take another card's realm and recipe (see Lend),
            // which live on the card asset. So write every gift card's own back
            // first, even with the shuffle off.
            foreach (var slot in gifts)
            {
                string type;
                Ingredient[] own;
                if (world.Realmless.TryGetValue(slot.Value, out type))
                {
                    Cards[slot.Value] = type;
                    Recipes[slot.Value] = world.RealmlessRecipes.TryGetValue(slot.Value, out own)
                                          ? own : new Ingredient[0];
                }
                else if (realms.Contains(slot.Value) && !Recipes.ContainsKey(slot.Value))
                    Recipes[slot.Value] = new Ingredient[0];
            }

            if (mode == GrantShuffle.Off) return;

            var slots = new List<Slot>();
            foreach (var slot in gifts)
            {
                if (!realms.Contains(slot.Value))
                {
                    if (mode != GrantShuffle.Everything
                        || !world.Realmless.ContainsKey(slot.Value)
                        || world.PlusOnly.Contains(slot.Value)) continue;
                    // The gift joins the pool so it can turn up at any reward,
                    // and its own event can give something else.
                    if (!pool.Contains(slot.Value)) pool.Add(slot.Value);
                }
                slots.Add(slot);
            }
            // Nodes are scanned from loaded assets in whatever order Unity
            // picks, so sort by key.
            slots = Ordered(slots);
            pool.Sort(string.CompareOrdinal);

            if (slots.Count == 0 || slots.Count > pool.Count) return;
            var rng = new Rng(seed + ":card_grants");
            var order = new int[pool.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Shuffle(order, null, rng);
            var given = new List<string>();
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var value = pool[order[i]];
                given.Add(value);
                Grants[Key(slot.Scene, slot.Key)] = value;
                Note("gift", world, "card", slot, value);
            }
            Lend(world, slots, given);
        }

        /// A card only a reward used to give, and no reward gives now, would be
        /// lost. So it swaps with a card that became a reward: it takes that
        /// card's recipe, and its realm if it had none. Cards pair up in slot
        /// order. A new reward card with no recipe has nothing to lend and is
        /// skipped.
        void Lend(World world, List<Slot> slots, List<string> given)
        {
            var was = new HashSet<string>();
            foreach (var slot in slots) was.Add(slot.Value);
            var now = new HashSet<string>(given);

            var lost = new List<string>();
            foreach (var slot in slots)
                if (!now.Contains(slot.Value) && !lost.Contains(slot.Value)) lost.Add(slot.Value);

            var lenders = new List<string>();
            foreach (var card in given)
            {
                Ingredient[] recipe;
                if (!was.Contains(card) && Recipes.TryGetValue(card, out recipe)
                    && Array.Exists(recipe, line => line.Item != null))
                    lenders.Add(card);
            }

            for (var i = 0; i < lost.Count && i < lenders.Count; i++)
            {
                var card = lost[i];
                var lender = lenders[i];
                Recipes[card] = Array.FindAll(Recipes[lender], line => line.Item != null);
                string realm;
                if (world.Realmless.ContainsKey(card) && Cards.TryGetValue(lender, out realm))
                    Cards[card] = realm;
                Crafted.Add(card);
                Spoiler.Add("craft\t" + world.Name("card", card) + "\treward only\trecipe of "
                            + world.Name("card", lender));
            }
        }

        /// Slots in a fixed order, so a run doesn't depend on the order the
        /// game loaded its assets.
        static List<Slot> Ordered(List<Slot> slots)
        {
            slots.Sort((a, b) => string.CompareOrdinal(Key(a.Scene, a.Key),
                                                       Key(b.Scene, b.Key)));
            return slots;
        }

        /// Shuffles the ingredients cards are crafted from, one at a time.
        /// Amounts stay put, so a card needs the same counts of different
        /// things.
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
                Ingredient[] recipe;
                if (!Recipes.TryGetValue(slot.Key, out recipe))
                    Recipes[slot.Key] = recipe = new Ingredient[sizes[slot.Key]];
                if (slot.Index < recipe.Length)
                    recipe[slot.Index] = new Ingredient { Item = values[i], Amount = slot.Amount };
                Note("recipe", world.Name("card", slot.Key) + " #" + slot.Index,
                     world, "item", slot.Value, values[i]);
            }
        }

        /// No vanilla recipe asks for the same ingredient twice, and duplicate
        /// slots on the crafting screen look like a bug. So a repeat swaps with
        /// a later slot that can take it. The last few slots may have nothing
        /// to swap with, leaving the repeat.
        static void Unrepeat(List<Slot> slots, string[] values)
        {
            var wanted = new Dictionary<string, HashSet<string>>();   // card -> what it asks for
            foreach (var slot in slots)
                if (!wanted.ContainsKey(slot.Key)) wanted[slot.Key] = new HashSet<string>();

            for (var i = 0; i < slots.Count; i++)
            {
                var mine = wanted[slots[i].Key];
                if (mine.Add(values[i])) continue;
                // Forward only: earlier slots are settled, and later ones get
                // their own turn.
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

        // Spirit ranks, and the arena type of an elder spirit fight.
        const int Elder = 1, Boss = 2, ElderArena = 1;

        /// Things in spirit fights that aren't spirits. They only swap with
        /// each other, never with spirits.
        static readonly HashSet<string> Environmental = new HashSet<string>
        {
            "Lifeblood (ThornyBushSpecialTile)", "BerryTree", "SwirlOfFrailty", "SwirlOfStrength", "ExplodingPlant",
            "TotemRock",
        };

        /// Spirits move as species, not prefabs: Owl and OwlElite are one
        /// spirit in two forms. The shuffle moves spirits, then the dial picks
        /// which are elder spirits, keeping both forms of each spirit somewhere
        /// so no ingredient goes missing. Off still runs, since the dial works
        /// on its own. Under scarce howls each fight drops loot once, so enough
        /// spawns of each form are set aside for the cards first.
        void BuildSpirits(World world, string seed, SpiritShuffle mode, int percent, bool scarce)
        {
            var slots = world.Spirits;
            var elder = new Dictionary<string, string>();    // species -> elder form
            var plain = new Dictionary<string, string>();    // elder form -> species
            Pairs(world.Rarity, elder, plain);

            // Split what vanilla puts in each slot into a spirit and a form.
            var species = new string[slots.Count];
            var wasElder = new bool[slots.Count];
            for (var i = 0; i < slots.Count; i++)
            {
                string common;
                wasElder[i] = plain.TryGetValue(slots[i].Value, out common);
                species[i] = wasElder[i] ? common : slots[i].Value;
            }

            // Which spots can hold an elder spirit, and which a plain one.
            // Strict keeps each spot's vanilla form. Restricted with a percent
            // keeps elder spirits in elder spirit fights.
            var strict = mode == SpiritShuffle.Restricted && percent < 0;
            var elderSpot = new bool[slots.Count];
            var plainSpot = new bool[slots.Count];
            for (var i = 0; i < slots.Count; i++)
            {
                elderSpot[i] = strict ? wasElder[i]
                    : mode != SpiritShuffle.Restricted || slots[i].Arena == ElderArena;
                plainSpot[i] = !strict || !wasElder[i];
            }

            // Move the spirits. Boss tier stays put, so boss fights stay
            // vanilla. Strict shuffles elder and plain spots separately.
            // Environmental things only swap with each other.
            var order = new int[slots.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            var pinned = new bool[slots.Count];
            var environmental = new bool[slots.Count];
            for (var i = 0; i < slots.Count; i++)
            {
                environmental[i] = Environmental.Contains(slots[i].Value);
                pinned[i] = !environmental[i] && IsBossTier(slots[i].Value, world.Rarity, plain);
            }
            var rng = new Rng(seed + ":spirits");
            if (mode != SpiritShuffle.Off)
            {
                var skip = new bool[slots.Count];
                for (var i = 0; i < slots.Count; i++) skip[i] = !environmental[i];
                Shuffle(order, skip, rng);
                if (strict)
                {
                    for (var i = 0; i < slots.Count; i++)
                        skip[i] = pinned[i] || environmental[i] || wasElder[i];
                    Shuffle(order, skip, rng);
                    for (var i = 0; i < slots.Count; i++)
                        skip[i] = pinned[i] || environmental[i] || !wasElder[i];
                    Shuffle(order, skip, rng);
                }
                else
                {
                    for (var i = 0; i < slots.Count; i++) skip[i] = pinned[i] || environmental[i];
                    Shuffle(order, skip, rng);
                }
            }

            var moved = new string[slots.Count];
            var isElder = new bool[slots.Count];
            for (var i = 0; i < slots.Count; i++)
            {
                moved[i] = species[order[i]];
                isElder[i] = wasElder[order[i]];
            }

            // Environmental things are done moving. Pin them so scarce howls
            // can't hand their spots to spirits. Their drops get picked along
            // with the bosses'.
            for (var i = 0; i < slots.Count; i++) pinned[i] |= environmental[i];

            // Then the dial. It runs whenever anything moves, since its
            // one-of-each-form seed keeps every ingredient in the world; a
            // fully vanilla run already has both forms. Under scarce howls
            // drops are picked here too: boss fights first, since they never
            // move, and the quota covers what they leave short.
            var missing = scarce ? Missing(world) : null;
            var drops = new Rng(seed + ":drops");
            if (scarce) Allot(world, slots, pinned, true, moved, isElder, elder, missing, drops);

            if (percent >= 0 || mode != SpiritShuffle.Off || scarce)
            {
                var vanilla = 0;
                foreach (var one in wasElder) if (one) vanilla++;
                var quota = scarce ? Quota(world, pinned, moved, elder, missing) : null;
                Promote(moved, isElder, slots, pinned, elderSpot, plainSpot, strict, elder, mode,
                        seed, percent, vanilla, quota);
            }

            if (scarce) Allot(world, slots, pinned, false, moved, isElder, elder, missing, drops);

            var sizes = Sizes(slots, slot => Key(slot.Scene, slot.Key));
            for (var i = 0; i < slots.Count; i++)
            {
                var name = isElder[i] ? elder[moved[i]] : moved[i];
                var key = Key(slots[i].Scene, slots[i].Key);
                string[] arena;
                if (!Spirits.TryGetValue(key, out arena))
                    Spirits[key] = arena = new string[sizes[key]];
                if (slots[i].Index < arena.Length) arena[slots[i].Index] = name;
                Note("spirit", world, "prefab", slots[i], name);
            }
        }

        /// Which spirits have two forms. The elder one is named after the
        /// common one (OwlElite, Owl), but only the prefab's rank is reliable;
        /// a few "Elite" names are ranked common.
        static void Pairs(Dictionary<string, int> rarity, Dictionary<string, string> elder,
                          Dictionary<string, string> plain)
        {
            var lower = new Dictionary<string, string>();
            foreach (var name in rarity.Keys) lower[name.ToLowerInvariant()] = name;
            foreach (var entry in rarity)
            {
                if (entry.Value != Elder) continue;
                string bare;
                if (!lower.TryGetValue(entry.Key.ToLowerInvariant().Replace("elite", ""),
                                       out bare)) continue;
                if (rarity[bare] != 0) continue;
                elder[bare] = entry.Key;
                plain[entry.Key] = bare;
            }
        }

        /// Bosses, and elder spirits with no common form, like the Great
        /// Spirits' arms, which only fit their own fight. Anything unranked
        /// counts as boss tier and stays put.
        static bool IsBossTier(string value, Dictionary<string, int> rarity,
                               Dictionary<string, string> plain)
        {
            int rank;
            if (!rarity.TryGetValue(value, out rank)) return true;
            return rank == Boss || (rank == Elder && !plain.ContainsKey(value));
        }

        /// Gives the elder form to that percent of the spawns that can take it.
        /// Both forms drop their own ingredients, so a seed goes first: each
        /// species gets one elder spawn and one plain. The dial fills the rest,
        /// so 0% is one elder of each kind and 100% one plain of each. Below
        /// zero the dial is off and vanilla's elder count stands, placed by the
        /// seed. Restricted with a percent only counts elder spirit fights, so
        /// 50% is half of those. Strict keeps vanilla forms, so there's nothing
        /// to pick. With a quota (scarce howls) the seed is the quota instead
        /// of one of each, and the dial can't touch spawns the quota claimed.
        static void Promote(string[] moved, bool[] isElder, List<Slot> slots, bool[] pinned,
                            bool[] elderSpot, bool[] plainSpot, bool strict,
                            Dictionary<string, string> elder, SpiritShuffle mode,
                            string seed, int percent, int vanilla,
                            Dictionary<string, int> quota)
        {
            var rng = new Rng(seed + ":elders");
            // Stock moves whole spawns, so it runs before forms are picked. Off
            // moves nothing, so there the quota only picks forms.
            if (quota != null && mode != SpiritShuffle.Off)
                Stock(moved, slots, pinned, elder, quota, elderSpot, plainSpot, rng);

            if (strict)
            {
                for (var i = 0; i < moved.Length; i++)
                    isElder[i] = elderSpot[i] && elder.ContainsKey(moved[i]);
                return;
            }

            // For each species: spawns that can be elder, and spawns that must
            // stay plain. Under Restricted only elder spirit fights can be
            // elder, so the other spawns cover the plain form by themselves.
            var canElder = new Dictionary<string, List<int>>();
            var plainOnly = new Dictionary<string, List<int>>();
            var open = new List<int>();
            for (var i = 0; i < moved.Length; i++)
            {
                isElder[i] = false;
                var name = moved[i];
                if (!elder.ContainsKey(name)) continue;
                if (!canElder.ContainsKey(name))
                {
                    canElder[name] = new List<int>();
                    plainOnly[name] = new List<int>();
                }
                if (!elderSpot[i]) plainOnly[name].Add(i);
                else { canElder[name].Add(i); open.Add(i); }
            }

            // Stock already gave every form the spawns it needs; Even would
            // trade some of them away again.
            if (mode == SpiritShuffle.Restricted && quota == null)
                Even(canElder, plainOnly, moved, rng);

            // One of each form per species first, from the same shuffle, so the
            // seed decides which spawn gets which. A species with a plain-only
            // spawn already has its plain form.
            Shuffle(open, null, rng);
            var spare = new List<int>();
            int seeded;
            if (quota != null) seeded = Seed(open, moved, isElder, slots, plainOnly, elder, quota, spare);
            else
            {
                var hasElder = new HashSet<string>();
                var hasPlain = new HashSet<string>();
                foreach (var i in open)
                {
                    var name = moved[i];
                    var covered = plainOnly[name].Count > 0;
                    if ((covered || canElder[name].Count > 1) && hasElder.Add(name)) isElder[i] = true;
                    else if (covered || !hasPlain.Add(name)) spare.Add(i);
                }
                seeded = hasElder.Count;
            }

            var want = percent >= 0 ? (open.Count * percent + 50) / 100 : vanilla;
            for (var i = 0; i < want - seeded && i < spare.Count; i++) isElder[spare[i]] = true;
        }

        /// Scarce howls pays out each fight once, so every ingredient has to
        /// come from a pickup or a first win. Returns how many of each
        /// ingredient the pickups leave short. Each craftable card counts once
        /// per allowed copy. Reward cards and cards outside the realms aren't
        /// craftable unless Lend made them so.
        Dictionary<string, double> Missing(World world)
        {
            var missing = new Dictionary<string, double>();
            foreach (var entry in Recipes)
            {
                int copies;
                if (!world.Copies.TryGetValue(entry.Key, out copies) || copies == 0) continue;
                if (!Crafted.Contains(entry.Key) && (world.Rewards.Contains(entry.Key)
                                                     || world.Realmless.ContainsKey(entry.Key)))
                    continue;
                foreach (var line in entry.Value)
                    if (line.Item != null) Tally(missing, line.Item, copies * line.Amount);
            }
            foreach (var slot in world.Ingredients) Tally(missing, slot.Value, -1);
            return missing;
        }

        /// How many spawns of each spirit form are needed to drop what's
        /// missing. The plan picks every drop, so a form with several possible
        /// drops can cover all of them, one spawn each. An ingredient dropped
        /// by several forms is split evenly.
        static Dictionary<string, int> Quota(World world, bool[] pinned, string[] moved,
                                             Dictionary<string, string> elder,
                                             Dictionary<string, double> missing)
        {
            var forms = new HashSet<string>();
            for (var i = 0; i < moved.Length; i++)
            {
                if (pinned[i]) continue;
                forms.Add(moved[i]);
                string form;
                if (elder.TryGetValue(moved[i], out form)) forms.Add(form);
            }

            var droppers = new Dictionary<string, double>();    // ingredient -> forms that drop it
            foreach (var form in forms)
            {
                string[] loot;
                if (!world.Loot.TryGetValue(form, out loot)) continue;
                foreach (var item in new HashSet<string>(loot)) Tally(droppers, item, 1);
            }

            var quota = new Dictionary<string, int>();
            foreach (var form in forms)
            {
                string[] loot;
                if (!world.Loot.TryGetValue(form, out loot)) continue;
                var spawns = 0.0;
                foreach (var item in new HashSet<string>(loot))
                {
                    double gap;
                    if (missing.TryGetValue(item, out gap) && gap > 0) spawns += gap / droppers[item];
                }
                // A hair off before rounding up, so 2.0000001 stays 2.
                var count = (int)Math.Ceiling(spawns - 1e-6);
                if (count > 0) quota[form] = count;
            }
            return quota;
        }

        /// Picks each spawn's drop, in either the pinned slots or the rest.
        /// Each spawn drops whichever of its drops is furthest short, or a
        /// random one if none are, like the game. These replace the game's roll
        /// on a fight's first win.
        void Allot(World world, List<Slot> slots, bool[] pinned, bool which, string[] moved,
                   bool[] isElder, Dictionary<string, string> elder,
                   Dictionary<string, double> missing, Rng rng)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (pinned[i] != which) continue;
                var name = isElder[i] ? elder[moved[i]] : moved[i];
                string[] loot;
                if (!world.Loot.TryGetValue(name, out loot) || loot.Length == 0) continue;
                int rank;
                world.Rarity.TryGetValue(name, out rank);

                var key = Key(slots[i].Scene, slots[i].Key);
                List<Drop> arena;
                if (!Drops.TryGetValue(key, out arena)) Drops[key] = arena = new List<Drop>();
                for (var n = 0; n < slots[i].Amount; n++)
                {
                    var item = Pick(loot, missing, rng);
                    Tally(missing, item, -1);
                    arena.Add(new Drop { Item = item, Elder = rank != 0 });
                    Spoiler.Add("drop\t" + slots[i].Scene + " " + slots[i].Key + "\t"
                                + name + "\t" + world.Name("item", item));
                }
            }
        }

        static string Pick(string[] loot, Dictionary<string, double> missing, Rng rng)
        {
            var best = new List<string>();
            var most = 0.0;
            foreach (var item in loot)
            {
                double gap;
                missing.TryGetValue(item, out gap);
                if (gap <= 0 || gap < most) continue;
                if (gap > most)
                {
                    best.Clear();
                    most = gap;
                }
                best.Add(item);
            }
            return best.Count > 0 ? best[rng.Below(best.Count)] : loot[rng.Below(loot.Length)];
        }

        static void Tally(Dictionary<string, double> counts, string key, double amount)
        {
            double had;
            counts.TryGetValue(key, out had);
            counts[key] = had + amount;
        }

        static int Get(Dictionary<string, int> quota, string form)
        {
            int count;
            return form != null && quota.TryGetValue(form, out count) ? count : 0;
        }

        /// Scarce howls only: gives each species enough spawns for its quota. A
        /// short species takes a slot from one with spawns to spare, so spirit
        /// counts change, unlike the plain shuffle. An elder shortfall takes a
        /// spot elder spirits may stand in, and under strict a plain shortfall
        /// takes a plain spot. Otherwise a plain shortfall prefers spots elder
        /// spirits can't use. Bosses are never taken. If nobody can spare a
        /// spawn, the species stays short.
        static void Stock(string[] moved, List<Slot> slots, bool[] pinned,
                          Dictionary<string, string> elder, Dictionary<string, int> quota,
                          bool[] elderSpot, bool[] plainSpot, Rng rng)
        {
            var total = new Dictionary<string, int>();      // species -> spawns
            var room = new Dictionary<string, int>();       // species -> spawns that can be elder
            var plainRoom = new Dictionary<string, int>();  // species -> spawns that can be plain
            for (var i = 0; i < slots.Count; i++)
            {
                if (pinned[i]) continue;
                var name = moved[i];
                if (!total.ContainsKey(name)) { total[name] = 0; room[name] = 0; plainRoom[name] = 0; }
                total[name] += slots[i].Amount;
                if (elderSpot[i]) room[name] += slots[i].Amount;
                if (plainSpot[i]) plainRoom[name] += slots[i].Amount;
            }

            var needElder = new Dictionary<string, int>();
            var needPlain = new Dictionary<string, int>();
            var needAll = new Dictionary<string, int>();
            var names = new List<string>(total.Keys);
            names.Sort(string.CompareOrdinal);
            foreach (var name in names)
            {
                string form;
                needElder[name] = elder.TryGetValue(name, out form) ? Get(quota, form) : 0;
                needPlain[name] = Get(quota, name);
                needAll[name] = needElder[name] + needPlain[name];
            }

            foreach (var name in names)
            {
                while (true)
                {
                    var elderShort = room[name] < needElder[name];
                    var plainShort = plainRoom[name] < needPlain[name];
                    if (!elderShort && !plainShort && total[name] >= needAll[name]) break;

                    var best = new List<int>();
                    var fallback = new List<int>();
                    for (var i = 0; i < slots.Count; i++)
                    {
                        var donor = moved[i];
                        var amount = slots[i].Amount;
                        if (pinned[i] || donor == name || amount == 0) continue;
                        if (elderShort ? !elderSpot[i] : plainShort && !plainSpot[i]) continue;
                        if (total[donor] - amount < needAll[donor]) continue;
                        if (elderSpot[i] && room[donor] - amount < needElder[donor]) continue;
                        if (plainSpot[i] && plainRoom[donor] - amount < needPlain[donor]) continue;
                        (elderSpot[i] && !elderShort ? fallback : best).Add(i);
                    }
                    if (best.Count == 0) best = fallback;
                    if (best.Count == 0) break;

                    var take = best[rng.Below(best.Count)];
                    var from = moved[take];
                    var spawns = slots[take].Amount;
                    total[from] -= spawns;
                    total[name] += spawns;
                    if (elderSpot[take])
                    {
                        room[from] -= spawns;
                        room[name] += spawns;
                    }
                    if (plainSpot[take])
                    {
                        plainRoom[from] -= spawns;
                        plainRoom[name] += spawns;
                    }
                    moved[take] = name;
                }
            }
        }

        /// The seed under scarce howls. Each species gets its quota of each
        /// form, and at least one of each where there's room. Each slot goes to
        /// the form further short, plain on a tie. Leftovers are spare for the
        /// dial. Returns how many slots became elder spirits.
        static int Seed(List<int> open, string[] moved, bool[] isElder, List<Slot> slots,
                        Dictionary<string, List<int>> plainOnly, Dictionary<string, string> elder,
                        Dictionary<string, int> quota, List<int> spare)
        {
            var gotElder = new Dictionary<string, int>();
            var gotPlain = new Dictionary<string, int>();
            foreach (var entry in plainOnly)
            {
                var spawns = 0;
                foreach (var i in entry.Value) spawns += slots[i].Amount;
                gotPlain[entry.Key] = spawns;
                gotElder[entry.Key] = 0;
            }

            var made = 0;
            foreach (var i in open)
            {
                var name = moved[i];
                var amount = slots[i].Amount;
                var elderGap = Math.Max(1, Get(quota, elder[name])) - gotElder[name];
                var plainGap = Math.Max(1, Get(quota, name)) - gotPlain[name];
                if (amount > 0 && elderGap > Math.Max(plainGap, 0))
                {
                    isElder[i] = true;
                    gotElder[name] += amount;
                    made++;
                }
                else if (amount > 0 && plainGap > 0) gotPlain[name] += amount;
                else spare.Add(i);
            }
            return made;
        }

        /// Restricted can put all of a species' spawns in elder spirit fights,
        /// or none, leaving one form nowhere to stand and its ingredients lost.
        /// Swap one spawn for the missing kind from a species with at least two
        /// of that kind, so the donor keeps both forms. Arena sizes hold, and
        /// elder forms stay in elder spirit fights. A species with one spawn
        /// can only have one form.
        static void Even(Dictionary<string, List<int>> canElder, Dictionary<string, List<int>> plainOnly,
                         string[] moved, Rng rng)
        {
            var names = new List<string>(canElder.Keys);
            names.Sort(string.CompareOrdinal);
            foreach (var name in names)
            {
                var wantsElder = canElder[name].Count == 0;
                var need = wantsElder ? canElder : plainOnly;   // the kind it has none of
                var have = wantsElder ? plainOnly : canElder;   // the kind it gives one up from
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

        /// Fisher-Yates over the order, skipping pinned slots so they keep
        /// their vanilla value.
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

        /// How many values each arena or recipe holds, in one pass.
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

        /// One planned drop: the item, and whether an elder spirit or boss
        /// drops it, which only changes the puff it comes out of.
        public struct Drop
        {
            public string Item;
            public bool Elder;
        }

        public struct Moved
        {
            public Slot Slot;
            public string Value;
            public string Spawn;
        }

        /// Fisher-Yates over the values, leaving slots in place. With shuffle
        /// false every slot gets its own value back, for writing out a category
        /// that's off.
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
