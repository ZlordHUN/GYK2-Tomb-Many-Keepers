using System;
using System.Collections.Generic;
using System.Linq;
using GYK2.TombManyKeepers.Network.Discovery;
using GYK2.TombManyKeepers.Network.Session;
using GYK2.TombManyKeepers.Network.Steam;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// Join Game, laid out as GYK1's server browser over the menu's backdrop: "JOIN A GAME" above a window whose header
// tabs pick Internet, Friends, Favorites or LAN games, with Back, Refresh, Join by Code and Connect below. The
// window is a copy of the game's own save list, its header the character window's tabbed one and each game one of
// the save list's cards. It takes the screen's height, its list growing on taller screens as far as the window's
// background reaches; beyond that the screen's parts stand together in its middle. The browser opens on the Internet
// tab, which lists the games Steam's lobby search finds, joined through Steam's network; the LAN tab lists the games
// the local network answers with; the Favorites tab the player's favourite hosts from both, added and removed with the
// game's own context menu on a right click. A game is listed as its host's settings open it: to everyone, to the
// host's Steam friends, never for a private game, and with a lock for a password game, which asks for its password
// before joining and again after a wrong one. An invite the player accepted opens the browser, which also asks the
// host's addresses from the invite, with its lobby's key, and joins its game past any password once it answers.
// Friends' games and joining by code come later, as their tab and button say. The frame and the buttons stay clear of
// the game's credits in the corner below.
internal sealed class ServerBrowser : LazyWindow<LazyWidgetDataBase>
{
    private const float Centre = 320f, Width = 640f, TitleRow = 22f, FrameTop = 43f, FrameWidth = 600f;
    // The frame ends this far above the screen's bottom, leaving room below it for the gamepad's button tips, and
    // the buttons' middle stands this far above it. The layout was drawn for the game's smallest canvas, 360 tall.
    private const float FrameEnd = 62f, ButtonsEnd = 26f, Smallest = 360f;
    // The save list window's room around its list.
    private const float SidePadding = 48f, EndPadding = 70f;
    private const float SearchInterval = 1f, Unseen = 3.5f, Searching = 2f, InviteWait = 10f;
    // Steam is asked for the listed games this often while a tab shows them.
    private const float OnlineInterval = 10f;
    private const int Internet = 0, Friends = 1, Favorites = 2, Lan = 3;
    private static readonly string[] Tabs = { "Internet", "Friends", "Favorites", "LAN" };
    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, UISaveSlot> SaveCard =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, UISaveSlot>("uiSaveSlotPrefab");
    private static readonly AccessTools.FieldRef<UIDialogWindow, TextMeshProUGUI> DialogText =
        AccessTools.FieldRefAccess<UIDialogWindow, TextMeshProUGUI>("information");
    private static ServerBrowser instance;

    private readonly List<LanDiscovery.Game> games = new List<LanDiscovery.Game>();
    private readonly List<LanDiscovery.Game> lobbies = new List<LanDiscovery.Game>();
    private readonly List<LanDiscovery.Game> shown = new List<LanDiscovery.Game>();
    private readonly List<LanDiscovery.Game> favoriteGames = new List<LanDiscovery.Game>();
    private readonly List<FavoriteHosts.Host> missing = new List<FavoriteHosts.Host>();
    private UIMainMenuWindow menu;
    private WindowTabs tabs;
    private ServerList list;
    private LanDiscovery discovery;
    private SteamLobbies online;
    private UIDialogWindowButton connect;
    private RectTransform layout;
    private RectTransform title;
    private Transform frame;
    // The tallest the frame grows: its background is one texture, drawn at its own size.
    private float tallest;
    private RectTransform content;
    private RectTransform buttons;
    private float fitted;
    private float nextSearch;
    private float nextOnlineSearch;
    private float searchedSince;
    // The invite being followed, and since when; the game being joined.
    private GameInvites.Invite invite;
    private float invitedAt;
    private LanDiscovery.Game joining;

    internal static void Open(UIMainMenuWindow menu, GameInvites.Invite invite = null)
    {
        if (instance == null)
            instance = Build();
        instance.menu = menu;
        CoopSession.Admitted += instance.Admit;
        CoopSession.Loading += instance.Enter;
        CoopSession.Failed += instance.Report;
        CoopSession.PasswordRefused += instance.AskAgain;
        menu.Close();
        instance.Open((LazyWidgetDataBase)null);
        if (invite != null)
            Accept(invite);
    }

