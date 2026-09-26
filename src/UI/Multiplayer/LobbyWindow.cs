using System.Collections.Generic;
using System.Text;
using GYK2.TombManyKeepers.Multiplayer.Session;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The lobby, laid out as GYK1's over the menu's backdrop: its title and commands above, the players and the
// world being hosted on the left, the chat with its field and Send on the right, and Back and Start Game
// together in the middle along the bottom. Send sits beside the field, clear of the game's credits in the
// corner below.
// Everything is a copy of the game's own header plates, cells, text fields, buttons and save card. Joined
// players ready up and the host starts once everyone is ready; a later player readies up in the running
// game's lobby and joins it.
internal sealed class LobbyWindow : LazyWindow<LazyWidgetDataBase>
{
    // Where GYK1's lobby has its parts, on the 640 by 360 screen: the columns' centres, and the rows'.
    private const float LeftColumn = 150f, Centre = 320f, RightColumn = 490f;
    private const float TitleWidth = 250f, PlateWidth = 236f, PanelWidth = 260f, SendWidth = 84f;
    private const float TitleRow = 24f, CommandRow = 58f, PlateRow = 92f, WorldPlateRow = 203f, SayRow = 290f, BottomRow = 334f;
    private const float PanelTop = 109f, PlayersHeight = 76f, WorldTop = 220f, WorldHeight = 53f;
    private const float ChatHeight = 163f, SayHeight = 24f, TextLine = 12f, LineGap = 2f;
    private const float AvatarRetry = 0.5f;
    private const string SystemTag = "#E8A33E", SystemText = "#CFC9C0", NameColor = "#FFBD00", SaidColor = "#DDD3C0";
    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, UISaveSlot> SaveCard =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, UISaveSlot>("uiSaveSlotPrefab");
    private static LobbyWindow instance;

    private UIMainMenuWindow menu;
    private RectTransform layout;
    private LobbyPlayers players;
    private GameObject card;
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
        LobbyChat.Changed += instance.DrawChat;
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

        NativeWindow.Plate(plate, layout, "MULTIPLAYER LOBBY", Centre, TitleRow, TitleWidth);
        var commands = NativeWindow.ButtonRow(layout);
        commands.GetComponent<HorizontalLayoutGroup>().spacing = 24f;
        NativeWindow.Place(commands, Centre, CommandRow, 0f, 0f);
        Navigable(NativeWindow.Button(button, commands, "Keepers", () => Notice("Keeper customization comes in a later version.")));
        Navigable(NativeWindow.Button(button, commands, "Invite",
            () => Notice("Invites come with online play. Players on your network find this game under Join Game.")));
        Navigable(NativeWindow.Button(button, commands, "Show Lobby Code", () => Notice("Lobby codes come with online play.")));

        NativeWindow.Plate(plate, layout, "Players", LeftColumn, PlateRow, PlateWidth);
        var panel = NativeWindow.Cell(cell, layout, "Players panel");
        NativeWindow.Place(panel.transform, LeftColumn, PanelTop + PlayersHeight / 2f, PanelWidth, PlayersHeight);
        window.players = new LobbyPlayers(panel.rectTransform, cell, selection, line);
        NativeWindow.Plate(plate, layout, "World", LeftColumn, WorldPlateRow, PlateWidth);

        NativeWindow.Plate(plate, layout, "Chat", RightColumn, PlateRow, PlateWidth);
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
        Navigable(Placed(NativeWindow.Button(button, NativeWindow.ButtonRow(layout), "Send", () => window.Say(window.input.text)),
            RightColumn + PanelWidth / 2f - SendWidth / 2f, SayRow));

