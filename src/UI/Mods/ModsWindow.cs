using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Mods;

// The Mods window, opened from the main menu: a copy of the game's save list window as wide as Join Game's, headed
// "Mods", with the mods loaded now on the left, each on one of the list's own cards as GYK1 listed its mods, and the
// picked mod's settings on the right as the game's own option rows. It takes the screen's height as Join Game does,
// as far as its background reaches, with Back below it. A gamepad moves through the mods, each showing its settings,
// enters them with its select key or by moving right, and leaves them, and then the window, with its back key.
internal sealed class ModsWindow : LazyWindow<LazyWidgetDataBase>
{
    // Drawn for the game's smallest canvas, 640 by 360: the frame from near the screen's top to above its button, which
    // stands where Join Game's do.
    private const float Centre = 320f, Width = 640f, FrameTop = 9f, FrameWidth = 600f, FrameEnd = 62f, ButtonsEnd = 26f, Smallest = 360f;
    // The save list window's room around its list, and how far its list reaches past its content's sides, ends and top.
    private const float SidePadding = 48f, EndPadding = 70f, Reach = 11f, ReachDown = 10f, ReachUp = 5f;
    // The mods take this much of the frame's width, the settings the rest, this far apart.
    private const float ListWidth = 200f, Gap = 10f, PaneWidth = FrameWidth - SidePadding + 2f * Reach - ListWidth - Gap;
    // Gamepad groups: the mods, their settings and the button below.
    private const int Mods = 0, Settings = 1, Buttons = 2;
    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, UISaveSlot> SaveCard =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, UISaveSlot>("uiSaveSlotPrefab");
    private static ModsWindow instance;

    private UIMainMenuWindow menu;
    private RectTransform layout;
    private RectTransform frame;
    private RectTransform content;
    private RectTransform buttons;
    // The tallest the frame grows: its background is one texture, drawn at its own size.
    private float tallest;
    private float fitted;
    private ModList list;
    private ModSettingsPane settings;
    // The settings shown changed since a gamepad last took the rows it reaches.
    private bool changed;

    internal static void Open(UIMainMenuWindow menu)
    {
        if (instance == null)
            instance = Build();
        instance.menu = menu;
        menu.Close();
        instance.Open((LazyWidgetDataBase)null);
    }

    private static ModsWindow Build()
    {
        var native = LazyUI.GetWindow<UISaveSlotsWindow>();
        var window = NativeWindow.Screen<ModsWindow>(native, out var layout);
        window.layout = layout;
        var frame = window.transform.Find("GenericWIndowLayout");
        // The host's campaign pick adds its Next row to the save list; this window has a button of its own.
        var next = frame.Find("Buttons");
        if (next != null)
            DestroyImmediate(next.gameObject);
        var plate = NativeWindow.Keep(window, Instantiate(frame.Find("Frame/HeaderGroup"), window.transform));
        DestroyImmediate(plate.Find("CloseButton").gameObject);
        NativeWindow.SetText(NativeWindow.Find<TMP_Text>(frame, "Frame/HeaderGroup/Header"), "Mods");
        var close = NativeWindow.Find<LazyButton>(frame, "Frame/HeaderGroup/CloseButton");
        close.gameObject.SetActive(true);
        NativeWindow.UseCloseButton(window, close);
        KeepSplitterUnderHeader(frame);
        frame.SetParent(layout, false);
        window.frame = (RectTransform)frame;
        window.content = (RectTransform)frame.Find("Content");
        window.tallest = NativeWindow.TallestFrame(frame);

        var scroll = window.content.Find("Scroll").GetComponent<ScrollRect>();
        foreach (var slot in scroll.content.GetComponentsInChildren<UISaveSlot>(true))
            DestroyImmediate(slot.gameObject);
        // The settings scroll in a copy of the list's own scroll view, which follows a gamepad's focus as the list does.
        var rows = Instantiate(scroll.gameObject, window.content).GetComponent<ScrollRect>();
        rows.name = "Settings";
        var footer = rows.content.Find("Footer");
        if (footer != null)
            DestroyImmediate(footer.gameObject);
        Column((RectTransform)scroll.transform, ListWidth, left: true);
        var pane = new GameObject("Settings pane", typeof(RectTransform)).GetComponent<RectTransform>();
        pane.SetParent(window.content, false);
        Column(pane, PaneWidth, left: false);

        var options = LazyUI.GetWindow<UIGameSettingsWindow>().transform.Find("GenericWIndowLayout/Content");
        var switched = options.Find("VoiceOverSwitchBtn").GetComponent<UISwitchButton>();
        var slid = options.Find("MasterVolume").GetComponent<UISlider>();
        var button = options.Find("DialogueButtonPrefab").GetComponent<UIDialogWindowButton>();
        var field = LazyUI.GetWindow<UIBugReportWindow>().transform.Find("GenericWIndowLayout/Content/Title").GetComponent<TMP_InputField>();
        var card = SaveCard(native);
        window.settings = new ModSettingsPane(pane, PaneWidth, plate, rows, switched, slid, field,
            card.transform.Find("Date").GetComponent<TMP_Text>(), switched.transform.Find("LeftName").GetComponent<TMP_Text>(), Settings);
        window.list = new ModList(scroll.content, card, Mods, window.Picked, window.EnterSettings);

        var row = NativeWindow.ButtonRow(layout);
        window.buttons = (RectTransform)row;
        NativeWindow.Place(row, Centre, Smallest - ButtonsEnd, 0f, 0f);
        var back = NativeWindow.Button(button, row, LLBase.L("tip_back"), window.Back).LazyButton;
        NativeWindow.Navigable(back);
        back.GetComponent<GamepadNavigationItem>().group = Buttons;
        window.GetComponent<GamepadNavigationController>().navigationGroupSources = new List<GamepadNavigationController.NavigationGroupSource>
        {
            Leads(Mods, (Settings, GUIDirection.Right), (Buttons, GUIDirection.Down)),
            Leads(Settings, (Buttons, GUIDirection.Down)),
            Leads(Buttons, (Mods, GUIDirection.Up))
        };
        window.Init();
        return window;
    }

