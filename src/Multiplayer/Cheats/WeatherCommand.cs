using System;
using System.Collections.Generic;
using System.Linq;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;
using NodeCanvas.StateMachines;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// /weather: the shared weather, as GYK1's, in the game's own terms: GYK2's weather moves between named states on its own,
// which a command can hold or let go. The host's game sets it, and its clock tells everyone at once.
internal static partial class ChatCommands
{
    private static readonly AccessTools.FieldRef<WeatherSystem, FSMOwner> WeatherStates =
        AccessTools.FieldRefAccess<WeatherSystem, FSMOwner>("fsmOwner");
    private static string[] weatherNames;
    private static WeatherSystem named;

    private static void SetWeather(List<string> args, Action<string> respond, int slot)
    {
        var weather = WeatherSystem.Instance;
        var data = MainGame.Instance.GameSave.weatherData;
        if (args.Count == 1 || IsWord(args[1], "status"))
        {
            respond($"The weather is {data.stateName}" + (data.hasForceState ? ", held." : ", changing on its own."));
            return;
        }
        if (IsWord(args[1], "clear"))
        {
            weather.SetWeatherState(WeatherSystem.CLEAN_WEATHER_NAME, force: true);
            Changed(slot, respond, "Holding clean weather.");
            return;
        }
        if (IsWord(args[1], "reset"))
        {
            weather.ResetWeatherState();
            Changed(slot, respond, "The weather is clean and changes on its own again.");
            return;
        }
        if (!IsWord(args[1], "set") || args.Count < 3)
        {
            respond("Usage: /weather set <state>, /weather clear, /weather reset, /weather status");
            return;
        }
        string requested = string.Join(" ", args.GetRange(2, args.Count - 2));
        string state = WeatherNames().FirstOrDefault(name => IsWord(Latin(name), Latin(requested)));
        if (state == null)
        {
            respond($"No weather state {requested}. The game's: {string.Join(", ", WeatherNames())}.");
            return;
        }
        weather.SetWeatherState(state, force: true);
        Changed(slot, respond, $"Holding the weather {state}.");
    }

    private static void Changed(int slot, Action<string> respond, string reply)
    {
        WorldClock.ShareNow();
        Debug.Log($"[Multiplayer] Keeper {slot}'s /weather: {MainGame.Instance.GameSave.weatherData.stateName}");
        respond(reply);
    }

    // The weather states of the game's weather machine, by name.
    private static string[] WeatherNames()
    {
        var weather = WeatherSystem.Instance;
        if (weather == null)
            return Array.Empty<string>();
        if (named == weather && weatherNames != null)
            return weatherNames;
        var machine = WeatherStates(weather)?.GetCurrentState()?.FSM;
        weatherNames = machine == null ? Array.Empty<string>() : machine.allNodes.OfType<FSMWeatherState>()
            .Where(state => state.weather != null).Select(state => state.name).Where(Completable).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        named = weather;
        return weatherNames;
    }

    private static string[] CompleteWeather(Typing typing)
    {
        if (typing.Index == 1)
            return MatchPrefix(typing.Partial, new[] { "set", "clear", "reset", "status" });
        if (typing.Index == 2 && IsWord(typing.Args[1], "set"))
        {
            string typed = Latin(typing.Partial);
            var matches = WeatherNames().Where(name => Latin(name).StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length > 0 ? matches : null;
        }
        return null;
    }

    // Some of the game's weather states are spelled with a Cyrillic letter where it looks Latin, as МistWeakLight; a
    // name typed with the Latin letter means it.
    private static string Latin(string name)
    {
        const string cyrillic = "АВЕКМНОРСТХаеорсух", latin = "ABEKMHOPCTXaeopcyx";
        var letters = (name ?? string.Empty).ToCharArray();
        for (int i = 0; i < letters.Length; i++)
        {
            int at = cyrillic.IndexOf(letters[i]);
            if (at >= 0)
                letters[i] = latin[at];
        }
        return new string(letters);
    }
}
