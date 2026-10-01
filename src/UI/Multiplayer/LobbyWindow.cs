using System.Collections.Generic;
using System.Net;
using System.Text;
using GYK2.TombManyKeepers.Multiplayer.Chat;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using GYK2.TombManyKeepers.Network.Steam;
using GYK2.TombManyKeepers.Patches.Saves;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The lobby, laid out as GYK1's over the menu's backdrop: its title and commands above, the players and the
// world being hosted on the left, the chat with its field and Send on the right, and Back and Start Game
// together in the middle along the bottom. Send sits beside the field, clear of the game's credits in the
// corner below. Invite opens the friends to invite to the game; a screen wide enough shows them outright in a
// column of their own on the right, the rest moved left to make room, and the Invite command goes, as does Show
// Lobby Code, whose line stands under the world instead, in the chat field's row.
// Everything is a copy of the game's own header plates, cells, text fields, buttons and save card. As in GYK1,
// everyone readies up, each ready player's avatar lit in gold: the host alone starts at once, and with others
// its Start Game becomes a Ready button like theirs until everyone is ready. A later player readies up in the
// running game's lobby and joins it. The chat shows the session's one chat log, which the game's chat shows too, each
// player's name in their colour, under the tab chosen on its plate: Players or NPCs, the game's chat showing the same.
internal sealed class LobbyWindow : LazyWindow<LazyWidgetDataBase>
{
    // Where GYK1's lobby has its parts, on the 640 by 360 screen: the columns' centres, and the rows'.
    private const float LeftColumn = 150f, Centre = 320f, RightColumn = 490f;
    private const float TitleWidth = 250f, PlateWidth = 236f, PanelWidth = 260f, SendWidth = 84f;
    private const float TitleRow = 24f, CommandRow = 58f, PlateRow = 92f, WorldPlateRow = 203f, SayRow = 290f, BottomRow = 334f;
    private const float PanelTop = 109f, PlayersHeight = 76f, WorldTop = 220f, WorldHeight = 53f;
    private const float ChatHeight = 163f, SayHeight = 24f, TextLine = 12f, LineGap = 2f;
    private const float AvatarRetry = 0.5f;
    // The layout's width with and without the friends column, the column's centre, as far right of the chat as the
    // players are left of it, and the screen width from which it fits with room to spare. Friends are listed again
    // this often.
    private const float NarrowWidth = 640f, WideWidth = 980f, FriendsColumn = 830f, WideScreen = 1060f;
    private const float FriendHeight = 36f, FriendGap = 6f, FriendPadding = 8f, Relisting = 5f;
    private const string SystemTag = "#E8A33E", SystemText = "#CFC9C0", NameColor = "#FFBD00", SaidColor = "#DDD3C0";
    // A character's name in the NPCs tab, grey while unknown.
    private const string NpcName = "#E0B878", UnknownName = "#A09A94";
    private const string CodeColor = "#968D88";
    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, UISaveSlot> SaveCard =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, UISaveSlot>("uiSaveSlotPrefab");
    private static LobbyWindow instance;
    // The session whose lobby the host has greeted, once.
    private static CoopSession greeted;

    private readonly HashSet<ulong> invited = new HashSet<ulong>();
    private UIMainMenuWindow menu;
    private RectTransform layout;
    private RectTransform title;
    private RectTransform commands;
    private RectTransform bottom;
    private GameObject inviteCommand;
    private GameObject codeCommand;
    private GameObject codeLine;
    private GameObject friendsColumn;
    private FriendsList friends;
    private UIDialogWindowButton inviteButton;
    private Image cell;
    private bool? wide;
    private float nextListing;
    private LobbyPlayers players;
    private GameObject card;
    private SaveSlotData shownWorld;
    private WindowTabs tabs;
    private UIDialogWindowButton send;
    private TMP_Text log;
    private ScrollRect scroll;
    private TMP_InputField input;
    private UIDialogWindowButton main;
    private string drawn;
    private bool avatarsPending;
    private float nextAvatarCheck;
    private bool inputSuspended;
    private bool inputWasActive;

    internal static void Open(UIMainMenuWindow menu)
    {
        if (instance == null)
            instance = Build();
        instance.menu = menu;
        instance.drawn = null;
        CoopSession.Loading += instance.Enter;
        CoopSession.Failed += instance.Report;
        ChatLog.Changed += instance.DrawChat;
        NpcNames.Changed += instance.DrawChat;
        ChatTabs.Changed += instance.ShowTab;
        menu.Close();
        instance.Open((LazyWidgetDataBase)null);
    }

