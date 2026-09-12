using System;
using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RandomHowl
{
    /// The randomizer screen, built out of parts of the title menu.
    ///
    /// A RANDOMIZER entry is copied from SETTINGS, and the screen behind it is
    /// a copy of the custom mode screen with our own rows in it. Copying means
    /// the art, the font, the sounds and the controller navigation all come
    /// for free, and there is no hand-drawn UI to go stale.
    public static class Options
    {
        static ManualLogSource log;
        static GameObject screen;
        static Type rowType;
        static MethodInfo doLoadFlow;

        // Config key, then the label on its row.
        static readonly string[] Shuffles =
        {
            "ingredients", "INGREDIENTS",
            "totems",      "TOTEMS",
            "entrances",   "CAVES",
        };

        // The choices on the rows that are more than a yes/no. A null label
        // shows the number itself, which is how the game writes its own.
        static readonly string[] EnemyLabels = { "OFF", "ON", "RESTRICTED" };
        static readonly int[] EnemyValues = { 0, 1, 2 };

        static readonly string[] GrantLabels = { "OFF", "ON", "EVERYTHING" };
        static readonly int[] GrantValues = { 0, 1, 2 };

        // Only the first entry is named; the rest draw as their own number.
        static readonly string[] ElitePercentLabels = { "VANILLA" };
        static readonly int[] ElitePercentValues =
            { -1, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        static readonly int[] EnergyValues = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        public static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            var type = AccessTools.TypeByName("TitleMenu");
            var start = type == null ? null : AccessTools.Method(type, "Start");
            if (start == null)
                log.LogWarning("no TitleMenu.Start — the randomizer screen is off; "
                               + "edit the config file instead");
            else
                harmony.Patch(start, null, new HarmonyMethod(
                    typeof(Options).GetMethod(nameof(TitleShown))));

            // Every run funnels through DoLoadFlow. The world scan is still
            // running behind the title screen, and a run started before it
            // lands loads its first scenes unpatched, so hold the flow here.
            doLoadFlow = type == null ? null : AccessTools.Method(type, "DoLoadFlow");
            if (doLoadFlow == null)
            {
                log.LogWarning("no TitleMenu.DoLoadFlow — a run can start before "
                               + "the world scan finishes");
                return;
            }
            harmony.Patch(doLoadFlow, new HarmonyMethod(
                typeof(Options).GetMethod(nameof(HoldLoadFlow))));
        }

        /// DoLoadFlow is a coroutine, so a prefix can't simply block — spinning
        /// on the thread would stall the scan that has to finish first. Hand
        /// back an enumerator that idles until Ready, then runs the game's own.
        public static bool HoldLoadFlow(object __instance, ref IEnumerator __result)
        {
            if (Plugin.Instance.Ready) return true;
            var blocker = Fields.Get(__instance, "blocker") as GameObject;
            if (blocker != null) blocker.SetActive(true);
            __result = WaitForWorld(__instance);
            return false;
        }

        static IEnumerator WaitForWorld(object title)
        {
            while (!Plugin.Instance.Ready) yield return null;
            yield return doLoadFlow.Invoke(title, null);
        }

        /// Runs every time the title screen loads, so the old copy is gone.
        public static void TitleShown(object __instance)
        {
            if (screen != null) return;
            try { Build((Component)__instance); }
            catch (Exception e) { log.LogError("no randomizer screen: " + e); }
        }

        static void Build(Component title)
        {
            var entry = Fields.Get(title, "settingsEntry") as GameObject;
            var custom = AccessTools.TypeByName("CustomModeSelectionMenu");
            var source = custom == null ? null : title.GetComponentInChildren(custom, true);
            if (entry == null || source == null)
            {
                log.LogWarning("the title menu is not shaped the way we expect — "
                               + "no randomizer screen; edit the config file instead");
                return;
            }
            if (!BuildScreen(source)) return;
            KeepSelection(title);
            AddEntry(entry);
            log.LogInfo("randomizer screen added to the title menu");
        }

        // --- the title menu entry -------------------------------------------

        static void AddEntry(GameObject settings)
        {
            var entry = UnityEngine.Object.Instantiate(settings, settings.transform.parent);
            entry.name = "Randomizer";
            entry.transform.SetSiblingIndex(settings.transform.GetSiblingIndex() + 1);
            entry.SetActive(true);

            Unlocalize(entry);
            var label = entry.GetComponent<Text>();
            if (label != null) label.text = "RANDOMIZER";

            // Only the click changes. The hover trigger still points at the
            // real title menu, which is where the sound comes from.
            var button = entry.GetComponent<Button>();
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(Open);
            }
        }

        static void Open()
        {
            if (screen == null) return;
            screen.SetActive(true);
            var first = screen.GetComponentInChildren(rowType, false) as Component;
            if (EventSystem.current != null && first != null)
                EventSystem.current.SetSelectedGameObject(first.gameObject);
        }

        // --- the screen -----------------------------------------------------

        static bool BuildScreen(Component source)
        {
            var clone = UnityEngine.Object.Instantiate(source.gameObject,
                                                       source.transform.parent);
            clone.name = "RandomizerMenu";
            clone.SetActive(false);

            var menu = clone.GetComponent(source.GetType());
            var panel = Fields.Get(menu, "optionaPanel") as CanvasGroup;
            var heading = Fields.Get(menu, "title") as Text;
            var done = Fields.Get(menu, "doneButton") as Component;
            var template = Fields.Get(menu, "rebirthCardSet") as Component;
            // Straight away, not at the end of the frame: its Start would take
            // the done button back off us.
            UnityEngine.Object.DestroyImmediate(menu);

            if (panel == null || done == null || template == null)
            {
                log.LogWarning("the custom mode screen is not shaped the way we "
                               + "expect — no randomizer screen");
                UnityEngine.Object.Destroy(clone);
                return false;
            }

            rowType = template.GetType();
            panel.interactable = true;
            panel.blocksRaycasts = true;
            if (heading != null)
            {
                Unlocalize(heading.gameObject);
                heading.text = "RANDOMIZER";
            }

            // Everything but the one row we copy from.
            var list = panel.transform;
            for (var i = list.childCount - 1; i >= 0; i--)
            {
                var child = list.GetChild(i).gameObject;
                if (child != template.gameObject)
                    UnityEngine.Object.DestroyImmediate(child);
            }

            // Copies first, while the template is still untouched, then the
            // template itself becomes the seed row at the top. The order here
            // is the order down the two columns: what gets shuffled on the
            // left, enemies and the extras on the right.
            var plugin = Plugin.Instance;
            for (var i = 0; i < Shuffles.Length; i += 2)
                AddToggle(list, template, Shuffles[i + 1], plugin.Shuffles[Shuffles[i]]);
            AddToggle(list, template, "CARDS", plugin.Shuffles["card_realms"]);
            AddToggle(list, template, "RECIPES", plugin.Shuffles["recipes"]);
            AddChoice(list, template, "CARD GIFTS", GrantLabels, GrantValues, false,
                      (int)plugin.GrantMode.Value,
                      value => plugin.GrantMode.Value = (GrantShuffle)value);
            AddChoice(list, template, "ENEMIES", EnemyLabels, EnemyValues, false,
                      (int)plugin.EnemyMode.Value,
                      value => plugin.EnemyMode.Value = (EnemyShuffle)value);
            AddChoice(list, template, "ELITE%", ElitePercentLabels,
                      ElitePercentValues, true, plugin.ElitePercent.Value,
                      value => plugin.ElitePercent.Value = value);
            AddChoice(list, template, "ENERGY", null, EnergyValues, false,
                      plugin.PlayerEnergy.Value,
                      value => plugin.PlayerEnergy.Value = value);
            AddToggle(list, template, "SCARCE HOWLS", plugin.Scarce);
            AddToggle(list, template, "CARDS UNLOCKED", plugin.RevealCards);
            AddToggle(list, template, "SKIP LOGOS", plugin.SkipLogos);
            AddToggle(list, template, "SKIP INTRO", plugin.SkipIntro);
            AddToggle(list, template, "SPOILER LOG", plugin.SpoilerLog);
            var rowSize = (template.transform as RectTransform)?.sizeDelta ?? Vector2.zero;
            MakeSeedRow(template);
            TwoColumns(list, rowSize);

            var close = clone.AddComponent<RandomizerScreen>();
            Wire(done, typeof(int), 0, close, "Done");
            foreach (var button in clone.GetComponentsInChildren<Button>(true))
            {
                // Rows already do their own thing when clicked. Everything
                // else on the screen closes it.
                if (button.GetComponent(rowType) != null) continue;
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(close.Close);
            }

            screen = clone;
            return true;
        }

        /// Game fits nine rows in one column
        static void TwoColumns(Transform list, Vector2 rowSize)
        {
            if (rowSize.x <= 0f || rowSize.y <= 0f) return;
            var column = list.GetComponent<VerticalLayoutGroup>();
            var align = column == null ? TextAnchor.UpperCenter : column.childAlignment;
            var spacing = column == null ? 0f : column.spacing;
            if (column != null) UnityEngine.Object.DestroyImmediate(column);

            var grid = list.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = rowSize;
            grid.spacing = new Vector2(20f, spacing);
            grid.childAlignment = align;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 2;
            grid.startAxis = GridLayoutGroup.Axis.Vertical;
        }

        static void AddToggle(Transform list, Component template, string label,
                              ConfigEntry<bool> config)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, list);
            go.name = label;
            var row = go.GetComponent(rowType);
            Describe(row, label);
            var entry = go.AddComponent<OptionRow>();
            entry.Config = config;
            Wire(row, typeof(bool), config.Value, entry, "Changed");
        }

        /// A row that clicks through a list of values instead of on and off.
        /// The game's own custom mode screen has rows like this — its enemy
        /// health one is a percentage in eight steps — so the row already
        /// knows how to draw them, and all we hand it is the list.
        static void AddChoice(Transform list, Component template, string label,
                              string[] labels, int[] values, bool percent,
                              int current, Action<int> chosen)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, list);
            go.name = label;
            var row = go.GetComponent(rowType);
            Describe(row, label);
            if (!SetChoices(row, labels, values, percent))
            {
                UnityEngine.Object.Destroy(go);
                return;
            }
            var entry = go.AddComponent<ValueRow>();
            entry.Values = values;
            entry.Chosen = chosen;
            Wire(row, typeof(int), Nearest(values, current), entry, "Changed");
        }

        /// Replace the row's on/off pair with our own choices. An entry with no
        /// text of its own draws as the number it carries, so only the labelled
        /// ones need a TextData made for them.
        static bool SetChoices(Component row, string[] labels, int[] values, bool percent)
        {
            var field = Fields.Of(row, "valueTexts");
            if (field == null || !field.FieldType.IsGenericType)
            {
                log.LogWarning("no OptionsSelector.valueTexts — no " + row.name + " row");
                return false;
            }
            var choiceType = field.FieldType.GetGenericArguments()[0];
            var optionalType = AccessTools.Field(choiceType, "customValue");
            var textType = AccessTools.TypeByName("TextData");
            if (optionalType == null || textType == null)
            {
                log.LogWarning("the options row is not shaped the way we expect — no "
                               + row.name + " row");
                return false;
            }

            var choices = (IList)Activator.CreateInstance(field.FieldType);
            for (var i = 0; i < values.Length; i++)
            {
                var choice = Activator.CreateInstance(choiceType);
                if (labels != null && i < labels.Length && labels[i] != null)
                {
                    var text = ScriptableObject.CreateInstance(textType);
                    Fields.Set(text, "text", labels[i]);
                    Fields.Set(choice, "text", text);
                }
                else
                {
                    // isSet is what tells the row to draw the number.
                    var optional = Activator.CreateInstance(optionalType.FieldType);
                    Fields.Set(optional, "isSet", true);
                    Fields.Set(optional, "value", values[i]);
                    optionalType.SetValue(choice, optional);
                }
                choices.Add(choice);
            }
            field.SetValue(row, choices);
            Fields.Set(row, "percent", percent);
            return true;
        }

        /// Which entry a config value shows as. Someone may have typed a number
        /// into the config file that isn't one of ours, so take the closest.
        static int Nearest(int[] values, int current)
        {
            var best = 0;
            for (var i = 1; i < values.Length; i++)
                if (Math.Abs(values[i] - current) < Math.Abs(values[best] - current)) best = i;
            return best;
        }

        /// The seed is text, not a yes/no, so this row loses the selector and
        /// gets a text field over the same label. Leave it empty and the next
        /// edit rolls a random one.
        static void MakeSeedRow(Component row)
        {
            var go = row.gameObject;
            go.name = "Seed";
            Describe(row, "SEED");
            var display = Fields.Get(row, "valueTextDisplay") as Text;
            var background = go.GetComponent<Image>();
            UnityEngine.Object.DestroyImmediate(row);
            foreach (var trigger in go.GetComponents<EventTrigger>())
                UnityEngine.Object.DestroyImmediate(trigger);
            // A row's own button would fight the text field for the click.
            foreach (var button in go.GetComponents<Button>())
                UnityEngine.Object.DestroyImmediate(button);

            if (display == null) return;
            display.supportRichText = false;
            var field = go.AddComponent<InputField>();
            field.textComponent = display;
            field.targetGraphic = background;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 32;
            field.text = Plugin.Instance.Seed.Value;
            field.onEndEdit.AddListener(SeedTyped);
        }

        static void SeedTyped(string typed)
        {
            var seed = (typed ?? "").Trim();
            if (seed.Length == 0) seed = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (seed == Plugin.Instance.Seed.Value) return;
            Plugin.Instance.Seed.Value = seed;
            Apply();
        }

        // --- odds and ends ---------------------------------------------------

        /// The title menu drops the UI selection every frame while none of the
        /// screens it knows about are up, so add ours to that list. Without
        /// this the seed field is unselected the frame after you click it and
        /// only one key press gets through.
        static void KeepSelection(Component title)
        {
            var screens = Fields.Get(title, "allScreensThatCanBlock") as IList;
            if (screens == null)
            {
                log.LogWarning("no TitleMenu.allScreensThatCanBlock — the seed "
                               + "field will not hold focus; set the seed in the "
                               + "config file instead");
                return;
            }
            screens.Add(screen);
        }

        /// Save, then redo the shuffle if there is a world to shuffle yet. If
        /// the scan is still running it will pick these values up on its own.
        internal static void Apply()
        {
            var plugin = Plugin.Instance;
            plugin.Config.Save();
            if (!plugin.Ready) return;
            plugin.Rebuild();
            Patches.SetPlayerEnergy();
        }

        static void Describe(Component row, string text)
        {
            var label = Fields.Get(row, "descriptionDisplay") as Text;
            if (label == null) return;
            Unlocalize(label.gameObject);
            label.text = text;
        }

        /// Drop the game's translator off a label we are about to write.
        static void Unlocalize(GameObject go)
        {
            foreach (var comp in go.GetComponents<Component>())
                if (comp != null && comp.GetType().Name == "LocalizeTextField")
                    UnityEngine.Object.DestroyImmediate(comp);
        }

        /// OptionsSelector.Setup comes in an int and a bool flavour, and takes
        /// whatever delegate type the game declared. Find the right one and
        /// hand it a method off our own component.
        static void Wire(Component row, Type valueType, object value,
                         Component target, string method)
        {
            MethodInfo setup = null;
            foreach (var candidate in row.GetType().GetMethods(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (candidate.Name != "Setup") continue;
                var args = candidate.GetParameters();
                if (args.Length == 2 && args[0].ParameterType == valueType)
                {
                    setup = candidate;
                    break;
                }
            }
            if (setup == null)
            {
                log.LogWarning("no OptionsSelector.Setup taking a " + valueType.Name
                               + " — that row will not do anything");
                return;
            }
            var callback = target.GetType().GetMethod(method)
                                 .CreateDelegate(setup.GetParameters()[1].ParameterType, target);
            setup.Invoke(row, new object[] { value, callback });
        }
    }

    /// One yes/no row. The game's selector flips the value and calls this.
    public class OptionRow : MonoBehaviour
    {
        public ConfigEntry<bool> Config;

        public void Changed(bool on)
        {
            if (Config == null || Config.Value == on) return;
            Config.Value = on;
            Options.Apply();
        }
    }

    /// One row with a list of values on it. The game's selector hands back the
    /// entry that was clicked to; which value that is, is ours to know.
    public class ValueRow : MonoBehaviour
    {
        public int[] Values;
        public Action<int> Chosen;

        public void Changed(int index)
        {
            if (Values == null || Chosen == null) return;
            if (index < 0 || index >= Values.Length) return;
            Chosen(Values[index]);
            Options.Apply();
        }
    }

    /// The randomizer screen itself. Everything is already saved by the time
    /// you get here, so closing is all there is to do.
    public class RandomizerScreen : MonoBehaviour
    {
        public void Done(int index) { Close(); }

        public void Close() { gameObject.SetActive(false); }

        void Update()
        {
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            // Escape out of the seed field first, the screen second.
            var seed = GetComponentInChildren<InputField>(true);
            if (seed != null && seed.isFocused) seed.DeactivateInputField();
            else Close();
        }
    }
}
