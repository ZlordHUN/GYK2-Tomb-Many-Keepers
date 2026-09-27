using System;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// GYK1's friend invite screen, opened from the lobby where the screen leaves no room for a friends column: "Select
// a friend to invite" over a copy of the game's own save list window, the player's online friends in three
// columns of cards, and Back, Refresh and Invite below. As the browser does, it takes the screen's height as far
// as its background reaches, standing in the middle of any height beyond.
internal sealed class InviteWindow : LazyWindow<LazyWidgetDataBase>
{
    private const float Centre = 320f, Width = 640f, FrameTop = 16f, FrameWidth = 600f;
    // Room below the frame for the gamepad's tips, then the buttons; the smallest canvas the game draws.
    private const float FrameEnd = 62f, ButtonsEnd = 26f, Smallest = 360f;
    // The save list window's room around its list.
    private const float SidePadding = 48f, EndPadding = 70f;
    // Three cards across the list's 574 units.
    private const float CardWidth = 182f, CardHeight = 36f, Across = 14f, Down = 10f;
    // Friends come and go and change what they do; the list looks again this often, and for avatars sooner.
    private const float Relisting = 5f, AvatarRetry = 0.5f;
    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, UISaveSlot> SaveCard =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, UISaveSlot>("uiSaveSlotPrefab");
    private static InviteWindow instance;

    private FriendsList friends;
    private RectTransform layout;
    private Transform frame;
    private RectTransform content;
    private RectTransform buttons;
    private UIDialogWindowButton invite;
    private float tallest;
    private float fitted;
    private float nextListing;
    private Action<ulong> send;
    private Func<ulong, bool> present;
    private Func<ulong, bool> invited;
    private Action closed;

    internal static bool IsOpen => instance != null && instance.IsShown;

    // Opens over the lobby, which hides until the window closes. The lobby sends the invites and knows who is in
    // it and who was invited.
    internal static void Open(Image cell, Action<ulong> send, Func<ulong, bool> present, Func<ulong, bool> invited, Action closed)
    {
        if (instance == null)
            instance = Build(cell);
        instance.send = send;
        instance.present = present;
        instance.invited = invited;
        instance.closed = closed;
        instance.Open((LazyWidgetDataBase)null);
    }

    // The list again at once, as after an invite.
    internal static void Relist()
    {
        if (IsOpen)
            instance.List();
    }

    internal static void CloseIfOpen()
    {
        if (IsOpen)
            instance.Leave();
    }