    private static LobbyWindow Build()
    {
        var window = NativeWindow.Screen<LobbyWindow>(LazyUI.GetWindow<UIBugReportWindow>(), out var layout);
        window.layout = layout;
        var frame = window.transform.Find("GenericWIndowLayout");
        var plate = NativeWindow.Keep(window, frame.Find("Frame/HeaderGroup"));
        Object.DestroyImmediate(plate.Find("CloseButton").gameObject);
        var content = frame.Find("Content");
        var line = NativeWindow.Keep(window, content.Find("Privacy").GetComponent<TMP_Text>());
        var field = NativeWindow.Keep(window, content.Find("Title").GetComponent<TMP_InputField>());
        var box = NativeWindow.Keep(window, content.Find("Steps").GetComponent<TMP_InputField>());
        var button = NativeWindow.Keep(window, content.Find("Buttons/Send").GetComponent<UIDialogWindowButton>());
        NativeWindow.RemoveFrame(window);
        // Copies of the fields are made asleep, so they wake with only the parts the lobby keeps.
        field.gameObject.SetActive(false);
        box.gameObject.SetActive(false);
        var cell = field.GetComponent<Image>();
        var selection = SaveCard(LazyUI.GetWindow<UISaveSlotsWindow>()).transform.Find("Selection").GetComponent<Image>();

        window.cell = cell;
        window.title = (RectTransform)NativeWindow.Plate(plate, layout, "MULTIPLAYER LOBBY", Centre, TitleRow, TitleWidth).transform.parent;
        var commands = NativeWindow.ButtonRow(layout);
        window.commands = (RectTransform)commands;
        commands.GetComponent<HorizontalLayoutGroup>().spacing = 24f;
        NativeWindow.Place(commands, Centre, CommandRow, 0f, 0f);
        Navigable(NativeWindow.Button(button, commands, "Keepers", () => Notice("Keeper customization comes in a later version.")));
        window.inviteCommand = Navigable(NativeWindow.Button(button, commands, "Invite", window.OpenInvites)).gameObject;
        window.codeCommand = Navigable(NativeWindow.Button(button, commands, "Show Lobby Code", () => Notice("Lobby codes come later."))).gameObject;

        NativeWindow.Plate(plate, layout, "Players", LeftColumn, PlateRow, PlateWidth);
        var panel = NativeWindow.Cell(cell, layout, "Players panel");
        NativeWindow.Place(panel.transform, LeftColumn, PanelTop + PlayersHeight / 2f, PanelWidth, PlayersHeight);
        window.players = new LobbyPlayers(panel.rectTransform, cell, selection, line);
        NativeWindow.Plate(plate, layout, "World", LeftColumn, WorldPlateRow, PlateWidth);

        // The chat's tabs stand on its plate in place of its name.
        window.tabs = WindowTabs.OnPlate(NativeWindow.Plate(plate, layout, "Chat", RightColumn, PlateRow, PlateWidth), ChatTabs.Names,
            index => ChatTabs.Current = (ChatTabs.Tab)index);
        window.log = Shown(box, layout, "Chat panel");
        // A little room between a message's lines, and more between messages, in whole units.
        window.log.lineSpacing = LineGap / (window.log.fontSize * 0.01f);
        window.log.paragraphSpacing = LineGap / (window.log.fontSize * 0.01f);
        NativeWindow.Place(window.log.transform.parent.parent, RightColumn, PanelTop + ChatHeight / 2f, PanelWidth, ChatHeight);
        window.scroll = Scrolling(window.log);
        window.input = Say(field, layout);
        float fieldWidth = PanelWidth - SendWidth - 6f;
        NativeWindow.Place(window.input.transform, RightColumn - PanelWidth / 2f + fieldWidth / 2f, SayRow, fieldWidth, SayHeight);
        window.input.onSubmit.AddListener(window.Say);
        window.send = Navigable(Placed(NativeWindow.Button(button, NativeWindow.ButtonRow(layout), "Send", () => window.Say(window.input.text)),
            RightColumn + PanelWidth / 2f - SendWidth / 2f, SayRow));

        AddFriends(window, plate, cell, line, button);
        AddCode(window, cell, line);

        var bottom = NativeWindow.ButtonRow(layout);
        window.bottom = (RectTransform)bottom;
        bottom.GetComponent<HorizontalLayoutGroup>().spacing = 24f;
        NativeWindow.Place(bottom, Centre, BottomRow, 0f, 0f);
        Navigable(NativeWindow.Button(button, bottom, LLBase.L("tip_back"), window.Back));
        window.main = Navigable(NativeWindow.Button(button, bottom, "Start Game", null));
        window.Init();
        return window;
    }

