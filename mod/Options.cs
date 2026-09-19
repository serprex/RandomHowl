using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RandomHowl
{
    /// The randomizer screen, built from copies of title menu parts.
    ///
    /// The RANDOMIZER entry is copied from SETTINGS, and its screen is a copy
    /// of the custom mode screen with our rows in it. Copying gets the art,
    /// font, sounds and controller navigation for free.
    public static class Options
    {
        static ManualLogSource log;
        static GameObject screen;
        static Type rowType;
        static MethodInfo doLoadFlow;

        // Config key, then the label on its row.
        static readonly string[] Shuffles =
        {
            "ingredients", "MATERIALS",
            "totems",      "TOTEMS",
            "nests",       "NESTS",
        };

        // The choices on the rows that are more than a yes/no. A null label
        // shows the number itself, which is how the game writes its own.
        static readonly string[] CaveLabels = { "OFF", "ON", "DECOUPLED" };
        static readonly int[] CaveValues = { 0, 1, 2 };

        static readonly string[] SpiritLabels = { "OFF", "ON", "RESTRICTED" };
        static readonly int[] SpiritValues = { 0, 1, 2 };

        static readonly string[] GrantLabels = { "OFF", "ON", "EVERYTHING" };
        static readonly int[] GrantValues = { 0, 1, 2 };

        // Only the first entry is named; the rest draw as their own number.
        static readonly string[] ElderPercentLabels = { "VANILLA" };
        static readonly int[] ElderPercentValues =
            { -1, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

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

            var gameplay = AccessTools.TypeByName("ControlsSettingsMenu");
            var tabShown = gameplay == null ? null : AccessTools.Method(gameplay, "OnEnable");
            if (tabShown == null)
                log.LogWarning("no ControlsSettingsMenu.OnEnable — skip logos, skip "
                               + "intro and auto text are only in the config file");
            else
                harmony.Patch(tabShown, null, new HarmonyMethod(
                    typeof(Options).GetMethod(nameof(GameplayShown))));

            // Every run goes through DoLoadFlow. A run started before the world
            // scan finishes would load its first scenes unpatched, so hold it
            // here.
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

        /// DoLoadFlow is a coroutine, so the prefix can't just block, which
        /// would stall the scan too. Return an enumerator that waits for Ready,
        /// then runs the game's own.
        public static bool HoldLoadFlow(object __instance, ref IEnumerator __result)
        {
            if (Plugin.Instance.Ready)
            {
                // The save slot is picked by now, so shuffle from its settings.
                Plugin.Instance.OpenProfile();
                return true;
            }
            var blocker = Fields.Get<GameObject>(__instance, "blocker");
            if (blocker != null) blocker.SetActive(true);
            __result = WaitForWorld(__instance);
            return false;
        }

        static IEnumerator WaitForWorld(object title)
        {
            while (!Plugin.Instance.Ready) yield return null;
            yield return doLoadFlow.Invoke(title, null);
        }

        /// Runs each time the title screen loads, since the old copy is gone by
        /// then.
        public static void TitleShown(object __instance)
        {
            if (screen != null) return;
            try { Build((Component)__instance); }
            catch (Exception e) { log.LogError("no randomizer screen: " + e); }
        }

        static void Build(Component title)
        {
            var entry = Fields.Get<GameObject>(title, "settingsEntry");
            var custom = AccessTools.TypeByName("CustomModeSelectionMenu");
            var source = custom == null ? null : title.GetComponentInChildren(custom, true);
            if (entry == null || source == null)
            {
                log.LogWarning("the title menu is not shaped the way we expect — "
                               + "no randomizer screen; edit the config file instead");
                return;
            }
            // After the copy, so the copy doesn't get this row too.
            var built = BuildScreen(source);
            try { AddCustomRow(source); }
            catch (Exception e) { log.LogError("no RANDOMIZER row on custom mode: " + e); }
            UnlockCustomMode();
            if (!built) return;
            KeepSelection(title);
            AddEntry(entry);
            log.LogInfo("randomizer screen added to the title menu");
        }

        // --- the custom mode screen -----------------------------------------

        /// Only custom mode games are randomized, so the custom mode screen
        /// gets a RANDOMIZER row at the top.
        static void AddCustomRow(Component menu)
        {
            var panel = Fields.Get<CanvasGroup>(menu, "optionaPanel");
            var template = Fields.Get<Component>(menu, "rebirthCardSet");
            if (panel == null || template == null)
            {
                log.LogWarning("the custom mode screen is not shaped the way we "
                               + "expect — no RANDOMIZER row; custom games use "
                               + "enabled from the config file");
                return;
            }
            var rowSize = (template.transform as RectTransform)?.sizeDelta ?? Vector2.zero;
            var row = AddToggle(panel.transform, template, "RANDOMIZER", Plugin.Instance.Enabled);
            row.transform.SetAsFirstSibling();
            // Controller navigation goes down this list.
            var options = Fields.Get<IList>(menu, "allOptions");
            if (options != null) options.Insert(0, row);
            // Ten rows don't fit in the game's one column.
            TwoColumns(panel.transform, rowSize);
        }

        /// Custom mode is locked until rebirth mode is beaten. It's the only
        /// way into a randomized game, so unlock it.
        static void UnlockCustomMode()
        {
            var type = AccessTools.TypeByName("PersistantDataManager");
            var settings = type == null ? null
                : AccessTools.Method(type, "GetSettings")?.Invoke(null, null);
            if (settings == null || !Fields.Set(settings, "customModeUnlocked", true))
                log.LogWarning("could not unlock custom mode, so a game can only be "
                               + "randomized once rebirth mode is beaten");
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

            // Only the click changes. Hover still points at the real title
            // menu, which plays the sound.
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
            var panel = Fields.Get<CanvasGroup>(menu, "optionaPanel");
            var heading = Fields.Get<Text>(menu, "title");
            var done = Fields.Get<Component>(menu, "doneButton");
            var template = Fields.Get<Component>(menu, "rebirthCardSet");
            // Right away, not at end of frame, or its Start takes the done
            // button back.
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

            // Copy rows while the template is untouched, then turn the template
            // into the seed row at the top. Rows fill two columns in this
            // order: shuffles on the left, spirits and extras on the right.
            var plugin = Plugin.Instance;
            for (var i = 0; i < Shuffles.Length; i += 2)
                AddToggle(list, template, Shuffles[i + 1], plugin.Shuffles[Shuffles[i]]);
            AddChoice(list, template, "CAVES", CaveLabels, CaveValues, false,
                      (int)plugin.EntranceMode.Value,
                      value => plugin.EntranceMode.Value = (EntranceShuffle)value);
            AddToggle(list, template, "CARDS", plugin.Shuffles["card_realms"]);
            AddToggle(list, template, "RECIPES", plugin.Shuffles["recipes"]);
            AddChoice(list, template, "CARD GIFTS", GrantLabels, GrantValues, false,
                      (int)plugin.GrantMode.Value,
                      value => plugin.GrantMode.Value = (GrantShuffle)value);
            AddChoice(list, template, "SPIRITS", SpiritLabels, SpiritValues, false,
                      (int)plugin.SpiritMode.Value,
                      value => plugin.SpiritMode.Value = (SpiritShuffle)value);
            AddToggle(list, template, "CHIMERAS", plugin.Shuffles["chimeras"]);
            AddChoice(list, template, "ELDER%", ElderPercentLabels,
                      ElderPercentValues, true, plugin.ElderPercent.Value,
                      value => plugin.ElderPercent.Value = value);
            AddToggle(list, template, "SCARCE HOWLS", plugin.Scarce);
            AddToggle(list, template, "CARDS UNLOCKED", plugin.RevealCards);
            AddToggle(list, template, "TEAR HEALTH", plugin.TearHealth);
            AddToggle(list, template, "OPEN WORLD", plugin.OpenWorld);
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

        /// The game's one column only fits nine rows.
        internal static void TwoColumns(Transform list, Vector2 rowSize)
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

        static GameObject AddToggle(Transform list, Component template, string label,
                                    ConfigEntry<bool> config)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, list);
            go.name = label;
            var row = go.GetComponent(template.GetType());
            Describe(row, label);
            var entry = go.AddComponent<OptionRow>();
            entry.Config = config;
            Wire(row, typeof(bool), config.Value, entry, "Changed");
            return go;
        }

        /// A row that cycles through a list of values instead of on/off. The
        /// custom mode screen already has rows like this (enemy health in eight
        /// steps), so we only hand it the list.
        internal static GameObject AddChoice(Transform list, Component template, string label,
                                             string[] labels, int[] values, bool percent,
                                             int current, Action<int> chosen)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, list);
            go.name = label;
            var row = go.GetComponent(template.GetType());
            Describe(row, label);
            if (!SetChoices(row, labels, values, percent))
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            var entry = go.AddComponent<ValueRow>();
            entry.Values = values;
            entry.Chosen = chosen;
            Wire(row, typeof(int), Nearest(values, current), entry, "Changed");
            return go;
        }

        /// Replace the row's on/off pair with our choices. An entry without its
        /// own text draws its number, so only labelled entries need a TextData.
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
                    SetLabel(choice, textType, labels[i]);
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

        /// Give one of a row's choices our own text.
        internal static void SetLabel(object choice, Type textType, string label)
        {
            var text = ScriptableObject.CreateInstance(textType);
            Fields.Set(text, "text", label);
            Fields.Set(choice, "text", text);
        }

        /// Which entry a config value shows as. The config file may hold a
        /// number that isn't one of ours, so take the closest.
        internal static int Nearest(int[] values, int current)
        {
            var best = 0;
            for (var i = 1; i < values.Length; i++)
                if (Math.Abs(values[i] - current) < Math.Abs(values[best] - current)) best = i;
            return best;
        }

        /// The seed is text, so this row swaps its selector for a text field
        /// over the same label. Leaving it empty rolls a random seed on the
        /// next edit.
        static void MakeSeedRow(Component row)
        {
            var go = row.gameObject;
            go.name = "Seed";
            Describe(row, "SEED");
            var display = Fields.Get<Text>(row, "valueTextDisplay");
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

        // --- settings > gameplay --------------------------------------------

        /// Skip logos, skip intro, auto text, cheats and faster ro aren't about
        /// the shuffle, so they sit with the game's own options. The tab turns
        /// on each time it opens: add the rows the first time, then show the
        /// current values.
        public static void GameplayShown(Component __instance)
        {
            try
            {
                if (__instance.GetComponent<GameplayRows>() == null)
                {
                    __instance.gameObject.AddComponent<GameplayRows>();
                    AddGameplayRows(__instance);
                }
                var on = Patches.Translate(__instance, "onText") ?? "ON";
                var off = Patches.Translate(__instance, "offText") ?? "OFF";
                foreach (var row in __instance.GetComponentsInChildren<SettingRow>(true))
                    row.Show(on, off);
            }
            catch (Exception e) { log.LogError("no extras on the gameplay tab: " + e); }
        }

        static void AddGameplayRows(Component menu)
        {
            // This list is what a controller moves down.
            var handlerType = AccessTools.TypeByName("AudioSettingsMenuControlsHandler");
            var handler = handlerType == null ? null : menu.GetComponent(handlerType);
            var items = handler == null ? null : Fields.Get<List<GameObject>>(handler, "items");
            if (items == null || items.Count < 2)
            {
                log.LogWarning("the gameplay tab is not shaped the way we expect — "
                               + "skip logos, skip intro, auto text, cheats and faster ro are only in "
                               + "the config file");
                return;
            }
            // Each new row goes as far below the last as the last is below the
            // one before it.
            var step = Position(items[items.Count - 1]) - Position(items[items.Count - 2]);
            // Copy the game's own row each time. A copy of one of ours would
            // bring along its SettingRow, which has no config and breaks Show.
            var template = items[items.Count - 1];
            var plugin = Plugin.Instance;
            AddSettingRow(items, template, step, "SKIP LOGOS", plugin.SkipLogos);
            AddSettingRow(items, template, step, "SKIP INTRO", plugin.SkipIntro);
            AddSettingRow(items, template, step, "AUTO TEXT", plugin.AutoText);
            AddSettingRow(items, template, step, "CHEATS", plugin.Cheats);
            AddSettingRow(items, template, step, "FASTER RO", plugin.FasterRo);
            Columns(items, step);
        }

        /// Too many rows for one column, so split them in two, side by side.
        /// The list order stays the same, so a controller goes down the left
        /// column and then on down the right one.
        static void Columns(List<GameObject> items, Vector2 step)
        {
            var first = items[0].transform as RectTransform;
            if (first == null) return;
            var top = first.anchoredPosition;
            var width = first.rect.width;
            var half = (items.Count + 1) / 2;
            for (int i = 0; i < items.Count; i++)
            {
                var rect = items[i].transform as RectTransform;
                if (rect == null) continue;
                var right = i >= half;
                var x = top.x + (right ? width / 2 : -width / 2);
                rect.anchoredPosition = new Vector2(x, top.y) + step * (right ? i - half : i);
            }
        }

        static Vector2 Position(GameObject go)
        {
            var rect = go.transform as RectTransform;
            return rect == null ? Vector2.zero : rect.anchoredPosition;
        }

        /// Copy a game row, a toggle with its name and an ON/OFF text, and
        /// put it below the last row.
        static void AddSettingRow(List<GameObject> items, GameObject template, Vector2 step,
                                  string label, ConfigEntry<bool> config)
        {
            var last = items[items.Count - 1];
            var go = UnityEngine.Object.Instantiate(template, last.transform.parent);
            go.name = label;
            go.transform.SetSiblingIndex(last.transform.GetSiblingIndex() + 1);
            var rect = go.transform as RectTransform;
            if (rect != null) rect.anchoredPosition = Position(last) + step;

            var toggle = go.GetComponent<Toggle>();
            if (toggle == null)
            {
                log.LogWarning("gameplay tab rows are not toggles — no " + label + " row");
                UnityEngine.Object.Destroy(go);
                return;
            }
            // A new event drops the copied call to the game's own setting.
            toggle.onValueChanged = new Toggle.ToggleEvent();
            items.Add(go);

            var row = go.AddComponent<SettingRow>();
            row.Config = config;
            foreach (var text in go.GetComponentsInChildren<Text>(true))
            {
                if (text.name == "OnState")
                {
                    row.Value = text;
                    continue;
                }
                Unlocalize(text.gameObject);
                text.text = label;
            }
            toggle.isOn = config.Value;
            toggle.onValueChanged.AddListener(row.Changed);
        }

        /// The game's toggles play this click themselves, so ours do too.
        internal static void Click()
        {
            var audio = Patches.Manager("AudioHandler");
            var sounds = audio == null ? null : Fields.Get(audio, "sounds");
            var sound = sounds == null ? null : Fields.Get(sounds, "buttonClicked1");
            var play = sound == null ? null
                : AccessTools.Method(sound.GetType(), "PlayByAudioHandler", Type.EmptyTypes);
            if (play != null) play.Invoke(sound, null);
        }

        // --- odds and ends ---------------------------------------------------

        /// The title menu clears the UI selection every frame unless one of its
        /// known screens is up, so add ours to that list. Otherwise the seed
        /// field loses focus right after a click and only one key press gets
        /// through.
        static void KeepSelection(Component title)
        {
            var screens = Fields.Get<IList>(title, "allScreensThatCanBlock");
            if (screens == null)
            {
                log.LogWarning("no TitleMenu.allScreensThatCanBlock — the seed "
                               + "field will not hold focus; set the seed in the "
                               + "config file instead");
                return;
            }
            screens.Add(screen);
        }

        /// Save only. A run shuffles from its save's settings when it starts,
        /// so shuffling on every change here would just stutter the menu.
        internal static void Apply()
        {
            var plugin = Plugin.Instance;
            plugin.Config.Save();
            // The screen edits what new games get, not the last save played.
            plugin.CloseProfile();
        }

        internal static void Describe(Component row, string text)
        {
            var label = Fields.Get<Text>(row, "descriptionDisplay");
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

        /// OptionsSelector.Setup has int and bool versions and takes whatever
        /// delegate type the game declared. Find the right one and pass it a
        /// method on our component.
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

    /// One row with a list of values. The game's selector passes back the
    /// clicked entry, and we know which value that is.
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

    /// Marks a gameplay tab that already has our rows.
    public class GameplayRows : MonoBehaviour { }

    /// One yes/no row on the gameplay tab.
    public class SettingRow : MonoBehaviour
    {
        public ConfigEntry<bool> Config;
        public Text Value;
        string on = "ON";
        string off = "OFF";

        /// Each time the tab opens, since the title and pause menus each have
        /// a copy, and the language may have changed.
        public void Show(string onText, string offText)
        {
            on = onText;
            off = offText;
            var toggle = GetComponent<Toggle>();
            if (toggle != null) toggle.isOn = Config.Value;
            Label();
        }

        public void Changed(bool isOn)
        {
            if (Config == null || Config.Value == isOn) return;
            Config.Value = isOn;
            // Not Options.Apply: in a run that would drop the save's settings.
            Plugin.Instance.Config.Save();
            Label();
            Options.Click();
        }

        void Label()
        {
            if (Value != null) Value.text = Config.Value ? on : off;
        }
    }

    /// The randomizer screen. Changes save as they're made, so closing is all
    /// that's left.
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