        var bottom = NativeWindow.ButtonRow(layout);
        bottom.GetComponent<HorizontalLayoutGroup>().spacing = 24f;
        NativeWindow.Place(bottom, Centre, BottomRow, 0f, 0f);
        Navigable(NativeWindow.Button(button, bottom, LLBase.L("tip_back"), window.Back));
        window.main = Navigable(NativeWindow.Button(button, bottom, "Start Game", null));
        window.Init();
        return window;
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
        input.characterLimit = LobbyChat.MaxLength;
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
        input.text = string.Empty;
        var session = CoopSession.Current;
        ShowWorld(session.World);
        Greet(session);
        DrawChat();
        drawn = null;
        Refresh();
        GamepadNavigationController.ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
    }

    // The world being hosted, on a copy of the save list's own card: a new game, or the saved campaign with
    // when it was saved, the days played and the graveyard's, church's and village's standing.
    private void ShowWorld(SaveSlotData world)
    {
        if (card != null)
            DestroyImmediate(card);
        var slot = Instantiate(SaveCard(LazyUI.GetWindow<UISaveSlotsWindow>()), layout);
        slot.Show(world, canDelete: false);
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

    // The lobby's first lines, as GYK1's: whether the game waits or runs, and the host's settings.
    private static void Greet(CoopSession session)
    {
        var settings = session.Settings;
        bool running = !session.IsHost && !session.InLobby && !session.StartedTogether;
        if (session.IsHost)
            Notice("Waiting for players...");
        else if (running)
            Notice("The game is in progress. Ready up, then join.");
        Notice($"Up to {settings.Players} keepers. {settings.Visibility}, cheats {(settings.Cheats ? "on" : "off")}.");
    }

    // A line of the lobby's own for this player only.
    private static void Notice(string text) => LobbyChat.Add(0, null, text);

    protected override void Update()
    {
        base.Update();
        if (!IsShown)
            return;
        Refresh();
        // Steam can take a moment to send a player's avatar.
        if (avatarsPending && Time.unscaledTime >= nextAvatarCheck && CoopSession.Current != null)
            DrawPlayers(CoopSession.Current);
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
            int present = 0, readied = 0;
            for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
            {
                if (session.PlayerName(slot) == null)
                    continue;
                present++;
                if (session.IsReady(slot))
                    readied++;
            }
            // As GYK1's, the host's button counts who is ready until everyone is.
            Draw(session.AllReady ? "Start Game" : $"Ready {readied}/{present}", () =>
            {
                if (CoopSession.Current?.AllReady == true)
                    StartGame();
                else
                    Notice($"Waiting for players to ready up ({readied}/{present} ready).");
            });
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

    private void Draw(string text, System.Action pressed)
    {
        main.name = text;
        main.Draw(new UIDialogWindowData.ButtonData(pressed, text, null, replaceForGamepad: false, keyToReplace: GameKey.Back));
    }

    // The chat follows its latest line while the player reads at its end, and stays put when scrolled back.
    private void DrawChat()
    {
        var viewport = scroll.viewport.rect;
        bool following = scroll.verticalNormalizedPosition <= 0.01f || log.rectTransform.rect.height <= viewport.height;
        log.text = Chat(LobbyChat.Lines);
        LayoutRebuilder.ForceRebuildLayoutImmediate(log.rectTransform);
        if (following)
            scroll.verticalNormalizedPosition = 0f;
    }

    private static string Chat(IReadOnlyList<LobbyChat.Line> lines)
    {
        var text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (text.Length > 0)
                text.Append('\n');
            var line = lines[i];
            if (line.Name == null)
                text.Append("<color=").Append(SystemTag).Append(">[System]</color> <color=").Append(SystemText).Append('>')
                    .Append(NativeWindow.Literal(line.Text)).Append("</color>");
            else
                text.Append("<color=").Append(NameColor).Append('>').Append(NativeWindow.Literal(line.Name)).Append(":</color> <color=")
                    .Append(SaidColor).Append('>').Append(NativeWindow.Literal(line.Text)).Append("</color>");
        }
        return text.ToString();
    }

    private void Say(string text)
    {
        CoopSession.Current?.Say(text);
        input.text = string.Empty;
        input.ActivateInputField();
    }

    private void StartGame()
    {
        if (!CoopSession.Current.StartGame())
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
        CoopSession.Loading -= Enter;
        CoopSession.Failed -= Report;
        LobbyChat.Changed -= DrawChat;
        if (inputSuspended)
            RestoreInput();
        CloseWithoutCallback();
    }

    protected override void TestDraw()
    {
    }
}