    // A wide screen's lobby code, in a dark cell under the world, in the chat field's row: until lobby codes come, a
    // line saying so.
    private static void AddCode(LobbyWindow window, Image cell, TMP_Text line)
    {
        var box = NativeWindow.Cell(cell, window.layout, "Lobby code");
        NativeWindow.Place(box.transform, LeftColumn, SayRow, PanelWidth, NativeWindow.FieldHeight);
        var text = Object.Instantiate(line, box.transform);
        text.name = "Text";
        Object.DestroyImmediate(text.GetComponent<TextStyleComponent>());
        text.gameObject.SetActive(true);
        text.richText = true;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.margin = Vector4.zero;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
        NativeWindow.SetText(text, $"<color={NameColor}>Lobby code:</color> <color={CodeColor}>comes later</color>");
        window.codeLine = box.gameObject;
        window.codeLine.SetActive(false);
    }

    // The friends column of a wide screen: its plate, the friends in the game's dark cell, scrolled as the chat is,
    // and Refresh and Invite beneath, beside the chat's field.
    private static void AddFriends(LobbyWindow window, Transform plate, Image cell, TMP_Text line, UIDialogWindowButton button)
    {
        var column = new GameObject("Friends column", typeof(RectTransform)).GetComponent<RectTransform>();
        column.SetParent(window.layout, false);
        column.anchorMin = Vector2.zero;
        column.anchorMax = Vector2.one;
        column.offsetMin = column.offsetMax = Vector2.zero;
        window.friendsColumn = column.gameObject;
        NativeWindow.Plate(plate, column, "Invite Friends", FriendsColumn, PlateRow, PlateWidth);
        var panel = NativeWindow.Cell(cell, column, "Friends panel");
        NativeWindow.Place(panel.transform, FriendsColumn, PanelTop + ChatHeight / 2f, PanelWidth, ChatHeight);
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
        viewport.SetParent(panel.transform, false);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = viewport.offsetMax = Vector2.zero;
        var list = new GameObject("Friends", typeof(RectTransform), typeof(GridLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
        list.SetParent(viewport, false);
        list.anchorMin = new Vector2(0f, 1f);
        list.anchorMax = Vector2.one;
        list.pivot = new Vector2(0.5f, 1f);
        list.sizeDelta = Vector2.zero;
        var grid = list.GetComponent<GridLayoutGroup>();
        grid.padding = new RectOffset((int)FriendPadding, (int)FriendPadding, (int)FriendPadding, (int)FriendPadding);
        grid.cellSize = new Vector2(PanelWidth - 2f * FriendPadding, FriendHeight);
        grid.spacing = new Vector2(0f, FriendGap);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 1;
        list.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = panel.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = list;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        SmoothMouseWheelScroll.Ensure(scroll, FriendHeight + FriendGap);
        var notice = Object.Instantiate(line, viewport);
        notice.name = "Notice";
        Object.DestroyImmediate(notice.GetComponent<TextStyleComponent>());
        notice.gameObject.SetActive(true);
        notice.alignment = TextAlignmentOptions.Center;
        notice.color = new Color(0.588f, 0.553f, 0.533f);
        notice.rectTransform.anchorMin = Vector2.zero;
        notice.rectTransform.anchorMax = Vector2.one;
        notice.rectTransform.offsetMin = notice.rectTransform.offsetMax = Vector2.zero;
        window.friends = new FriendsList(list, SaveCard(LazyUI.GetWindow<UISaveSlotsWindow>()), cell, notice, window.Invite);
        var actions = NativeWindow.ButtonRow(column);
        actions.GetComponent<HorizontalLayoutGroup>().spacing = 20f;
        NativeWindow.Place(actions, FriendsColumn, SayRow, 0f, 0f);
        Navigable(NativeWindow.Button(button, actions, "Refresh", window.ListFriends));
        window.inviteButton = Navigable(NativeWindow.Button(button, actions, "Invite", () => window.Invite(window.friends.Picked)));
        column.gameObject.SetActive(false);
    }

    // Each button stands in a row of its own, which sizes it to its label.
    private static UIDialogWindowButton Placed(UIDialogWindowButton button, float x, float y)
    {
        NativeWindow.Place(button.transform.parent, x, y, 0f, 0f);
        return button;
    }

    private static UIDialogWindowButton Navigable(UIDialogWindowButton button)
    {
        NativeWindow.Navigable(button.LazyButton);
        return button;
    }

    // One of the window's native text fields, showing text instead of taking it.
    private static TMP_Text Shown(TMP_InputField template, Transform parent, string name)
    {
        var field = Object.Instantiate(template, parent);
        field.name = name;
        var text = field.textComponent;
        Object.DestroyImmediate(field.placeholder.gameObject);
        foreach (var caret in field.GetComponentsInChildren<TMP_SelectionCaret>(true))
            Object.DestroyImmediate(caret.gameObject);
        Object.DestroyImmediate(field);
        // The lobby sets the text's alignment and colours itself.
        Object.DestroyImmediate(text.GetComponent<TextStyleComponent>());
        text.text = string.Empty;
        text.richText = true;
        text.parseCtrlCharacters = false;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.margin = new Vector4(8f, 8f, 8f, 8f);
        text.rectTransform.anchoredPosition = Vector2.zero;
        text.transform.parent.parent.gameObject.SetActive(true);
        return text;
    }

    // The chat's panel scrolls through everything said in the lobby, with the game's own smooth mouse wheel
    // scrolling or by dragging.
    private static ScrollRect Scrolling(TMP_Text log)
    {
        var content = log.rectTransform;
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;
        log.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = content.parent.parent.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = (RectTransform)content.parent;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        SmoothMouseWheelScroll.Ensure(scroll, 2f * TextLine);
        return scroll;
    }

    // The chat's own field, which a gamepad reaches and opens as well.
    private static TMP_InputField Say(TMP_InputField template, Transform parent)
    {
        var input = Object.Instantiate(template, parent);
        input.name = "Say";
        // The field makes its own caret as it wakes, so the template's copied one goes.
        foreach (var caret in input.GetComponentsInChildren<TMP_SelectionCaret>(true))
            Object.DestroyImmediate(caret.gameObject);
        input.characterLimit = ChatLog.MaxLength;
        input.lineType = TMP_InputField.LineType.SingleLine;
        // As the native field does, a tab is not typed.
        input.onValidateInput = (text, index, added) => added == '\t' ? '\0' : added;
        NativeWindow.SetText((TMP_Text)input.placeholder, "Type message...");
        input.gameObject.AddComponent<GamepadNavigationItem>().SetCallbacks(null, null, input.ActivateInputField);
        input.gameObject.SetActive(true);
        return input;
    }

    public override void Open(LazyWidgetDataBase data)
    {
        base.Open(data);
        invited.Clear();
        wide = null;
        layout.gameObject.SetActive(true);
        Arrange();
        input.text = string.Empty;
        var session = CoopSession.Current;
        ShowWorld(session.World);
        Greet(session);
        MarkTab();
        DrawChat();
        drawn = null;
        Refresh();
        GamepadNavigationController.ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
    }

    // The world being hosted, on a copy of the save list's own card: a new game, or the saved campaign with
    // when it was saved, the days played and the graveyard's, church's and village's standing.
    private void ShowWorld(SaveSlotData world)
    {
        shownWorld = world;
        if (card != null)
            DestroyImmediate(card);
        var slot = Instantiate(SaveCard(LazyUI.GetWindow<UISaveSlotsWindow>()), layout);
        SaveKindPatches.Displaying = true;
        try
        {
            slot.Show(world, canDelete: false);
        }
        finally
        {
            SaveKindPatches.Displaying = false;
        }
        card = slot.gameObject;
        card.name = "World card";
        // The copy only shows the world: what picks, deletes or imports a save goes.
        DestroyImmediate(slot);
        DestroyImmediate(card.GetComponent<GamepadNavigationItem>());
        DestroyImmediate(card.GetComponent<LazyButton>());
        card.transform.Find("Buttons").gameObject.SetActive(false);
        var picked = card.transform.Find("Picked");
        if (picked != null)
            DestroyImmediate(picked.gameObject);
        NativeWindow.Place(card.transform, LeftColumn, WorldTop + WorldHeight / 2f, PanelWidth, WorldHeight);
    }

    // The lobby's first lines, as GYK1's: the host tells everyone, players arriving later too, that the lobby waits for
    // them and how it set the game; a game for the host alone waits for no one. A player arriving in a running game hears
    // how to join it.
    private static void Greet(CoopSession session)
    {
        var settings = session.Settings;
        bool running = !session.IsHost && !session.InLobby && !session.StartedTogether;
        if (session.IsHost && greeted != session)
        {
            greeted = session;
            if (settings.Players > 1)
                session.Tell("Waiting for players...");
            string keepers = settings.Players == 1 ? "One keeper" : $"Up to {settings.Players} keepers";
            session.Tell($"{keepers}. {settings.Visibility}, {settings.NetworkShown}, cheats {(settings.Cheats ? "on" : "off")}.");
        }
        else if (running)
            Notice("The game is in progress. Ready up, then join.");
    }

    // A reply of the lobby's own to what this player pressed, for them alone.
    private static void Notice(string text) => ChatLog.Notice(text);

    protected override void Update()
    {
        base.Update();
        if (!IsShown)
            return;
        Arrange();
        if (wide == true && Time.unscaledTime >= nextListing)
            ListFriends();
        if (wide == true)
        {
            inviteButton.LazyButton.interactable = friends.Picked != 0;
            // A gamepad reaches the friends shown now; its focus stays where it was, or, if its card went, starts over.
            if (IsShownAndTop && friends.TakeChanges())
            {
                var focused = GamepadNavigationController.FocusedItem;
                bool kept = focused != null && focused.gameObject.activeInHierarchy;
                GamepadNavigationController.ReinitItems(!kept && LazyInput.IsGamepadActive, skipUnfocusItem: kept ? focused : null);
            }
        }
        Refresh();
        // Steam can take a moment to send a player's avatar.
        if (avatarsPending && Time.unscaledTime >= nextAvatarCheck && CoopSession.Current != null)
            DrawPlayers(CoopSession.Current);
    }

    // The friends column shows where the screen is wide enough for it, the layout widened to make room: the
    // columns keep their places, so they move left, and the title, commands and bottom buttons stay in the middle.
    private void Arrange()
    {
        bool fits = ((RectTransform)transform).rect.width >= WideScreen;
        if (wide == fits)
            return;
        wide = fits;
        float centre = (fits ? WideWidth : NarrowWidth) / 2f;
        layout.sizeDelta = new Vector2(fits ? WideWidth : NarrowWidth, layout.sizeDelta.y);
        foreach (var part in new[] { title, commands, bottom })
            part.anchoredPosition = new Vector2(centre, part.anchoredPosition.y);
        inviteCommand.SetActive(!fits);
        codeCommand.SetActive(!fits);
        friendsColumn.SetActive(fits);
        codeLine.SetActive(fits);
        if (fits)
        {
            InviteWindow.CloseIfOpen();
            ListFriends();
        }
        layout.RefreshContentFitter();
        GamepadNavigationController.ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
    }

    private void ListFriends()
    {
        friends.Show(InLobby, invited.Contains);
        nextListing = Time.unscaledTime + (friends.AvatarsPending ? AvatarRetry : Relisting);
    }

    private static bool InLobby(ulong account)
    {
        var session = CoopSession.Current;
        for (int slot = 1; session != null && slot <= CoopSession.MaxPlayers; slot++)
            if (session.PlayerAccount(slot) == account)
                return true;
        return false;
    }

    // Invite on a narrow screen: the friends' window over the lobby, which steps aside until it closes.
    private void OpenInvites()
    {
        layout.gameObject.SetActive(false);
        InviteWindow.Open(cell, Invite, InLobby, invited.Contains, () =>
        {
            layout.gameObject.SetActive(true);
            layout.RefreshContentFitter();
            GamepadNavigationController.ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
        });
    }

    // Sends a friend Steam's invite to this lobby's game. Their game looks for the host at the addresses the host
    // is reached at: this machine's for the host, or the address a joined player reached the host at. A player who
    // joined through Steam's network knows no address to give.
    private void Invite(ulong friend)
    {
        var session = CoopSession.Current;
        if (session == null || friend == 0)
            return;
        var host = session.HostEndpoint;
        if (!session.IsHost && host == null)
        {
            Notice("Inviting to a game joined online comes later.");
            return;
        }
        bool reached = host != null && !IPAddress.IsLoopback(host.Address);
        var addresses = reached ? new List<IPAddress> { host.Address } : GameInvites.LocalAddresses();
        ushort port = host != null ? (ushort)host.Port : CoopSession.GamePort;
        string name = friends.NameOf(friend);
        if (addresses.Count == 0)
        {
            Notice($"This computer has no network address to invite {name} to.");
            return;
        }
        if (!GameInvites.Send(friend, session.PlayerAccount(CoopSession.HostSlot), port, session.LobbyKey, addresses))
        {
            Notice($"Steam did not send the invite to {name}.");
            return;
        }
        bool first = invited.Count == 0;
        invited.Add(friend);
        Notice(first ? $"Invited {name}. For now, invited friends join from your network or a VPN." : $"Invited {name}.");
        if (wide == true)
            ListFriends();
        InviteWindow.Relist();
    }

    private void DrawPlayers(CoopSession session)
    {
        avatarsPending = players.Draw(session);
        nextAvatarCheck = Time.unscaledTime + AvatarRetry;
    }

    // Typing in the chat keeps the game's keys from acting, as the game's own text fields do.
    private void LateUpdate()
    {
        if (!IsShown)
            return;
        bool typing = input.isFocused;
        if (typing && !inputSuspended)
        {
            inputWasActive = LazyInput.IsInputActive();
            LazyInput.SetInputActivity(isInputActive: false);
            inputSuspended = true;
        }
        else if (!typing && inputSuspended)
        {
            RestoreInput();
        }
        if (typing && Input.GetKeyDown(KeyCode.Escape))
            input.DeactivateInputField();
    }

    private void RestoreInput()
    {
        inputSuspended = false;
        LazyInput.SetInputActivity(inputWasActive);
    }

    // The window is drawn again only when what it shows changes.
    private void Refresh()
    {
        var session = CoopSession.Current;
        if (session == null)
            return;
        // The host's Load Game changes the world it hosts while later players wait here.
        if (session.World != shownWorld)
            ShowWorld(session.World);
        bool ready = session.IsReady(session.LocalSlot);
        // A later player readies up in the running game's lobby, then joins it.
        bool running = !session.IsHost && !session.InLobby && !session.StartedTogether;
        var state = new StringBuilder();
        state.Append(session.Settings.Players).Append(ready).Append(running).Append(session.AllReady).Append(session.InLobby);
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
            state.Append('|').Append(session.PlayerName(slot)).Append(session.IsReady(slot)).Append(session.PlayerAccount(slot));
        if (state.ToString() == drawn)
            return;
        drawn = state.ToString();

        DrawPlayers(session);
        if (session.IsHost)
        {
            // The host alone starts at once; with others its Start Game becomes a Ready button like theirs, and
            // returns once everyone, the host too, is ready.
            if (session.CanStart)
                Draw("Start Game", StartGame);
            else
                Draw(ready ? "Unready" : "Ready", ToggleHostReady);
        }
        else if (running)
        {
            Draw(ready ? "Join Game" : "Ready", ready ? session.JoinGame : () => session.SetReady(true));
        }
        else
        {
            Draw(ready ? "Unready" : "Ready", () => session.SetReady(!ready));
        }
        // The native buttons size themselves to their labels in the layout's pass.
        layout.RefreshContentFitter();
    }

    // The host readies up or stops being ready, as every player does; the session tells everyone how many are ready.
    private static void ToggleHostReady()
    {
        var session = CoopSession.Current;
        session?.SetReady(!session.IsReady(session.LocalSlot));
    }

    private void Draw(string text, System.Action pressed)
    {
        main.name = text;
        main.Draw(new UIDialogWindowData.ButtonData(pressed, text, null, replaceForGamepad: false, keyToReplace: GameKey.Back));
    }

    // The chat follows its latest line while the player reads at its end, and stays put when scrolled back.
    private void DrawChat() => DrawChat(following: false);

    private void DrawChat(bool following)
    {
        var viewport = scroll.viewport.rect;
        following |= scroll.verticalNormalizedPosition <= 0.01f || log.rectTransform.rect.height <= viewport.height;
        log.text = Chat(ChatLog.Lines, ChatTabs.Current);
        LayoutRebuilder.ForceRebuildLayoutImmediate(log.rectTransform);
        if (following)
            scroll.verticalNormalizedPosition = 0f;
    }

    // Another tab was chosen, here or in the game's chat: it shows from its latest line.
    private void ShowTab()
    {
        MarkTab();
        DrawChat(following: true);
    }

    // The plate marks the chosen tab. NPCs only shows what was said with the game's people; the players chat under
    // Players.
    private void MarkTab()
    {
        tabs.Choose((int)ChatTabs.Current);
        bool players = ChatTabs.Current == ChatTabs.Tab.Players;
        input.readOnly = !players;
        send.LazyButton.interactable = players;
        NativeWindow.SetText((TMP_Text)input.placeholder, players ? "Type message..." : "Switch to Players to chat");
    }

    private static string Chat(IReadOnlyList<ChatLog.Line> lines, ChatTabs.Tab tab)
    {
        var text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Tab != tab)
                continue;
            if (text.Length > 0)
                text.Append('\n');
            if (line.Tab == ChatTabs.Tab.Npcs)
            {
                // A character's line under the name the players know them by, a keeper's under their player's; its words
                // in this player's language.
                string name = line.Npc != null ? NpcNames.Shown(line.Npc) : line.Name;
                text.Append("<color=").Append(line.Npc != null ? name == NpcNames.Unknown ? UnknownName : NpcName
                        : PlayerColors.Valid(line.Color) ? PlayerColors.Hex(line.Color) : NameColor).Append('>')
                    .Append(NativeWindow.Literal(name)).Append(":</color> <color=").Append(SaidColor).Append('>')
                    .Append(NativeWindow.Literal(LLBase.L(line.Text))).Append("</color>");
            }
            else if (line.Name == null)
                text.Append("<color=").Append(SystemTag).Append(">[System]</color> <color=").Append(SystemText).Append('>')
                    .Append(NativeWindow.Literal(line.Text)).Append("</color>");
            else
                // A player's name in their colour, as GYK1's chat shows it.
                text.Append("<color=").Append(PlayerColors.Valid(line.Color) ? PlayerColors.Hex(line.Color) : NameColor).Append('>')
                    .Append(NativeWindow.Literal(line.Name)).Append(":</color> <color=")
                    .Append(SaidColor).Append('>').Append(NativeWindow.Literal(line.Text)).Append("</color>");
        }
        return text.ToString();
    }