    // Follows an invite the player accepted, from the browser open now; false when it is not.
    internal static bool Accept(GameInvites.Invite invite)
    {
        var browser = instance;
        if (browser == null || !browser.IsShown || CoopSession.Current != null)
            return false;
        browser.invite = invite;
        browser.invitedAt = Time.unscaledTime;
        browser.nextSearch = 0f;
        var dialog = LazyUI.GetWindow<UIDialogWindow>();
        if (dialog.IsShown)
            dialog.CloseWithoutCallback();
        dialog.Open(new UIDialogWindowData("Join Game", $"Looking for {GameOf(invite.Host, start: false)}...",
            new UIDialogWindowData.ButtonData(browser.StopFollowing, "Cancel", keyToReplace: GameKey.Back)) { ShowCloseButton = false });
        return true;
    }

    private static ServerBrowser Build()
    {
        var native = LazyUI.GetWindow<UISaveSlotsWindow>();
        var window = NativeWindow.Screen<ServerBrowser>(native, out var layout);
        window.layout = layout;
        var frame = window.frame = window.transform.Find("GenericWIndowLayout");
        // The host's campaign pick adds its Next row to the save list; the browser has buttons of its own.
        var next = frame.Find("Buttons");
        if (next != null)
            DestroyImmediate(next.gameObject);
        var plate = NativeWindow.Keep(window, Instantiate(frame.Find("Frame/HeaderGroup"), window.transform));
        DestroyImmediate(plate.Find("CloseButton").gameObject);
        window.title = (RectTransform)NativeWindow.Plate(plate, window.layout, "JOIN A GAME", Centre, TitleRow, 250f).transform.parent;

        frame.SetParent(window.layout, false);
        window.content = (RectTransform)frame.Find("Content");
        // The tabbed header brings the splitter under it.
        DestroyImmediate(frame.Find("Frame/FrameDown").gameObject);
        window.tallest = NativeWindow.TallestFrame(frame);
        window.tabs = new WindowTabs(frame.Find("Frame"), Tabs, window.Show);
        NativeWindow.UseCloseButton(window, window.tabs.Close);

        var rows = window.content.Find("Scroll/Viewport/Content");
        foreach (var slot in rows.GetComponentsInChildren<UISaveSlot>(true))
            DestroyImmediate(slot.gameObject);
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
        window.list = new ServerList(rows, card, notice, window.Join);

        var button = LazyUI.GetWindow<UIGameSettingsWindow>().transform
            .Find("GenericWIndowLayout/Content/DialogueButtonPrefab").GetComponent<UIDialogWindowButton>();
        var buttons = NativeWindow.ButtonRow(window.layout);
        buttons.GetComponent<HorizontalLayoutGroup>().spacing = 20f;
        window.buttons = (RectTransform)buttons;
        NativeWindow.Place(buttons, Centre, Smallest - ButtonsEnd, 0f, 0f);
        NativeWindow.Navigable(NativeWindow.Button(button, buttons, LLBase.L("tip_back"), window.Back).LazyButton);
        NativeWindow.Navigable(NativeWindow.Button(button, buttons, "Refresh", window.Refresh).LazyButton);
        NativeWindow.Navigable(NativeWindow.Button(button, buttons, "Join by Code",
            () => Tell("Joining by code comes later.")).LazyButton);
        window.connect = NativeWindow.Button(button, buttons, "Connect", () => window.Join(window.list.Picked));
        NativeWindow.Navigable(window.connect.LazyButton);
        window.Init();
        return window;
    }