    private static InviteWindow Build(Image cell)
    {
        var native = LazyUI.GetWindow<UISaveSlotsWindow>();
        var window = NativeWindow.Screen<InviteWindow>(native, out var layout);
        window.layout = layout;
        var frame = window.frame = window.transform.Find("GenericWIndowLayout");
        // The host's campaign pick adds its Next row to the save list; this window has buttons of its own.
        var next = frame.Find("Buttons");
        if (next != null)
            DestroyImmediate(next.gameObject);
        // The splitter under the header stands by the frame's middle in the native window; here it keeps to the
        // header as the frame grows.
        var splitter = (RectTransform)frame.Find("Frame/FrameDown");
        float fromTop = ((RectTransform)splitter.parent).rect.height * (1f - splitter.anchorMin.y) - splitter.anchoredPosition.y;
        splitter.anchorMin = new Vector2(splitter.anchorMin.x, 1f);
        splitter.anchorMax = new Vector2(splitter.anchorMax.x, 1f);
        splitter.anchoredPosition = new Vector2(splitter.anchoredPosition.x, -fromTop);
        NativeWindow.SetText(frame.Find("Frame/HeaderGroup/Header").GetComponent<TMP_Text>(), "Select a friend to invite");
        var close = frame.Find("Frame/HeaderGroup/CloseButton").GetComponent<LazyButton>();
        close.gameObject.SetActive(true);
        NativeWindow.UseCloseButton(window, close);
        frame.SetParent(layout, false);
        window.content = (RectTransform)frame.Find("Content");
        window.tallest = NativeWindow.TallestFrame(frame);

        var rows = window.content.Find("Scroll/Viewport/Content");
        foreach (var slot in rows.GetComponentsInChildren<UISaveSlot>(true))
            DestroyImmediate(slot.gameObject);
        var footer = rows.Find("Footer");
        if (footer != null)
            DestroyImmediate(footer.gameObject);
        var column = rows.GetComponent<VerticalLayoutGroup>();
        var padding = column.padding;
        DestroyImmediate(column);
        var grid = rows.gameObject.AddComponent<GridLayoutGroup>();
        grid.padding = padding;
        grid.cellSize = new Vector2(CardWidth, CardHeight);
        grid.spacing = new Vector2(Across, Down);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 3;
        grid.childAlignment = TextAnchor.UpperCenter;
        var card = SaveCard(native);
        var notice = Instantiate(card.transform.Find("NewGameLabel").GetComponent<TMP_Text>(), window.content.Find("Scroll/Viewport"));
        notice.name = "Notice";
        DestroyImmediate(notice.GetComponent<LocalizedLabel>());
        // The notice keeps its own colour as it shows and hides.
        DestroyImmediate(notice.GetComponent<TextStyleComponent>());
        notice.color = new Color(0.588f, 0.553f, 0.533f);
        var area = notice.rectTransform;
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.pivot = new Vector2(0.5f, 0.5f);
        area.offsetMin = area.offsetMax = Vector2.zero;
        notice.alignment = TextAlignmentOptions.Center;
        window.friends = new FriendsList(rows, card, cell, notice, account => window.send(account));

        var button = LazyUI.GetWindow<UIGameSettingsWindow>().transform
            .Find("GenericWIndowLayout/Content/DialogueButtonPrefab").GetComponent<UIDialogWindowButton>();
        var buttons = NativeWindow.ButtonRow(layout);
        buttons.GetComponent<HorizontalLayoutGroup>().spacing = 20f;
        window.buttons = (RectTransform)buttons;
        NativeWindow.Place(buttons, Centre, Smallest - ButtonsEnd, 0f, 0f);
        NativeWindow.Navigable(NativeWindow.Button(button, buttons, LLBase.L("tip_back"), window.Leave).LazyButton);
        NativeWindow.Navigable(NativeWindow.Button(button, buttons, "Refresh", window.List).LazyButton);
        window.invite = NativeWindow.Button(button, buttons, "Invite", () => window.send(window.friends.Picked));
        NativeWindow.Navigable(window.invite.LazyButton);
        window.Init();
        return window;
    }

    public override void Open(LazyWidgetDataBase data)
    {
        base.Open(data);
        Fit();
        List();
        ((RectTransform)transform).RefreshContentFitter();
        GamepadNavigationController.ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
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
        if (Time.unscaledTime >= nextListing)
            List();
        invite.LazyButton.interactable = friends.Picked != 0;
        // A gamepad reaches the cards shown now; its focus stays where it was, or, if its card went, starts over.
        if (IsShownAndTop && friends.TakeChanges())
        {
            var focused = GamepadNavigationController.FocusedItem;
            bool kept = focused != null && focused.gameObject.activeInHierarchy;
            GamepadNavigationController.ReinitItems(!kept && LazyInput.IsGamepadActive, skipUnfocusItem: kept ? focused : null);
        }
    }

    private void List()
    {
        friends.Show(present, invited);
        nextListing = Time.unscaledTime + (friends.AvatarsPending ? AvatarRetry : Relisting);
        invite.LazyButton.interactable = friends.Picked != 0;
    }

    private float Height => Mathf.Max(Smallest, ((RectTransform)transform).rect.height);

    // The frame takes the height between the top and the buttons up to what its background covers; any height
    // left over stands evenly above and below.
    private void Fit()
    {
        fitted = Height;
        layout.sizeDelta = new Vector2(Width, fitted);
        float room = fitted - FrameTop - FrameEnd;
        float height = Mathf.Min(room, tallest);
        float top = FrameTop + Mathf.Floor((room - height) / 2f);
        NativeWindow.Place(frame, Centre, top + height / 2f, FrameWidth, height);
        content.sizeDelta = new Vector2(FrameWidth - SidePadding, height - EndPadding);
        buttons.anchoredPosition = new Vector2(Centre, -(top + height + FrameEnd - ButtonsEnd));
    }

    protected override bool OnPressedBack()
    {
        Leave();
        return true;
    }

    protected override void InitCloseButton(LazyButton button) => button.onClick.AddListener(Leave);

    private void Leave()
    {
        CloseWithoutCallback();
        var then = closed;
        closed = null;
        then?.Invoke();
    }

    protected override void TestDraw()
    {
    }
}