    public override void Open(LazyWidgetDataBase data)
    {
        base.Open(data);
        Fit();
        ((RectTransform)transform).RefreshContentFitter();
        list.Show(Chainloader.PluginInfos.Values, plugin => ModSetting.Of(plugin).Count);
        changed = false;
        GamepadNavigationController.ReinitItems(focusOnFirstActive: false);
        var picked = list.PickedItem;
        if (LazyInput.IsGamepadActive && picked != null)
            GamepadNavigationController.SetFocusedItem(picked);
    }

    protected override void Update()
    {
        base.Update();
        if (!IsShown)
            return;
        if (Height != fitted)
        {
            Fit();
            ((RectTransform)transform).RefreshContentFitter();
        }
        // A gamepad reaches the settings shown now, its focus staying where it was.
        if (changed)
            TakeRows();
    }

    // The mod picked on the left: its settings replace the ones shown.
    private void Picked(PluginInfo plugin)
    {
        settings.Show(plugin);
        changed = true;
    }

    // A gamepad's select key on a mod, or its move to the right, reaches the mod's first setting.
    private void EnterSettings()
    {
        if (!LazyInput.IsGamepadActive)
            return;
        if (changed)
            TakeRows();
        var first = settings.Items.FirstOrDefault();
        if (first != null)
            GamepadNavigationController.SetFocusedItem(first);
    }

    private void TakeRows()
    {
        changed = false;
        var focused = GamepadNavigationController.FocusedItem;
        bool kept = focused != null && focused.gameObject.activeInHierarchy;
        GamepadNavigationController.ReinitItems(!kept && LazyInput.IsGamepadActive, skipUnfocusItem: kept ? focused : null);
    }

    // The back key leaves a mod's settings for its card first, then the window.
    protected override bool OnPressedBack()
    {
        var picked = list.PickedItem;
        if (settings.Holds(GamepadNavigationController.FocusedItem) && picked != null)
        {
            GamepadNavigationController.SetFocusedItem(picked);
            return true;
        }
        Back();
        return true;
    }

    protected override void InitCloseButton(LazyButton button) => button.onClick.AddListener(Back);

    private void Back()
    {
        CloseWithoutCallback();
        menu.Open(null);
    }

    // The screen's height in the canvas's units, which the game's whole-pixel scaling leaves at 360 or more.
    private float Height => Mathf.Max(Smallest, ((RectTransform)transform).rect.height);

    // The frame keeps to the top and the button to the bottom; the frame takes the height between them up to what
    // its background covers, and any height left over stands evenly above and below.
    private void Fit()
    {
        fitted = Height;
        layout.sizeDelta = new Vector2(Width, fitted);
        float room = fitted - FrameTop - FrameEnd;
        float height = Mathf.Min(room, tallest);
        float down = Mathf.Floor((room - height) / 2f);
        float top = FrameTop + down;
        NativeWindow.Place(frame, Centre, top + height / 2f, FrameWidth, height);
        content.sizeDelta = new Vector2(FrameWidth - SidePadding, height - EndPadding);
        buttons.anchoredPosition = new Vector2(Centre, -(top + height + FrameEnd - ButtonsEnd));
    }

    // A column of the content, as tall as the list's scroll view reaches, at the content's left or right side.
    private static void Column(RectTransform part, float width, bool left)
    {
        float side = left ? 0f : 1f;
        part.anchorMin = new Vector2(side, 0f);
        part.anchorMax = new Vector2(side, 1f);
        part.pivot = new Vector2(side, 0.5f);
        part.offsetMin = new Vector2(left ? -Reach : Reach - width, -ReachDown);
        part.offsetMax = new Vector2(left ? width - Reach : Reach, ReachUp);
    }

    // The splitter under the header is placed from the save list's middle; it keeps its place under the header as
    // the frame grows.
    private static void KeepSplitterUnderHeader(Transform frame)
    {
        var splitter = (RectTransform)frame.Find("Frame/FrameDown");
        if (splitter == null)
            return;
        var padding = frame.GetComponent<VerticalLayoutGroup>().padding;
        float height = ((RectTransform)frame.Find("Content")).sizeDelta.y + padding.top + padding.bottom;
        float fromTop = height * (1f - splitter.anchorMin.y) - splitter.anchoredPosition.y;
        splitter.anchorMin = new Vector2(splitter.anchorMin.x, 1f);
        splitter.anchorMax = new Vector2(splitter.anchorMax.x, 1f);
        splitter.anchoredPosition = new Vector2(splitter.anchoredPosition.x, -fromTop);
    }

    private static GamepadNavigationController.NavigationGroupSource Leads(int group, params (int group, GUIDirection direction)[] targets) =>
        new GamepadNavigationController.NavigationGroupSource(group, targets
            .Select(target => new GamepadNavigationController.NavigationGroupTarget(target.group, target.direction)).ToList());

    protected override void TestDraw()
    {
    }
}