    public override void Open(LazyWidgetDataBase data)
    {
        base.Open(data);
        discovery = LanDiscovery.Browser();
        online = SteamLobbies.Browser();
        games.Clear();
        lobbies.Clear();
        list.Clear();
        nextSearch = 0f;
        nextOnlineSearch = 0f;
        searchedSince = Time.unscaledTime;
        tabs.UpdateHints();
        Fit();
        if (tabs.Current == Internet)
            Show(Internet);
        else
            tabs.Choose(Internet);
        UpdateConnect();
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
        if (Time.unscaledTime >= nextSearch)
        {
            nextSearch = Time.unscaledTime + SearchInterval;
            discovery.Search(FavoriteHosts.All.Where(host => host.Address != null).Select(host => host.Address),
                invite?.Addresses ?? Array.Empty<System.Net.IPAddress>(), invite?.Key);
        }
        discovery.Collect(games);
        games.RemoveAll(game => Time.unscaledTime - game.SeenAt > Unseen);
        foreach (var game in games)
            FavoriteHosts.Seen(game);
        online.Collect(lobbies);
        foreach (var game in lobbies)
            FavoriteHosts.Seen(game);
        if (invite != null)
            FollowInvite();
        if (tabs.Current != Friends)
            Show(tabs.Current);
        UpdateConnect();
        // A gamepad reaches the cards shown now; its focus stays where it was, or, if its card went, starts over.
        if (IsShownAndTop && list.TakeChanges())
        {
            var focused = GamepadNavigationController.FocusedItem;
            bool kept = focused != null && focused.gameObject.activeInHierarchy;
            GamepadNavigationController.ReinitItems(!kept && LazyInput.IsGamepadActive, skipUnfocusItem: kept ? focused : null);
        }
    }

    // The screen's height in the canvas's units, which the game's whole-pixel scaling leaves at 360 or more.
    private float Height => Mathf.Max(Smallest, ((RectTransform)transform).rect.height);

    // The title keeps to the top and the buttons to the bottom; the window's list takes the height between them
    // up to what its background covers, and any height left over stands evenly above and below.
    private void Fit()
    {
        fitted = Height;
        layout.sizeDelta = new Vector2(Width, fitted);
        float room = fitted - FrameTop - FrameEnd;
        float height = Mathf.Min(room, tallest);
        float down = Mathf.Floor((room - height) / 2f);
        title.anchoredPosition = new Vector2(Centre, -(TitleRow + down));
        float top = FrameTop + down;
        NativeWindow.Place(frame, Centre, top + height / 2f, FrameWidth, height);
        content.sizeDelta = new Vector2(FrameWidth - SidePadding, height - EndPadding);
        buttons.anchoredPosition = new Vector2(Centre, -(top + height + FrameEnd - ButtonsEnd));
    }

    // The invite's host joined as soon as its game answers; one that stays silent is explained.
    private void FollowInvite()
    {
        var game = games.Find(item => item.Host == invite.Host);
        if (game != null && CoopSession.Current == null)
        {
            string key = invite.Key;
            invite = null;
            CloseDialog();
            Connect(game, password: null, key);
            return;
        }
        if (Time.unscaledTime - invitedAt <= InviteWait)
            return;
        string silent = GameOf(invite.Host, start: true);
        invite = null;
        Tell($"{silent} did not answer. For now, an invite reaches a game on your network or across a VPN.");
    }

    private void StopFollowing()
    {
        invite = null;
        LazyUI.GetWindow<UIDialogWindow>().Close();
    }

    // The host's game by the host's Steam name as their friends see it, shown as typed; an unknown host's at the
    // start of a sentence or within one.
    private static string GameOf(ulong account, bool start)
    {
        string name = Steamworks.SteamFriends.GetFriendPersonaName(new Steamworks.CSteamID(account));
        if (string.IsNullOrEmpty(name) || name == "[unknown]")
            return start ? "The host's game" : "the host's game";
        return Typed(name) + "'s game";
    }

    // A player's name in the game's dialog, its text tags shown as typed.
    private static string Typed(string name) => DialogText(LazyUI.GetWindow<UIDialogWindow>()).richText ? NativeWindow.Literal(name) : name;

    // Connect joins the picked game, so it waits for a pick.
    private void UpdateConnect() => connect.LazyButton.interactable = list.Picked != null && CoopSession.Current == null;

