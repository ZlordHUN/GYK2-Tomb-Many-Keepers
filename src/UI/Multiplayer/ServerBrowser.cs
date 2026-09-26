using System.Collections.Generic;
using System.Linq;
using GYK2.TombManyKeepers.Network.Discovery;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// Lists LAN games in the native dialog while it stays open.
internal sealed class ServerBrowser : MonoBehaviour
{
    private const int MaxListed = 3;
    private const float SearchInterval = 1f;
    private const float Timeout = 3.5f;

    private readonly List<LanDiscovery.Game> games = new List<LanDiscovery.Game>();
    private UIMainMenuWindow menu;
    private UIDialogWindow window;
    private LanDiscovery discovery;
    private string message;
    private string drawn;
    private float nextSearch;

    internal static void Open(UIMainMenuWindow menu)
    {
        var browser = new GameObject("Server Browser").AddComponent<ServerBrowser>();
        browser.menu = menu;
        browser.window = LazyUI.GetWindow<UIDialogWindow>();
        browser.discovery = LanDiscovery.Browser();
        CoopSession.Admitted += browser.Admit;
        CoopSession.Loading += browser.Enter;
        CoopSession.Failed += browser.Report;
        menu.Close();
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextSearch)
        {
            nextSearch = Time.unscaledTime + SearchInterval;
            discovery.Search();
        }
        discovery.Collect(games);
        games.RemoveAll(game => Time.unscaledTime - game.SeenAt > Timeout);
        Draw();
    }


    // The dialog is rebuilt only when its content changes.
    private void Draw()
    {
        var session = CoopSession.Current;
        var listed = games.Take(MaxListed).ToList();
        string text = session == null
            ? listed.Count == 0 ? "Searching for games on your network..."
                : string.Join("\n", listed.Select(game =>
                    $"{game.Name}  {game.Players}/{CoopSession.MaxPlayers}  {(game.InLobby ? "In lobby" : "In game")}"))
            : session.LocalSlot == 0 ? "Connecting..."
            : $"Joined as Keeper {session.LocalSlot}. Receiving the host's game...";
        if (message != null)
            text = message + "\n\n" + text;
        if (text == drawn)
            return;
        drawn = text;

        var buttons = new List<UIDialogWindowData.ButtonData>();
        if (session == null)
        {
            foreach (var game in listed)
                buttons.Add(new UIDialogWindowData.ButtonData(() => Connect(game), game.Name,
                    replaceForGamepad: false, keyToReplace: GameKey.Select));
        }
        buttons.Add(new UIDialogWindowData.ButtonData(window.Close, LLBase.L("tip_back"),
            keyToReplace: GameKey.Back));
        if (window.IsShown)
            window.CloseWithoutCallback();
        window.Open(new UIDialogWindowData("Join", text, buttons) { ShowCloseButton = true }, _ => Back());
    }

    private void Connect(LanDiscovery.Game game)
    {
        message = CoopSession.Join(game.Endpoint, out string error) ? null : error;
    }

    private void Report(string reason) => message = reason;

    private void Back()
    {
        CoopSession.Stop();
        Close();
        menu.Open(null);
    }

    // The host's lobby replaces this dialog, also for a game already running.
    private void Admit()
    {
        window.CloseWithoutCallback();
        Close();
        LobbyWindow.Open(menu);
    }

    // The player's entry into the host's game begins: the loading screen replaces this dialog.
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
        CoopSession.Admitted -= Admit;
        CoopSession.Loading -= Enter;
        CoopSession.Failed -= Report;
        discovery.Dispose();
        enabled = false;
        Destroy(gameObject);
    }
}
