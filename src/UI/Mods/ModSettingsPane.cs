using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Mods;

// The settings of the mod picked in the Mods window, beside the list: the mod's name on a header plate, then its
// settings as the game's own option rows under the names of their sections, in a copy of the save list's own scroll
// view; below them, what the setting under the pointer or at a gamepad's focus is for, and its default. A mod without
// settings says so in the pane's middle.
internal sealed class ModSettingsPane
{
    // The room between the plate and the rows, and the description's height below them: three lines and their margins.
    private const float PlateGap = 4f, DescriptionHeight = 40f;
    // The native rows' spacing and width; the rows stand, as drawn, in the pane's middle.
    private const float RowGap = 6f, RowWidth = 330f, SectionHeight = 16f, Edge = 6f;
    private static readonly Color Dim = new Color(0.588f, 0.553f, 0.533f);
    private readonly TMP_Text name;
    private readonly RectTransform rows;
    private readonly ScrollRect scroll;
    private readonly TMP_Text description;
    private readonly TMP_Text notice;
    private readonly UISwitchButton switched;
    private readonly UISlider slid;
    private readonly TMP_InputField typed;
    private readonly TMP_Text section;
    private readonly int group;
    private readonly List<GamepadNavigationItem> items = new List<GamepadNavigationItem>();
    // The rows shown, and every value each slider among them can show.
    private readonly List<RectTransform> shown = new List<RectTransform>();
    private readonly Dictionary<RectTransform, string[]> slidValues = new Dictionary<RectTransform, string[]>();

    // Takes the pane and its width, a header plate, the scroll view the rows go in, and the native row, slider, text
    // field, heading and line the settings are made of; a gamepad reaches the rows in the navigation group given.
    internal ModSettingsPane(RectTransform pane, float width, Transform plate, ScrollRect scroll, UISwitchButton switched,
        UISlider slid, TMP_InputField typed, TMP_Text section, TMP_Text line, int group)
    {
        this.scroll = scroll;
        this.switched = switched;
        this.slid = slid;
        this.typed = typed;
        this.section = section;
        this.group = group;
        name = NativeWindow.Plate(plate, pane, string.Empty, width / 2f, NativeWindow.PlateHeight / 2f, width);
        name.transform.parent.name = "Name";
        var view = (RectTransform)scroll.transform;
        view.SetParent(pane, false);
        view.anchorMin = Vector2.zero;
        view.anchorMax = Vector2.one;
        view.pivot = new Vector2(0.5f, 0.5f);
        view.offsetMin = new Vector2(0f, DescriptionHeight);
        view.offsetMax = new Vector2(0f, -(NativeWindow.PlateHeight + PlateGap));
        rows = scroll.content;
        foreach (Transform old in rows.Cast<Transform>().ToArray())
            UnityEngine.Object.DestroyImmediate(old.gameObject);
        var layout = rows.GetComponent<VerticalLayoutGroup>();
        layout.spacing = RowGap;
        layout.padding = new RectOffset(0, 0, (int)Edge, (int)Edge);
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        description = Line(line, pane, "Description");
        var about = description.rectTransform;
        about.anchorMin = Vector2.zero;
        about.anchorMax = new Vector2(1f, 0f);
        about.pivot = new Vector2(0.5f, 0f);
        about.sizeDelta = new Vector2(-2f * Edge, DescriptionHeight);
        about.anchoredPosition = Vector2.zero;
        notice = Line(line, scroll.viewport, "Notice");
        notice.rectTransform.anchorMin = Vector2.zero;
        notice.rectTransform.anchorMax = Vector2.one;
        notice.rectTransform.offsetMin = notice.rectTransform.offsetMax = Vector2.zero;
        notice.text = "This mod has no settings.";
    }

    // The rows a gamepad reaches, first to last.
    internal IReadOnlyList<GamepadNavigationItem> Items => items;

    internal bool Holds(GamepadNavigationItem item) => item != null && items.Contains(item);

    // Shows this mod's settings from the top, as they stand now.
    internal void Show(PluginInfo plugin)
    {
        NativeWindow.SetText(name, NativeWindow.Literal(ModList.Named(plugin)));
        foreach (Transform old in rows.Cast<Transform>().ToArray())
        {
            old.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(old.gameObject);
        }
        items.Clear();
        shown.Clear();
        slidValues.Clear();
        Describe(null);
        var settings = ModSetting.Of(plugin);
        string shownSection = null;
        foreach (var setting in settings)
        {
            if (setting.Section != shownSection)
            {
                shownSection = setting.Section;
                Heading(setting.Section);
            }
            Add(setting);
        }
        notice.gameObject.SetActive(settings.Count == 0);
        DrawnRows.Centre(shown, row => slidValues.TryGetValue(row, out var values) ? values : Array.Empty<string>());
        LayoutRebuilder.ForceRebuildLayoutImmediate(rows);
        scroll.verticalNormalizedPosition = 1f;
    }

    // Shows what a setting is for, or nothing.
    internal void Describe(ModSetting setting) =>
        description.text = setting == null ? string.Empty : NativeWindow.Literal(setting.Description);