    private void Say(string text)
    {
        if (ChatTabs.Current != ChatTabs.Tab.Players)
            return;
        CoopSession.Current?.Say(text);
        input.text = string.Empty;
        input.ActivateInputField();
    }

    private void StartGame()
    {
        if (CoopSession.Current?.StartGame() != true)
            return;
        Leave();
        MultiplayerMenu.Start(menu);
    }

    protected override bool OnPressedBack()
    {
        Back();
        return true;
    }

    private void Back()
    {
        CoopSession.Stop();
        Leave();
        menu.Open(null);
    }

    private void Report(string reason)
    {
        Leave();
        MultiplayerMenu.ShowError(menu, reason);
    }

    // The host started or this player joins: the loading screen replaces the lobby at once.
    private void Enter()
    {
        Leave();
        // Leaving the game later returns to the main menu, not this screen.
        MultiplayerMenu.Show(menu, false);
        LoadingScreen.Open(menu);
    }

    private void Leave()
    {
        InviteWindow.CloseIfOpen();
        layout.gameObject.SetActive(true);
        CoopSession.Loading -= Enter;
        CoopSession.Failed -= Report;
        ChatLog.Changed -= DrawChat;
        NpcNames.Changed -= DrawChat;
        ChatTabs.Changed -= ShowTab;
        if (inputSuspended)
            RestoreInput();
        CloseWithoutCallback();
    }

    // A gamepad's bumpers turn the chat's tabs, as they turn a window's pages.
    protected override Dictionary<GameKey, System.Func<bool>> GetGameKeyDelegates()
    {
        var delegates = base.GetGameKeyDelegates();
        delegates.Add(GameKey.NextTab, () => tabs.Next());
        delegates.Add(GameKey.PrevTab, () => tabs.Previous());
        return delegates;
    }

    protected override void TestDraw()
    {
    }
}
