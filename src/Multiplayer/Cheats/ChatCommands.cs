using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// GYK1's debug commands, typed into the game's chat: /help, /give, /spawn, /time, /tp and /weather. As GYK1's, the host
// can always use them, and the other players when the host turned cheats on for the campaign. A player who switched
// cheats off in the Mods window uses none, whatever the host allows, and a game they host has cheats off. What a command
// changes of the shared world, the day, its time and the weather, the host's game changes, for a joined player too. A
// command's replies are for its player alone, and the line itself is said to no one.
internal static partial class ChatCommands
{
    private static readonly string[] Names = { "give", "help", "spawn", "time", "tp", "weather" };

    // A line starting with / names a command: it runs here, goes to the host or is refused. Another line is said as usual.
    internal static bool TryRun(string text, Action<string> respond)
    {
        if (!IsCommand(text))
            return false;
        var args = Split(text.Trim());
        if (args.Count == 0)
            return false;
        string command = Normalize(args[0]);
        if (!Known(command))
            return false;
        if (!FeatureSwitches.Cheats)
        {
            respond("Your cheats are off. Turn them on in the Mods window.");
            return true;
        }
        // Help only reads the commands, so it answers without the host's cheats.
        if (command == "help")
        {
            Help(args, respond);
            return true;
        }
        var session = CoopSession.Current;
        if (session == null || !session.IsHost && !session.Settings.Cheats)
        {
            respond("Cheats are off. The host can turn them on in the campaign's host settings.");
            return true;
        }
        if (!InGame())
        {
            respond("Cheats work in a running game.");
            return true;
        }
        // The shared world is the host's to change.
        if (HostsCommand(command) && !session.IsHost)
        {
            CoopSession.RunOnHost(text.Trim());
            return true;
        }
        Run(command, args, respond, session.LocalSlot);
        return true;
    }

    // Host: a joined player's command for the shared world, run here, with its replies for them.
    internal static void RunFor(int slot, string text, Action<string> respond)
    {
        var args = Split(text ?? string.Empty);
        string command = args.Count > 0 ? Normalize(args[0]) : string.Empty;
        var session = CoopSession.Current;
        if (!HostsCommand(command) || session == null || !session.IsHost)
            return;
        if (!session.Settings.Cheats)
        {
            respond("The host has cheats off.");
            return;
        }
        if (!InGame())
        {
            respond("The host's game is not running.");
            return;
        }
        Run(command, args, respond, slot);
    }

    private static void Run(string command, List<string> args, Action<string> respond, int slot)
    {
        try
        {
            switch (command)
            {
                case "give":
                    Give(args, respond);
                    break;
                case "spawn":
                    Spawn(args, respond);
                    break;
                case "time":
                    SetTime(args, respond, slot);
                    break;
                case "tp":
                    Teleport(args, respond);
                    break;
                case "weather":
                    SetWeather(args, respond, slot);
                    break;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Multiplayer] The command '{string.Join(" ", args)}' failed: {exception}");
            respond("The command failed: " + exception.Message);
        }
    }

    private static bool HostsCommand(string command) => command == "time" || command == "weather";

    private static bool Known(string command) => Array.IndexOf(Names, command) >= 0;

    private static bool IsCommand(string text) => !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("/", StringComparison.Ordinal);

    // A line the suggestions follow: a command's, while this player's cheats are on.
    internal static bool Suggests(string text) => FeatureSwitches.Cheats && IsCommand(text);

    // This game plays the session's campaign, its keeper in the world.
    private static bool InGame() =>
        MainGame.Instance != null && MainGame.Instance.gameState == MainGame.GameState.InGame && MainGame.PlayerController != null &&
        KeeperSpawn.Active && !LazyUI.Get<UILoadingOverlay>().IsShown;

    // The player a name means, among those in the game, and whose keeper this game shows: by their name, or as keeper and
    // their slot, such as keeper2, where two players share a name.
    private static int FindPlayer(string name)
    {
        var session = CoopSession.Current;
        if (session == null || string.IsNullOrEmpty(name))
            return 0;
        int found = 0;
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
        {
            string player = session.PlayerName(slot);
            if (player == null || !session.IsPlaying(slot))
                continue;
            if (string.Equals(name, "keeper" + slot, StringComparison.OrdinalIgnoreCase))
                return slot;
            if (string.Equals(name, player, StringComparison.OrdinalIgnoreCase) && found == 0)
                found = slot;
        }
        return found;
    }

    // The names a command can take for the players in the game: each name once, and keeper and their slot for players
    // sharing one.
    private static string[] PlayerNames()
    {
        var session = CoopSession.Current;
        var names = new List<string>();
        if (session == null)
            return names.ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
        {
            string player = session.PlayerName(slot);
            if (player != null && session.IsPlaying(slot))
                counts[player] = counts.TryGetValue(player, out int count) ? count + 1 : 1;
        }
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
        {
            string player = session.PlayerName(slot);
            if (player == null || !session.IsPlaying(slot))
                continue;
            if (counts[player] > 1)
                names.Add("keeper" + slot);
            else if (Completable(player))
                names.Add(player);
        }
        return names.ToArray();
    }

    // Arguments split at spaces, as GYK1's: quotes keep spaces in one.
    private static List<string> Split(string input)
    {
        var result = new List<string>();
        bool quoted = false;
        var current = new StringBuilder();
        foreach (char c in input)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                    result.Add(current.ToString());
                current.Length = 0;
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static string Normalize(string token) => (token ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();

    private static bool IsWord(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFloat(string token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string token, out int value) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