    private void Heading(string text)
    {
        var heading = UnityEngine.Object.Instantiate(section, rows);
        heading.name = "Section " + text;
        heading.gameObject.SetActive(true);
        // The heading keeps the card name's colour and font, but stands in the middle.
        UnityEngine.Object.DestroyImmediate(heading.GetComponent<TextStyleComponent>());
        heading.alignment = TextAlignmentOptions.Center;
        heading.textWrappingMode = TextWrappingModes.NoWrap;
        heading.overflowMode = TextOverflowModes.Ellipsis;
        heading.rectTransform.sizeDelta = new Vector2(RowWidth, SectionHeight);
        NativeWindow.SetText(heading, NativeWindow.Literal(text));
    }

    private void Add(ModSetting setting)
    {
        GameObject row;
        GamepadNavigationItem item;
        switch (setting.Kind)
        {
            case ModSetting.Kinds.Switched:
                var choice = NativeWindow.Choice(switched, rows, setting.Name, setting.Labels, setting.Index, setting.Choose);
                row = choice.gameObject;
                item = row.GetComponent<GamepadNavigationItem>();
                break;
            case ModSetting.Kinds.Slid:
                var slider = NativeWindow.Slider(slid, rows, setting.Name, setting.Positions, setting.Position, setting.ValueAt, setting.Slide);
                // Until it moves, the slider shows the value as it stands, which may lie between its steps.
                slider.transform.Find("Value").GetComponent<TMP_Text>().text = setting.Value;
                row = slider.gameObject;
                slidValues[(RectTransform)row.transform] = Enumerable.Range(0, setting.Positions + 1).Select(setting.ValueAt)
                    .Append(setting.Value).ToArray();
                item = row.GetComponent<GamepadNavigationItem>();
                break;
            default:
                var field = NativeWindow.TextRow(switched, typed, rows, setting.Name, string.Empty, 0);
                field.SetTextWithoutNotify(setting.Kind == ModSetting.Kinds.Typed ? setting.Text : setting.Value);
                if (setting.Kind == ModSetting.Kinds.Shown)
                    field.interactable = false;
                else
                    // What the mod cannot read leaves the setting as it was, and the field shows it again.
                    field.onEndEdit.AddListener(text =>
                    {
                        setting.Type(text);
                        field.SetTextWithoutNotify(setting.Text);
                    });
                row = field.transform.parent.gameObject;
                item = field.GetComponent<GamepadNavigationItem>();
                break;
        }
        Fit((RectTransform)row.transform);
        shown.Add((RectTransform)row.transform);
        item.group = group;
        item.OnFocus.AddListener(() => Describe(setting));
        var pointed = row.AddComponent<Pointed>();
        pointed.Entered = () => Describe(setting);
        pointed.Left = () => Describe(null);
        items.Add(item);
    }

    // A long name wraps, and its row grows to hold it, every part standing in the row's middle as before.
    private static void Fit(RectTransform row)
    {
        var label = row.Find("LeftName").GetComponent<TMP_Text>();
        float one = label.GetPreferredValues("Ag").y;
        float grow = Mathf.Ceil(label.GetPreferredValues(label.text, label.rectTransform.rect.width, 0f).y - one);
        if (grow < one / 2f)
            return;
        float half = row.sizeDelta.y / 2f;
        foreach (RectTransform part in row)
        {
            if (part.anchorMin.y != part.anchorMax.y || part.anchorMin.y == 0.5f)
                continue;
            float fromMiddle = part.anchoredPosition.y + (part.anchorMin.y - 0.5f) * 2f * half;
            part.anchorMin = new Vector2(part.anchorMin.x, 0.5f);
            part.anchorMax = new Vector2(part.anchorMax.x, 0.5f);
            part.anchoredPosition = new Vector2(part.anchoredPosition.x, fromMiddle);
        }
        row.sizeDelta += new Vector2(0f, grow);
        label.rectTransform.sizeDelta += new Vector2(0f, grow);
    }

    private static TMP_Text Line(TMP_Text template, Transform parent, string name)
    {
        var line = UnityEngine.Object.Instantiate(template, parent);
        line.name = name;
        line.gameObject.SetActive(true);
        // The line keeps its own colour and alignment.
        UnityEngine.Object.DestroyImmediate(line.GetComponent<TextStyleComponent>());
        line.color = Dim;
        line.alignment = TextAlignmentOptions.Center;
        line.textWrappingMode = TextWrappingModes.Normal;
        line.overflowMode = TextOverflowModes.Ellipsis;
        NativeWindow.SetText(line, string.Empty);
        return line;
    }

    // Tells the pane when the pointer comes over a row and leaves it, and nothing else, so the scroll view still
    // takes the mouse wheel over the rows.
    private sealed class Pointed : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal Action Entered;
        internal Action Left;

        public void OnPointerEnter(PointerEventData eventData) => Entered?.Invoke();

        public void OnPointerExit(PointerEventData eventData) => Left?.Invoke();
    }
}
