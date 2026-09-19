using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RandomHowl
{
    /// Builds the mixed spirits the chimeras option asks for.
    ///
    /// Each one is a copy of the spirit's own prefab, kept under a hidden,
    /// switched off holder so it never wakes up. Arenas spawn from the copy
    /// instead of the prefab. The copy keeps its body: sprites, animations,
    /// sounds, effects and drops. Stats, attack cards, AI settings and
    /// traits like retaliate are copied in from the spirits the plan picked.
    public static class Chimeras
    {
        static ManualLogSource log;
        static Plan plan;
        static GameObject holder;
        static readonly Dictionary<string, GameObject> built = new Dictionary<string, GameObject>();

        static readonly Type SetterType = AccessTools.TypeByName("MovementModuleSetter");
        static readonly MethodInfo Clone = AccessTools.Method(typeof(object), "MemberwiseClone");

        static readonly string[] AiFields =
        {
            "intelligence", "targetPriority", "skipMoveIfCanAttack", "skipTurnIfNoAbility",
            "allowTurnTowardsPlayerAtEndOfMovement", "customMovementModule",
        };

        /// Called on every rebuild. Copies from the last plan are thrown away.
        public static void Reset(Plan shuffled, ManualLogSource logger)
        {
            plan = shuffled;
            log = logger;
            built.Clear();
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null;
        }

        /// The prefab an arena should spawn for this spirit. The spirit's own
        /// prefab unless it's a chimera.
        public static GameObject Prefab(string name)
        {
            Chimera parts;
            if (plan == null || !plan.Chimeras.TryGetValue(name, out parts))
                return Registry.Spirit(name);
            GameObject made;
            if (!built.TryGetValue(name, out made))
                built[name] = made = Build(name, parts);
            return made != null ? made : Registry.Spirit(name);
        }

        static GameObject Build(string name, Chimera parts)
        {
            var body = Registry.Spirit(name);
            if (body == null) return null;
            if (holder == null)
            {
                holder = new GameObject("RandomHowl chimeras");
                holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(holder);
            }
            var copy = UnityEngine.Object.Instantiate(body, holder.transform);
            copy.name = body.name;
            try
            {
                var enemy = copy.GetComponent(Registry.EnemyType);
                var kit = new Kit(enemy);
                Stats(enemy, Donor(parts.Stats));
                Attacks(enemy, Donor(parts.Attacks), kit);
                Ai(enemy, Donor(parts.Ai));
                Traits(enemy, Donor(parts.Traits), kit);
                return copy;
            }
            catch (Exception e)
            {
                log.LogError("could not build chimera " + name + ", using the plain spirit: " + e);
                UnityEngine.Object.Destroy(copy);
                return null;
            }
        }

        /// A donor's own prefab, never another chimera, so every part is vanilla.
        static Component Donor(string name)
        {
            var prefab = Registry.Spirit(name);
            if (prefab == null) log.LogWarning("chimera part from missing spirit " + name);
            return prefab == null ? null : prefab.GetComponent(Registry.EnemyType);
        }

        static void Stats(Component enemy, Component donor)
        {
            if (donor == null) return;
            var stats = Fields.Get(donor, "stats");
            Fields.Set(enemy, "stats", stats == null ? null : Clone.Invoke(stats, null));
            Fields.Set(enemy, "extraPlusHealth", Fields.Get(donor, "extraPlusHealth"));
        }

        /// Where a spirit keeps its abilities: its attacks, then the ones it
        /// uses when hit, at the start of the player's turn and when it dies.
        static readonly string[] Slots =
        {
            "abilities", "onDamagedAbilities", "startOfPlayerTurnAbilities", "onDeathAbility",
        };

        /// What makes an ability object play a card, beyond its animations.
        static readonly string[] AbilityFields =
        {
            "data", "aiRequirements", "otherCardsThatSHouldUseThisAsFallback",
            "warnIfUsable", "channelTime",
        };

        /// A slot's abilities as a list, whether it holds a list or just one.
        static List<Component> Abilities(Component enemy, string slot)
        {
            var list = new List<Component>();
            var value = Fields.Get(enemy, slot);
            var many = value as IList;
            if (many != null)
            {
                foreach (var ability in many)
                {
                    var one = ability as Component;
                    if (one != null) list.Add(one);
                }
            }
            else
            {
                var one = value as Component;
                if (one != null) list.Add(one);
            }
            return list;
        }

        /// The copy's ability objects as the body had them. The copy keeps
        /// these so its own animations and effects play, but they get the
        /// donor's cards and AI rules. Each object is handed out once, so
        /// none has to play two cards.
        class Kit
        {
            readonly Dictionary<string, List<Component>> slots = new Dictionary<string, List<Component>>();
            readonly HashSet<Component> used = new HashSet<Component>();
            readonly GameObject root;

            public Kit(Component enemy)
            {
                root = enemy.gameObject;
                foreach (var slot in Slots) slots[slot] = Abilities(enemy, slot);
            }

            /// An object set up to play the donor's ability `from`, which sits
            /// at `index` in `slot`. The body's object in that same place if
            /// it's free and of the same kind. Otherwise a new copy of one of
            /// the same kind, from that slot or else the attacks. Null if the
            /// body has nothing of that kind.
            public Component Take(Component from, string slot, int index)
            {
                var type = from.GetType();
                var mine = slots[slot];
                var to = index < mine.Count ? mine[index] : null;
                if (to == null || to.GetType() != type || used.Contains(to))
                {
                    var template = Template(type, mine, index) ?? Template(type, slots["abilities"], 0);
                    if (template == null) return null;
                    var made = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                    made.name = template.gameObject.name;
                    to = made.GetComponent(type);
                }
                used.Add(to);
                foreach (var field in AbilityFields) Fields.Set(to, field, Fields.Get(from, field));
                return to;
            }

            /// The first object of this kind, looking from `index` onward and
            /// wrapping around. Never the spirit's own root, which would copy
            /// the whole spirit.
            Component Template(Type type, List<Component> list, int index)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var ability = list[(index + i) % list.Count];
                    if (ability.GetType() == type && ability.gameObject != root) return ability;
                }
                return null;
            }
        }

        /// The donor's abilities in one slot, played by the copy's objects,
        /// in the donor's order. The spirit uses the first attack in the list
        /// it can. Abilities the copy has no object for are left out.
        static void Borrow(Component enemy, Component donor, Kit kit, string slot)
        {
            var theirs = Abilities(donor, slot);
            var taken = new List<Component>();
            foreach (var from in theirs)
            {
                var to = kit.Take(from, slot, taken.Count);
                if (to != null) taken.Add(to);
            }
            var field = Fields.Of(enemy, slot);
            if (field == null) return;
            if (typeof(IList).IsAssignableFrom(field.FieldType))
            {
                var list = (IList)Activator.CreateInstance(field.FieldType);
                foreach (var ability in taken) list.Add(ability);
                Fields.Set(enemy, slot, list);
            }
            else
            {
                Fields.Set(enemy, slot, taken.Count > 0 ? taken[0] : null);
            }
        }

        /// How far away it likes to stand and when it runs off go with the
        /// attacks, so a spirit with melee attacks doesn't flee.
        static readonly string[] RangeFields = { "effectiveRange", "customFleeRange", "customFleeChance" };

        static void Attacks(Component enemy, Component donor, Kit kit)
        {
            if (donor == null || Abilities(donor, "abilities").Count == 0) return;
            Borrow(enemy, donor, kit, "abilities");
            foreach (var field in RangeFields) Fields.Set(enemy, field, Fields.Get(donor, field));
        }

        /// Retaliate, deathtouch, fragile from behind, block each round and
        /// the like, plus the abilities that go with them: on hit, at the
        /// start of the player's turn and on death. Whether the spirit has a
        /// facing stays the body's, since that comes with its sprites.
        static void Traits(Component enemy, Component donor, Kit kit)
        {
            if (donor == null) return;
            var mine = Fields.Get(enemy, "startingModifiers");
            var theirs = Fields.Get(donor, "startingModifiers");
            if (mine != null && theirs != null)
            {
                var modifiers = Clone.Invoke(theirs, null);
                // A plain copy shares the donor's lists, so give it its own.
                foreach (var field in new[] { "plusModifiers", "damageModifiers" })
                {
                    var list = Fields.Get<IList>(modifiers, field);
                    if (list != null) Fields.Set(modifiers, field, Activator.CreateInstance(list.GetType(), list));
                }
                Fields.Set(modifiers, "nonDirectional", Fields.Get(mine, "nonDirectional"));
                Fields.Set(enemy, "startingModifiers", modifiers);
            }
            for (var i = 1; i < Slots.Length; i++) Borrow(enemy, donor, kit, Slots[i]);
        }

        /// Movement modules only use the spirit handed to them, so the copy
        /// can use the ones on the donor's prefab directly.
        static void Ai(Component enemy, Component donor)
        {
            if (donor == null) return;
            foreach (var field in AiFields) Fields.Set(enemy, field, Fields.Get(donor, field));
            if (SetterType == null) return;

            // Some spirits pick their movement when the fight starts, by
            // whether rebirth enemies are on. The copy needs the donor's pick.
            var theirs = donor.GetComponent(SetterType);
            var mine = enemy.GetComponent(SetterType);
            if (mine == null)
            {
                if (theirs == null) return;
                mine = enemy.gameObject.AddComponent(SetterType);
                Fields.Set(mine, "owner", enemy);
            }
            var module = Fields.Get(donor, "customMovementModule");
            Fields.Set(mine, "normalModeMovementModule",
                       theirs == null ? module : Fields.Get(theirs, "normalModeMovementModule"));
            Fields.Set(mine, "plusModeMovementModule",
                       theirs == null ? module : Fields.Get(theirs, "plusModeMovementModule"));
        }
    }
}