    private void Show(int tab)
    {
        bool searching = Time.unscaledTime - searchedSince < Searching;
        // Steam is asked only while a tab lists what it finds, from the moment the tab shows.
        if ((tab == Internet || tab == Favorites) && Time.unscaledTime >= nextOnlineSearch)
        {
            nextOnlineSearch = Time.unscaledTime + OnlineInterval;
            online.Search();
        }
        if (tab == Lan || tab == Internet)
        {
            shown.Clear();
            shown.AddRange((tab == Lan ? games : lobbies).Where(Listed));
            shown.AddRange(SampleListings.On(tab));
            if (tab == Lan)
                list.Show(shown, searching ? "Searching LAN..." : "No LAN sessions found");
            else
                list.Show(shown, online.Searching && lobbies.Count == 0 ? "Searching..." : "No online sessions found");
            return;
        }
        if (tab == Friends)
        {
            list.Show(SampleListings.On(tab), "Friends' sessions come later.");
            return;
        }
        // Favourites that answer come first, joined as where they were found, the local network's answer over the
        // online listing; the others wait below, dimmed. The tab mixes games found in different places, so each says
        // where.
        favoriteGames.Clear();
        missing.Clear();
        foreach (var host in FavoriteHosts.All)
        {
            var game = games.Find(item => item.Host == host.Account && Listed(item)) ??
                       lobbies.Find(item => item.Host == host.Account && Listed(item)) ?? SampleListings.Find(host.Account);
            if (game != null)
                favoriteGames.Add(game);
            else
                missing.Add(host);
        }
        string add = LazyInput.IsGamepadActive
            ? $"Press {ControllerIconLibrary.GetIconId(GameKey.ItemMove)} on a game to add it."
            : "Right-click a game to add it.";
        list.Show(favoriteGames, missing, searching ? "Searching..." : "Not found", "No favorite servers\n" + add,
            game => SampleListings.SourceOf(game) ?? (game.Lobby != 0 ? "Internet" : "LAN"));
    }

    // The list starts over, searching again at once.
    private void Refresh()
    {
        games.Clear();
        online.Clear();
        lobbies.Clear();
        list.Clear();
        nextSearch = 0f;
        nextOnlineSearch = 0f;
        searchedSince = Time.unscaledTime;
        Show(tabs.Current);
    }

    // The game's context menu for the game the player points at, as the inventory opens one for an item: it adds
    // the game's host to the favourites, or removes a favourite.
    private bool OpenFavoriteMenu()
    {
        if (!IsShownAndTop || !list.TryPointed(out ulong host, out var game, out var at))
            return false;
        bool favorite = FavoriteHosts.Contains(host);
        if (!favorite && game == null)
            return false;
        var menu = LazyUI.GetWindow<UIContextMenuWindow>();
        menu.Open(new UIContextMenuWindowData
        {
            Position = at,
            Options = new List<UIContextMenuWindowWidgetData>
            {
                new UIContextMenuWindowWidgetData(favorite ? "Remove from Favorites" : "Add to Favorites", () =>
                {
                    if (favorite)
                        FavoriteHosts.Remove(host);
                    else
                        FavoriteHosts.Add(game);
                    menu.Close();
                    Show(tabs.Current);
                })
            }
        });
        return true;
    }

    // Whether the browser lists a game: the host's settings open it to everyone, or to the host's Steam friends, of
    // whom the host's own account is one; a private game is never listed.
    private static bool Listed(LanDiscovery.Game game)
    {
        switch ((HostSettings.Access)game.Access)
        {
            case HostSettings.Access.Private:
                return false;
            case HostSettings.Access.Friends:
                return game.Host == PlayerIdentity.SteamId ||
                       Steamworks.SteamFriends.HasFriend(new Steamworks.CSteamID(game.Host), Steamworks.EFriendFlags.k_EFriendFlagImmediate);
            default:
                return true;
        }
    }

    // A password game asks for its password first.
    private void Join(LanDiscovery.Game game)
    {
        if (game == null || CoopSession.Current != null)
            return;
        if (game.Access == (byte)HostSettings.Access.Password)
            PasswordWindow.Open(game.Name, null, password => Connect(game, password, key: null));
        else
            Connect(game, password: null, key: null);
    }

