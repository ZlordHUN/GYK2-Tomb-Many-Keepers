using System;
using System.Collections.Generic;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using GYK2.TombManyKeepers.Network.Steam;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The multiplayer screen swaps the main menu buttons for Host Game, Join Game and Back. An invite the player
// accepts opens Join Game to follow it, at once from the main menu or once the player returns there.
internal static class MultiplayerMenu
{
    private static readonly List<GameObject> hidden = new List<GameObject>();
    private static UIMainMenuWindow mainMenu;
    private static LazyButton[] buttons;
    private static string closedReason;
    // The campaign whose settings the host went back from, and the settings chosen for it.
    private static SaveSlotData edited;
    private static HostSettings editedSettings;

    internal static void Init(UIMainMenuWindow menu, params LazyButton[] screenButtons)
    {
        mainMenu = menu;
        buttons = screenButtons;
        foreach (var button in buttons)
            button.gameObject.SetActive(false);
        CoopSession.Closed -= ReturnToMenu;
        CoopSession.Closed += ReturnToMenu;
        GameInvites.Arrived -= InviteArrived;
        GameInvites.Arrived += InviteArrived;
        GameInvites.Listen();
    }

    internal static void Show(UIMainMenuWindow menu, bool visible)
    {
        foreach (var button in buttons)
            button.gameObject.SetActive(visible);
        if (!visible)
        {
            foreach (var item in hidden)
                item.SetActive(true);
            hidden.Clear();
        }
        Refresh(menu);
    }

    // Native Open() restores its own buttons, so they are hidden again while this screen shows.
    internal static void Refresh(UIMainMenuWindow menu)
    {
        if (buttons == null)
            return;
        if (buttons[0].gameObject.activeSelf)
        {
            foreach (Transform child in buttons[0].transform.parent)
            {
                if (!child.gameObject.activeSelf || !child.TryGetComponent<LazyButton>(out var button) ||
                    Array.IndexOf(buttons, button) >= 0)
                    continue;
                hidden.Add(child.gameObject);
                child.gameObject.SetActive(false);
            }
            // Child text-style components apply their own style during activation.
            foreach (var button in buttons)
                button.SetKeepPressed(false);
        }
        if (!menu.IsShown)
            return;
        ((RectTransform)menu.transform).RefreshContentFitterAndDisable();
        if (LazyInput.IsGamepadActive)
            menu.GetComponent<GamepadNavigationController>().ReinitItems(focusOnFirstActive: true);
        // A joined game that lost its host explains why it came back here.
        if (closedReason != null)
        {
            string reason = closedReason;
            closedReason = null;
            ShowError(menu, reason);
            return;
        }
        FollowInvite(menu);
    }

    // An accepted invite waiting for the main menu is followed once it shows. It is taken first: leaving the
    // multiplayer screen refreshes the menu, which would follow it again.
    private static void FollowInvite(UIMainMenuWindow menu)
    {
        if (GameInvites.Pending == null || CoopSession.Current != null || !menu.IsShownAndTop)
            return;
        var invite = GameInvites.Take();
        Show(menu, false);
        ServerBrowser.Open(menu, invite);
    }

    // An invite accepted while the game runs: followed from the main menu or the browser, kept for later during
    // a game of one's own, and set aside in a multiplayer game.
    private static void InviteArrived()
    {
        if (CoopSession.Current != null)
        {
            GameInvites.Take();
            Note("You are already in a multiplayer game. Leave it to accept an invite.");
            return;
        }
        if (ServerBrowser.Accept(GameInvites.Pending))
        {
            GameInvites.Take();
            return;
        }
        if (MainGame.Instance != null && MainGame.Instance.gameState != MainGame.GameState.MainMenu)
        {
            Note("The invite opens Join Game when you return to the main menu.");
            return;
        }
        if (mainMenu != null)
            FollowInvite(mainMenu);
    }

    private static void Note(string text)
    {
        var window = LazyUI.GetWindow<UIDialogWindow>();
        if (window.IsShown)
            window.CloseWithoutCallback();
        window.Open(new UIDialogWindowData("Multiplayer", text,
            new UIDialogWindowData.ButtonData(window.Close, LLBase.L("btn_ok"), keyToReplace: GameKey.Back)));
    }

    // Host first picks the campaign, a new game or a saved one, then the settings it is hosted with.
    internal static void Host(UIMainMenuWindow menu)
    {
        editedSettings = null;
        CampaignPicker.Open(menu);
    }

    // A saved campaign starts from the settings it was last hosted with. Going back to the campaigns and
    // on to the same one keeps the settings chosen for it.
    internal static void Configure(UIMainMenuWindow menu, SaveSlotData campaign)
    {
        var settings = editedSettings != null && CampaignPicker.Same(edited, campaign) ? editedSettings : HostSettings.Of(campaign);
        HostSettingsWindow.Open(settings, chosen =>
        {
            edited = campaign;
            editedSettings = chosen;
            CampaignPicker.Open(menu, keepPick: true);
        }, chosen =>
        {
            editedSettings = null;
            Host(menu, campaign, chosen);
        });
    }

    private static void Host(UIMainMenuWindow menu, SaveSlotData campaign, HostSettings settings)
    {
        if (!CoopSession.Host(campaign, settings, out string error))
        {
            ShowError(menu, error);
            return;
        }
        LobbyWindow.Open(menu);
    }

    // Starts the lobby's campaign natively behind the loading screen, which the lobby's players
    // share from this moment; they load the host's game once it has loaded here.
    internal static void Start(UIMainMenuWindow menu)
    {
        Show(menu, false);
        var saved = CoopSession.Current.Campaign;
        if (saved == null)
        {
            menu.OnStartNewGameButtonClicked();
            return;
        }
        LazyTimer.AddTimer(0f, delegate
        {
            var overlay = LazyUI.Get<UILoadingOverlay>();
            overlay.Draw(new LoadingWindowData(MainGame.EntrySceneToLoad, delegate
            {
                SaveSystem.Load(saved, save =>
                {
                    if (save != null)
                    {
                        MainGame.Instance.ContinueGame(saved, save);
                        return;
                    }
                    CoopSession.Stop();
                    ShowError(menu, "The saved campaign could not be loaded.");
                });
            }));
        });
    }

    internal static void Join(UIMainMenuWindow menu) => ServerBrowser.Open(menu);

    // The lobby ends with its host, so a joined game returns to the main menu.
    private static void ReturnToMenu(string reason)
    {
        closedReason = reason;
        if (WorldSnapshot.IsLoading)
            MainGame.OnGameStarted += LeaveLoadedGame;
        else
            MainGame.Instance.GoToMenu();
    }

    private static void LeaveLoadedGame()
    {
        MainGame.OnGameStarted -= LeaveLoadedGame;
        MainGame.Instance.GoToMenu();
    }

    internal static void ShowError(UIMainMenuWindow menu, string error)
    {
        var window = LazyUI.GetWindow<UIDialogWindow>();
        menu.Close();
        window.Open(new UIDialogWindowData("Multiplayer", error,
            new UIDialogWindowData.ButtonData(window.Close, LLBase.L("btn_ok"), keyToReplace: GameKey.Back)),
            _ => menu.Open(null));
    }
}
