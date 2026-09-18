// Just the parts of UnityEngine.UI the mod uses, copied from the game's DLL
// (Unity 2022.3.62). Names, base classes, property types and enum values must
// match the real thing, or the mod fails to load them in game. When the mod
// starts using something new from UnityEngine.UI, add it here.

using UnityEngine.Events;

namespace UnityEngine.EventSystems
{
    public abstract class UIBehaviour : MonoBehaviour { }

    public class EventSystem : UIBehaviour
    {
        public static EventSystem current { get => throw null; set => throw null; }
        public void SetSelectedGameObject(GameObject selected) => throw null;
    }

    public class EventTrigger : MonoBehaviour { }
}

namespace UnityEngine.UI
{
    using UnityEngine.EventSystems;

    public abstract class Graphic : UIBehaviour
    {
        public virtual Color color { get => throw null; set => throw null; }
    }

    public abstract class MaskableGraphic : Graphic { }

    public class Text : MaskableGraphic
    {
        public virtual string text { get => throw null; set => throw null; }
        public bool supportRichText { get => throw null; set => throw null; }
    }

    public class Image : MaskableGraphic { }

    public class Selectable : UIBehaviour
    {
        public Graphic targetGraphic { get => throw null; set => throw null; }
    }

    public class Button : Selectable
    {
        public class ButtonClickedEvent : UnityEvent { }

        public ButtonClickedEvent onClick { get => throw null; set => throw null; }
    }

    public class Toggle : Selectable
    {
        public class ToggleEvent : UnityEvent<bool> { }

        // A field in the real thing, not a property.
        public ToggleEvent onValueChanged;
        public bool isOn { get => throw null; set => throw null; }
    }

    public class InputField : Selectable
    {
        public class EndEditEvent : UnityEvent<string> { }

        public enum LineType { SingleLine = 0, MultiLineSubmit = 1, MultiLineNewline = 2 }

        public string text { get => throw null; set => throw null; }
        public bool isFocused => throw null;
        public Text textComponent { get => throw null; set => throw null; }
        public EndEditEvent onEndEdit { get => throw null; set => throw null; }
        public int characterLimit { get => throw null; set => throw null; }
        public LineType lineType { get => throw null; set => throw null; }
        public void DeactivateInputField() => throw null;
    }

    public abstract class LayoutGroup : UIBehaviour
    {
        public TextAnchor childAlignment { get => throw null; set => throw null; }
    }

    public abstract class HorizontalOrVerticalLayoutGroup : LayoutGroup
    {
        public float spacing { get => throw null; set => throw null; }
    }

    public class VerticalLayoutGroup : HorizontalOrVerticalLayoutGroup { }

    public class GridLayoutGroup : LayoutGroup
    {
        public enum Axis { Horizontal = 0, Vertical = 1 }
        public enum Constraint { Flexible = 0, FixedColumnCount = 1, FixedRowCount = 2 }

        public Axis startAxis { get => throw null; set => throw null; }
        public Vector2 cellSize { get => throw null; set => throw null; }
        public Vector2 spacing { get => throw null; set => throw null; }
        public Constraint constraint { get => throw null; set => throw null; }
        public int constraintCount { get => throw null; set => throw null; }
    }
}
