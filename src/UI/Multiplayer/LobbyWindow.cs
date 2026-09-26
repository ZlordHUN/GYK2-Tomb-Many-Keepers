using System.Collections.Generic;
using System.Text;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// Lists the lobby's players and campaign in the native dialog. Joined players ready up; the host
// starts once everyone is ready. A later player readies up in the running game's lobby and joins.
internal sealed class LobbyWindow : MonoBehaviour
{
    private UIMainMenuWindow menu;
    private UIDialogWindow window;
    private string drawn;

    internal static void Open(UIMainMenuWindow menu)
    {
        var lobby = new GameObject("Multiplayer Lobby").AddComponent<LobbyWindow>();
        lobby.menu = menu;
        lobby.window = LazyUI.GetWindow<UIDialogWindow>();
        CoopSession.Loading += lobby.Enter;
        CoopSession.Failed += lobby.Report;
        menu.Close();
    }

    private void Update()
    {
        var session = CoopSession.Current;
        if (session != null)
            Draw(session);
    }

    // The dialog is rebuilt only when its content changes.
    private void Draw(CoopSession session)
    {
        var text = new StringBuilder($"Campaign: {session.CampaignLabel}\n\n");
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
        {
            string name = session.PlayerName(slot);
            if (name != null)
                text.Append($"Keeper {slot}: {name} - {(slot == 1 ? "Host" : session.IsReady(slot) ? "Ready" : "Not ready")}\n");
        }
        bool ready = session.IsReady(session.LocalSlot);
        // A later player readies up in the running game's lobby, then joins it.
        bool running = !session.IsHost && !session.InLobby && !session.StartedTogether;
        text.Append('\n').Append(session.IsHost ? session.AllReady ? "Start when everyone is here." : "Waiting for everyone to be ready..."
            : running ? ready ? "The game is in progress. Join when you are ready." : "The game is in progress. Ready up to join."
            : ready ? "Waiting for the host to start..." : "Ready up when you are.");
        if (text.ToString() == drawn)
            return;
        drawn = text.ToString();

        var buttons = new List<UIDialogWindowData.ButtonData>();
        if (session.IsHost)
            buttons.Add(new UIDialogWindowData.ButtonData(StartGame, "Start", () => session.AllReady,
                keyToReplace: GameKey.Select));
        else if (session.InLobby || running)
        {
            if (running && ready)
                buttons.Add(new UIDialogWindowData.ButtonData(session.JoinGame, "Join Game", replaceForGamepad: false,
                    keyToReplace: GameKey.Select));
            buttons.Add(new UIDialogWindowData.ButtonData(() => session.SetReady(!ready), ready ? "Not ready" : "Ready",
                replaceForGamepad: !running || !ready, keyToReplace: GameKey.Select));
        }
        buttons.Add(new UIDialogWindowData.ButtonData(window.Close, LLBase.L("tip_back"), keyToReplace: GameKey.Back));
        if (window.IsShown)
            window.CloseWithoutCallback();
        window.Open(new UIDialogWindowData("Lobby", drawn, buttons) { ShowCloseButton = true }, _ => Back());
    }

    private void StartGame()
    {
        if (!CoopSession.Current.StartGame())
            return;
        window.CloseWithoutCallback();
        Close();
        MultiplayerMenu.Start(menu);
    }

    private void Back()
    {
        CoopSession.Stop();
        Close();
        menu.Open(null);
    }

    private void Report(string reason)
    {
        window.CloseWithoutCallback();
        Close();
        MultiplayerMenu.ShowError(menu, reason);
    }

    // The host started or this player joins: the loading screen replaces this dialog at once.
    private void Enter()
    {
        window.CloseWithoutCallback();
        Close();
        // Leaving the game later returns to the main menu, not this screen.
        MultiplayerMenu.Show(menu, false);
        LoadingScreen.Open(menu);
    }

    // Destruction waits for the frame end; the dialog must not be redrawn meanwhile.
    private void Close()
    {
        CoopSession.Loading -= Enter;
        CoopSession.Failed -= Report;
        enabled = false;
        Destroy(gameObject);
    }
}
