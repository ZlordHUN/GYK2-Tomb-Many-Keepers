using System;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// Windows of this mod made from the game's own: a copy of one of its windows keeps the canvas, sounds and
// gamepad navigation, and the mod fills it with copies of the game's header plates, option rows, cells,
// text fields and buttons. The game opens, stacks, closes and navigates them like its own.
internal static class NativeWindow
{
    // A header plate's height, and the room its two ornaments take at its ends.
    internal const float PlateHeight = 26f;
    private const float PlateInset = 28f;
    private static readonly AccessTools.FieldRef<LazyWindow<LazyWidgetDataBase>, LazyButton> CloseButton =
        AccessTools.FieldRefAccess<LazyWindow<LazyWidgetDataBase>, LazyButton>("closeButton");
    private static readonly AccessTools.FieldRef<LazyWindow<LazyWidgetDataBase>, string> OpenSound =
        AccessTools.FieldRefAccess<LazyWindow<LazyWidgetDataBase>, string>("openSoundId");
    private static readonly AccessTools.FieldRef<LazyWindow<LazyWidgetDataBase>, string> CloseSound =
        AccessTools.FieldRefAccess<LazyWindow<LazyWidgetDataBase>, string>("closeSoundId");

    // The native window, copied without its own behaviour as a window of type T under the given header.
    internal static T Copy<T>(LazyWindow<LazyWidgetDataBase> native, string header) where T : LazyWindow<LazyWidgetDataBase>
    {
        var window = Clone<T>(native);
        var close = Find<LazyButton>(window.transform, "GenericWIndowLayout/Frame/HeaderGroup/CloseButton");
        close.gameObject.SetActive(true);
        CloseButton(window) = close;
        SetText(Find<TMP_Text>(window.transform, "GenericWIndowLayout/Frame/HeaderGroup/Header"), header);
        return window;
    }

    // The native window as a whole screen for the mod to lay out on the game's 640 by 360 canvas, centred
    // on other screen shapes: its canvas, sounds and gamepad navigation without the shade over the game
    // behind it. The caller keeps what it needs of the frame, then removes it.
    internal static T Screen<T>(LazyWindow<LazyWidgetDataBase> native, out RectTransform layout) where T : LazyWindow<LazyWidgetDataBase>
    {
        var window = Clone<T>(native);
        UnityEngine.Object.DestroyImmediate(window.transform.Find("BgShadow").gameObject);
        layout = new GameObject("Layout", typeof(RectTransform)).GetComponent<RectTransform>();
        layout.SetParent(window.transform, false);
        layout.sizeDelta = new Vector2(640f, 360f);
        return window;
    }

    internal static void RemoveFrame(Component window) =>
        UnityEngine.Object.DestroyImmediate(window.transform.Find("GenericWIndowLayout").gameObject);

    private static T Clone<T>(LazyWindow<LazyWidgetDataBase> native) where T : LazyWindow<LazyWidgetDataBase>
    {
        var source = native.gameObject;
        bool active = source.activeSelf;
        // Copied inactive, so no part of the copy wakes as the native window.
        source.SetActive(false);
        var copy = UnityEngine.Object.Instantiate(source, source.transform.parent);
        source.SetActive(active);
        copy.name = typeof(T).Name;
        UnityEngine.Object.DestroyImmediate(copy.GetComponent(native.GetType()));
        var window = copy.AddComponent<T>();
        OpenSound(window) = OpenSound(native);
        CloseSound(window) = CloseSound(native);
        return window;
    }

    internal static Transform Content(Component window) => window.transform.Find("GenericWIndowLayout/Content");

    // Keeps a part as a template for copies, out of the window's layout.
    internal static T Keep<T>(Component window, T part) where T : Component
    {
        var templates = window.transform.Find("Templates");
        if (templates == null)
        {
            templates = new GameObject("Templates", typeof(RectTransform)).transform;
            templates.SetParent(window.transform, false);
            templates.gameObject.SetActive(false);
        }
        part.transform.SetParent(templates, false);
        return part;
    }

