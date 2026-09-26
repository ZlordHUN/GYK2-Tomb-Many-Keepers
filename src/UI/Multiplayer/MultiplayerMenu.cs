using System;
using System.Collections.Generic;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The multiplayer screen swaps the main menu buttons for Host, Join and Back.
internal static class MultiplayerMenu
{
    private static readonly List<GameObject> hidden = new List<GameObject>();
    private static LazyButton[] buttons;
    private static string closedReason;

    internal static void Init(params LazyButton[] screenButtons)
    {
        buttons = screenButtons;
        foreach (var button in buttons)
            button.gameObject.SetActive(false);
        CoopSession.Closed -= ReturnToMenu;
        CoopSession.Closed += ReturnToMenu;
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
        }
    }

    // Host first picks the campaign: a new game or a saved one.
    internal static void Host(UIMainMenuWindow menu) => CampaignPicker.Open(menu);

    internal static void Host(UIMainMenuWindow menu, SaveSlotData campaign)
    {
        if (!CoopSession.Host(campaign, out string error))
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