    // A game found on the network is joined at its address, an online one through Steam's network.
    private void Connect(LanDiscovery.Game game, string password, string key)
    {
        if (CoopSession.Current != null)
            return;
        if (SampleListings.Contains(game))
        {
            Tell("This is a sample listing for reviewing the browser. There is no game behind it to join.");
            return;
        }
        string error;
        bool started = game.Endpoint != null
            ? CoopSession.Join(game.Endpoint, password, key, out error)
            : CoopSession.Join(game.Host, password, key, out error);
        if (!started)
        {
            Tell(error);
            return;
        }
        joining = game;
        var dialog = LazyUI.GetWindow<UIDialogWindow>();
        dialog.Open(new UIDialogWindowData("Join Game", $"Joining {Typed(game.Name)}...",
            new UIDialogWindowData.ButtonData(Cancel, "Cancel", keyToReplace: GameKey.Back)) { ShowCloseButton = false });
    }

    // The host asked for its password, or another than the one given: the password is asked again.
    private void AskAgain(string reason)
    {
        CloseDialog();
        var game = joining;
        if (game == null)
        {
            Tell(reason);
            return;
        }
        PasswordWindow.Open(game.Name, reason == "Wrong password." ? "Wrong password. Try again." : reason,
            password => Connect(game, password, key: null));
    }

    private static void Cancel()
    {
        CoopSession.Stop();
        LazyUI.GetWindow<UIDialogWindow>().Close();
    }

    // A note over the browser, which returns to it.
    private static void Tell(string text)
    {
        var dialog = LazyUI.GetWindow<UIDialogWindow>();
        if (dialog.IsShown)
            dialog.CloseWithoutCallback();
        dialog.Open(new UIDialogWindowData("Join Game", text,
            new UIDialogWindowData.ButtonData(dialog.Close, LLBase.L("btn_ok"), keyToReplace: GameKey.Back)));
    }

    // Joining failed: the note says why, and the browser stays for another try.
    private void Report(string reason) => Tell(reason);

    protected override Dictionary<GameKey, Func<bool>> GetGameKeyDelegates()
    {
        var delegates = base.GetGameKeyDelegates();
        delegates.Add(GameKey.NextTab, () => tabs.Next());
        delegates.Add(GameKey.PrevTab, () => tabs.Previous());
        delegates.Add(GameKey.RightClick, OpenFavoriteMenu);
        // A gamepad opens the menu of the game it is on with the key that opens an item's in the inventory.
        delegates.Add(GameKey.ItemMove, () => LazyInput.IsGamepadActive && OpenFavoriteMenu());
        return delegates;
    }

    protected override void PrintTips(GamepadNavigationItem item)
    {
        if (lazyButtonTips != null && list.Lists(item))
            lazyButtonTips.Print(LazyGameKeyTip.Select(), LazyGameKeyTip.Back(), new LazyGameKeyTip(GameKey.ItemMove, "tip_item_action"));
        else
            base.PrintTips(item);
    }

    protected override void UpdateGamepadDependentStuff()
    {
        base.UpdateGamepadDependentStuff();
        tabs?.UpdateHints();
    }

    protected override bool OnPressedBack()
    {
        Back();
        return true;
    }

    protected override void InitCloseButton(LazyButton button) => button.onClick.AddListener(Back);

    private void Back()
    {
        CoopSession.Stop();
        Leave();
        menu.Open(null);
    }

    // The host's lobby replaces the browser, also for a game already running.
    private void Admit()
    {
        CloseDialog();
        Leave();
        LobbyWindow.Open(menu);
    }

    // The player's entry into the host's game begins: the loading screen replaces the browser.
    private void Enter()
    {
        CloseDialog();
        Leave();
        // Leaving the game later returns to the main menu, not this screen.
        MultiplayerMenu.Show(menu, false);
        LoadingScreen.Open(menu);
    }

    private static void CloseDialog()
    {
        var dialog = LazyUI.GetWindow<UIDialogWindow>();
        if (dialog.IsShown)
            dialog.CloseWithoutCallback();
    }

    private void Leave()
    {
        invite = null;
        CoopSession.Admitted -= Admit;
        CoopSession.Loading -= Enter;
        CoopSession.Failed -= Report;
        CoopSession.PasswordRefused -= AskAgain;
        discovery?.Dispose();
        discovery = null;
        online?.Dispose();
        online = null;
        CloseWithoutCallback();
    }

    protected override void TestDraw()
    {
    }
}