    // Removes what is left of the native window's content.
    internal static void ClearContent(Component window)
    {
        var content = Content(window);
        for (int i = content.childCount - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(content.GetChild(i).gameObject);
    }

    internal static T Find<T>(Transform root, string path) where T : Component => root.Find(path).GetComponent<T>();

    internal static void SetText(TMP_Text label, string text)
    {
        if (label.TryGetComponent<LocalizedLabel>(out var localized))
            localized.IgnoreLocalize = true;
        label.text = text;
    }

    // Text a player typed, shown as typed: text tags in it are not applied.
    internal static string Literal(string text)
    {
        const string end = "</noparse";
        int at;
        while ((at = text.IndexOf(end, StringComparison.OrdinalIgnoreCase)) >= 0)
            text = text.Remove(at, end.Length);
        return "<noparse>" + text + "</noparse>";
    }

    // Places a part of a screen by its centre, measured from the screen's top left.
    internal static void Place(Transform part, float x, float y, float width, float height)
    {
        var rect = (RectTransform)part;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, -y);
    }

    // A window's header plate standing on its own, such as a screen's title or a panel's name. The template
    // is the frame's header without its close button.
    internal static TMP_Text Plate(Transform template, Transform parent, string text, float x, float y, float width)
    {
        var plate = UnityEngine.Object.Instantiate(template, parent);
        plate.name = text;
        plate.gameObject.SetActive(true);
        Place(plate, x, y, width, PlateHeight);
        var header = Find<TMP_Text>(plate, "Header");
        header.rectTransform.offsetMin = new Vector2(PlateInset, header.rectTransform.offsetMin.y);
        header.rectTransform.offsetMax = new Vector2(-PlateInset, header.rectTransform.offsetMax.y);
        // The window's own header leaves room for its close button; a plate's name sits in its middle.
        header.margin = Vector4.zero;
        header.textWrappingMode = TextWrappingModes.NoWrap;
        SetText(header, text);
        return header;
    }

    // A plain native cell, such as the dark ground under a panel's contents.
    internal static Image Cell(Image template, Transform parent, string name)
    {
        var cell = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
        cell.transform.SetParent(parent, false);
        cell.sprite = template.sprite;
        cell.type = template.type;
        cell.color = template.color;
        cell.pixelsPerUnitMultiplier = template.pixelsPerUnitMultiplier;
        cell.material = template.material;
        return cell;
    }

    // A native option row: its name and a value switched with its arrows.
    internal static UISwitchButton Choice(UISwitchButton template, Transform parent, string name, string[] values, int index,
        Action<int> changed)
    {
        var row = UnityEngine.Object.Instantiate(template, parent);
        row.name = name;
        row.gameObject.SetActive(true);
        SetText(Find<TMP_Text>(row.transform, "LeftName"), name);
        row.Initialize(changed, values, index);
        return row;
    }

    // A row of native buttons, centred under the content above it.
    internal static Transform ButtonRow(Transform parent)
    {
        var row = new GameObject("Buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter)).transform;
        row.SetParent(parent, false);
        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 12f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var fitter = row.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return row;
    }

    // A native dialog button. A gamepad presses the window's main button with its select key.
    internal static UIDialogWindowButton Button(UIDialogWindowButton template, Transform row, string text, Action pressed,
        bool main = false, Func<bool> available = null)
    {
        var button = UnityEngine.Object.Instantiate(template, row);
        button.name = text;
        button.gameObject.SetActive(true);
        button.Draw(new UIDialogWindowData.ButtonData(pressed, text, available, replaceForGamepad: main,
            keyToReplace: main ? GameKey.Select : GameKey.Back));
        return button;
    }

    // Gamepad navigation reaches a button and presses it, as the game's own windows set theirs up.
    internal static void Navigable(LazyButton button)
    {
        if (!button.TryGetComponent<GamepadNavigationItem>(out _))
            button.gameObject.AddComponent<GamepadNavigationItem>();
        button.SetCallbacksIntoGamepadNavigationItem();
    }
}
