using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RandomHowl
{
    /// Extra choices on the game's custom mode screen, and the card cost rules
    /// behind them. They work in any custom mode game, randomized or not.
    ///
    /// "These cards cost 1 more" becomes one toggle per kind of card. "Realm
    /// cards cost 1 more" gets an ALL REALMS choice. ENERGY sits next to Ro's
    /// health.
    public static class Custom
    {
        static ManualLogSource log;
        static MethodBase typePenalty;
        static MethodBase realmPenalty;

        // True while we call the game's own cost check, so our patch lets it run.
        static bool asking;

        // The settings UnlockCardBluePrint is borrowing, and what to put back.
        static object lentSettings;
        static int lentIndex;

        // The realm choices: ForeignCards, InRealmCards, None, then ours.
        const int ForeignCards = 0;
        const int InRealmCards = 1;
        const int AllRealms = 3;

        // The kinds of card, as the game numbers them. 0 is none.
        const int FirstKind = 1;
        const int RandomCards = 7;
        static readonly string[] KindLabels =
        {
            null, "CURSES", "SPIRIT CARDS", "QUEST CARDS", "COMMON CARDS",
            "RARE CARDS", "ALL CARDS", "RANDOM CARDS",
        };

        // The game saves one kind as a number. With this bit set, the number is
        // a set of kinds instead, one bit each.
        const int Several = 1 << 16;

        static readonly int[] EnergyValues = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // Custom mode choices as the screen was last left: game settings field,
        // then its config entry.
        static readonly Dictionary<string, ConfigEntryBase> remembered =
            new Dictionary<string, ConfigEntryBase>();

        public static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            var config = Plugin.Instance.Config;
            Remember(config, "alternateCardSet", "rebirth_cards", false,
                     "use rebirth mode's cards");
            Remember(config, "advancedEnemies", "rebirth_enemies", false,
                     "use rebirth mode's enemies");
            Remember(config, "customEnemyHeathPercent", "enemy_health_percent", 100,
                     "enemy health, as a percent of normal");
            Remember(config, "customPlayerHealth", "player_health", 20, "Ro's health");
            Remember(config, "highDeckMinLimit", "high_deck_minimum", false,
                     "decks need at least 20 cards instead of 15");
            Remember(config, "customCraftingLimit", "crafting_limit", -1,
                     "how many copies of each card can be crafted. -1 is the normal limit");
            Remember(config, "realmManaPenaltyIndex", "realm_cost", 0,
                     "which realm cards cost 1 more. 0 foreign cards, 1 cards from the "
                     + "realm you're in, 2 none, 3 all realms");
            Remember(config, "typeManaPenaltyIndex", "kind_cost", 0,
                     "which kinds of card cost 1 more. 0 none, 1 curses, 2 spirit, 3 quest, "
                     + "4 common, 5 rare, 6 all, 7 random. Several kinds at once are "
                     + "65536 plus 2 to the power of each kind; easier to set on the screen");
            Remember(config, "returnToGroveOnDeath", "return_to_grove", false,
                     "go back to the grove when Ro dies");

            typePenalty = Patch(harmony, "CardData", "HasTypeManaPenalty", nameof(KindCost));
            realmPenalty = Patch(harmony, "CardData", "HasRealmManaPenalty", nameof(RealmCost));
            Patch(harmony, "LiveGameDataManager", "UnlockCardBluePrint", nameof(Unlocking),
                  finalizer: nameof(Unlocked));
            Patch(harmony, "CustomModeSelectionMenu", "SetUp", nameof(MenuOpening),
                  postfix: nameof(MenuShown));
            Patch(harmony, "CustomModeSelectionMenu", "ConfirmSettings", null,
                  postfix: nameof(MenuConfirmed));
        }

        static MethodBase Patch(Harmony harmony, string className, string methodName,
                                string prefix, string postfix = null, string finalizer = null)
        {
            try
            {
                var type = AccessTools.TypeByName(className);
                var original = type == null ? null : AccessTools.Method(type, methodName);
                if (original == null)
                {
                    log.LogWarning("no " + className + "." + methodName
                                   + " — skipping that part of custom mode");
                    return null;
                }
                harmony.Patch(original, Ours(prefix), Ours(postfix), null, Ours(finalizer), null);
                return original;
            }
            catch (Exception e)
            {
                log.LogError("could not patch " + className + "." + methodName + ": " + e.Message);
                return null;
            }
        }

        static HarmonyMethod Ours(string name)
        {
            return name == null ? null : new HarmonyMethod(typeof(Custom).GetMethod(name));
        }

        static void Remember<T>(ConfigFile config, string field, string key, T value,
                                string description)
        {
            remembered[field] = config.Bind("custom", key, value, description);
        }

        // --- card costs ------------------------------------------------------

        /// The kinds of card a saved number turns on, one bit each.
        static int Kinds(int index)
        {
            if ((index & Several) != 0) return index & ~Several;
            return index > 0 ? 1 << index : 0;
        }

        /// The number to save for a set of kinds. One kind is saved the game's
        /// own way, so that save still works without the mod.
        static int Index(int kinds)
        {
            for (var kind = FirstKind; kind < KindLabels.Length; kind++)
                if (kinds == 1 << kind) return kind;
            return kinds == 0 ? 0 : Several | kinds;
        }

        static object Settings()
        {
            var data = Patches.Manager("LiveGameDataManager");
            return data == null ? null : Fields.Get(data, "customModeSettings");
        }

        /// With several kinds on, ask the game about each kind in turn.
        public static bool KindCost(object __instance, ref bool __result)
        {
            if (asking) return true;
            var settings = Settings();
            var index = settings == null ? null : Fields.Get<int?>(settings, "typeManaPenaltyIndex");
            if (index == null || (index.Value & Several) == 0) return true;
            var kinds = new List<int>();
            for (var kind = FirstKind; kind < KindLabels.Length; kind++)
                if ((index.Value & 1 << kind) != 0) kinds.Add(kind);
            __result = AnyRule(__instance, settings, "typeManaPenaltyIndex", typePenalty, kinds);
            return false;
        }

        /// All realms asks the game about foreign cards, then in realm cards.
        public static bool RealmCost(object __instance, ref bool __result)
        {
            if (asking) return true;
            var settings = Settings();
            if (settings == null || Fields.Get<int?>(settings, "realmManaPenaltyIndex") != AllRealms)
                return true;
            __result = AnyRule(__instance, settings, "realmManaPenaltyIndex", realmPenalty,
                               new[] { ForeignCards, InRealmCards });
            return false;
        }

        /// Try each choice in turn with the game's own check, then put the
        /// setting back.
        static bool AnyRule(object card, object settings, string field, MethodBase check,
                            IEnumerable<int> choices)
        {
            if (check == null) return false;
            var saved = Fields.Get(settings, field);
            asking = true;
            try
            {
                foreach (var choice in choices)
                {
                    Fields.Set(settings, field, choice);
                    if ((bool)check.Invoke(card, null)) return true;
                }
                return false;
            }
            finally
            {
                Fields.Set(settings, field, saved);
                asking = false;
            }
        }

        /// The game only picks random cards when random cards is the only kind
        /// chosen. If it's one of several, show the game just that kind for
        /// this call.
        public static void Unlocking(object __instance)
        {
            var settings = Fields.Get(__instance, "customModeSettings");
            var index = settings == null ? null : Fields.Get<int?>(settings, "typeManaPenaltyIndex");
            if (index == null || (index.Value & Several) == 0) return;
            if ((index.Value & 1 << RandomCards) == 0) return;
            lentSettings = settings;
            lentIndex = index.Value;
            Fields.Set(settings, "typeManaPenaltyIndex", RandomCards);
        }

        /// A finalizer, so the setting is put back even if the game throws.
        public static void Unlocked()
        {
            if (lentSettings == null) return;
            Fields.Set(lentSettings, "typeManaPenaltyIndex", lentIndex);
            lentSettings = null;
        }

        // --- the screen ------------------------------------------------------

        /// Runs before the screen fills in its rows, so ours get filled too.
        /// The title and pause menus each have their own copy of the screen,
        /// and each gets our rows the first time it opens.
        public static void MenuOpening(Component __instance, object setting, bool interactable)
        {
            // The title menu starts from what was picked last time. The pause
            // menu shows the game being played, so leave that alone.
            if (interactable && setting != null) Load(setting);
            if (__instance.GetComponent<CustomRows>() != null) return;
            var rows = __instance.gameObject.AddComponent<CustomRows>();
            try { AddRows(__instance, rows); }
            catch (Exception e) { log.LogError("no extra custom mode rows: " + e); }
        }

        static void AddRows(Component menu, CustomRows rows)
        {
            var template = Fields.Get<Component>(menu, "rebirthCardSet");
            var health = Fields.Get<Component>(menu, "playerHealth");
            var realm = Fields.Get<Component>(menu, "cardManaPenaltyByRealm");
            var kind = Fields.Get<Component>(menu, "cardManaPenaltyByType");
            var options = Fields.Get<IList>(menu, "allOptions");
            if (template == null || health == null || realm == null || kind == null)
            {
                log.LogWarning("the custom mode screen is not shaped the way we "
                               + "expect — no extra custom mode rows");
                return;
            }
            var list = kind.transform.parent;

            var plugin = Plugin.Instance;
            var energy = Options.AddChoice(list, template, "ENERGY", null, EnergyValues, false,
                                           plugin.PlayerEnergy.Value,
                                           value => plugin.PlayerEnergy.Value = value);
            if (energy != null)
            {
                PlaceAfter(energy, health.gameObject, options);
                rows.Energy = energy.GetComponent(template.GetType());
            }

            var choices = Fields.Get<IList>(realm, "valueTexts");
            var textType = AccessTools.TypeByName("TextData");
            if (choices != null && textType != null && choices.Count == AllRealms)
            {
                var choice = Activator.CreateInstance(choices.GetType().GetGenericArguments()[0]);
                Options.SetLabel(choice, textType, "ALL REALMS");
                choices.Add(choice);
            }

            // A toggle for each kind, where the one row was.
            var after = kind.gameObject;
            for (var i = FirstKind; i < KindLabels.Length; i++)
            {
                var go = UnityEngine.Object.Instantiate(template.gameObject, list);
                go.name = KindLabels[i];
                var row = go.GetComponent(template.GetType());
                Options.Describe(row, "+1 " + KindLabels[i]);
                PlaceAfter(go, after, options);
                after = go;
                rows.Toggles.Add(row);
                rows.Kinds.Add(i);
            }
            kind.gameObject.SetActive(false);
            if (options != null) options.Remove(kind.gameObject);

            if (list.GetComponent<UnityEngine.UI.GridLayoutGroup>() == null)
            {
                var rowSize = (template.transform as RectTransform)?.sizeDelta ?? Vector2.zero;
                Options.TwoColumns(list, rowSize);
            }
        }

        /// Put a row just below another, on screen and in controller order.
        static void PlaceAfter(GameObject row, GameObject above, IList options)
        {
            row.transform.SetSiblingIndex(above.transform.GetSiblingIndex() + 1);
            if (options == null) return;
            var at = options.IndexOf(above);
            options.Insert(at < 0 ? options.Count : at + 1, row);
        }

        /// Show what the settings say. The title menu shows what a new game
        /// gets; the pause menu shows the game being played.
        public static void MenuShown(Component __instance, object setting, bool interactable)
        {
            var rows = __instance.GetComponent<CustomRows>();
            if (rows == null || setting == null) return;
            try
            {
                var kinds = Kinds(Fields.Get<int?>(setting, "typeManaPenaltyIndex") ?? 0);
                for (var i = 0; i < rows.Toggles.Count; i++)
                    Show(rows.Toggles[i], (kinds & 1 << rows.Kinds[i]) != 0);
                if (rows.Energy != null)
                {
                    var plugin = Plugin.Instance;
                    var energy = interactable ? plugin.PlayerEnergy.Value
                                              : Plugin.Rule(plugin.PlayerEnergy);
                    Show(rows.Energy, Options.Nearest(EnergyValues, energy));
                }
            }
            catch (Exception e) { log.LogError("custom mode rows: " + e); }
        }

        static void Show(Component row, object value)
        {
            AccessTools.Method(row.GetType(), "UpdateVisuals", new[] { value.GetType() })
                       ?.Invoke(row, new[] { value });
        }

        /// The game saves the hidden row's single kind. Save our toggles over
        /// it, then remember every choice for the next new game.
        public static void MenuConfirmed(Component __instance)
        {
            var title = AccessTools.TypeByName("TitleMenu");
            var settings = title == null ? null
                : AccessTools.Field(title, "lastSelcetedModeSettings")?.GetValue(null);
            if (settings == null) return;
            var rows = __instance.GetComponent<CustomRows>();
            if (rows != null && rows.Toggles.Count != 0)
            {
                var kinds = 0;
                for (var i = 0; i < rows.Toggles.Count; i++)
                    if ((Fields.Get<int?>(rows.Toggles[i], "valueIndex") ?? 0) != 0)
                        kinds |= 1 << rows.Kinds[i];
                Fields.Set(settings, "typeManaPenaltyIndex", Index(kinds));
            }
            try { Save(settings); }
            catch (Exception e) { log.LogError("could not save custom mode choices: " + e); }
        }

        /// Copy the remembered choices onto the game's settings.
        static void Load(object settings)
        {
            try
            {
                foreach (var pair in remembered)
                    Fields.Set(settings, pair.Key, pair.Value.BoxedValue);
            }
            catch (Exception e) { log.LogError("could not load custom mode choices: " + e); }
        }

        /// Copy the game's settings into the config. Only changed values are
        /// set, since each set writes the file.
        static void Save(object settings)
        {
            foreach (var pair in remembered)
            {
                var value = Fields.Get(settings, pair.Key);
                if (value != null && !value.Equals(pair.Value.BoxedValue))
                    pair.Value.BoxedValue = value;
            }
        }
    }

    /// The rows we added to one custom mode screen.
    public class CustomRows : MonoBehaviour
    {
        public List<Component> Toggles = new List<Component>();
        public List<int> Kinds = new List<int>();
        public Component Energy;
    }
}
