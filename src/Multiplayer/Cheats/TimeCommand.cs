using System;
using System.Collections.Generic;
using System.Globalization;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// /time: the shared day and its time, as GYK1's. The host's game sets them, and its clock tells everyone at once.
internal static partial class ChatCommands
{
    private static readonly AccessTools.FieldRef<EnvironmentData, int> DayOf = AccessTools.FieldRefAccess<EnvironmentData, int>("day");
    // The times a word names in the game's day, midnight at 0 and noon at 0.5: its daylight runs from 0.25 to 0.75.
    private static readonly Dictionary<string, float> TimeWords = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
    {
        ["midnight"] = 0f, ["dawn"] = 0.25f, ["morning"] = 0.3f, ["day"] = 0.4f, ["daytime"] = 0.4f, ["noon"] = 0.5f,
        ["evening"] = 0.75f, ["dusk"] = 0.75f, ["night"] = 0.85f
    };

    private static void SetTime(List<string> args, Action<string> respond, int slot)
    {
        var engine = EnvironmentEngine.Instance;
        if (args.Count == 1 || IsWord(args[1], "status"))
        {
            respond($"It is day {engine.Data.Day}, {Clock(engine.Data.TimeOfDay)}.");
            return;
        }
        if (!IsWord(args[1], "set"))
        {
            respond("Usage: /time set <time>, /time set <day> <time>, /time status");
            return;
        }
        if (!TryReadTime(args, out int? day, out float time, out string error))
        {
            respond(error);
            return;
        }
        if (day.HasValue)
            DayOf(engine.Data) = Mathf.Max(1, day.Value);
        engine.SetTimeOfDay(time);
        WorldClock.ShareNow();
        Debug.Log($"[Multiplayer] Keeper {slot}'s /time set day {engine.Data.Day}, {Clock(time)}");
        respond($"Set the time to day {engine.Data.Day}, {Clock(time)}.");
    }

    private static bool TryReadTime(List<string> args, out int? day, out float time, out string error)
    {
        day = null;
        time = EnvironmentEngine.Instance.Data.TimeOfDay;
        error = null;
        int first = 2;
        if (args.Count <= first)
        {
            error = "Usage: /time set <time>, /time set <day> <time>";
            return false;
        }
        if (IsWord(args[first], "day") && args.Count > first + 1 && TryParseInt(args[first + 1], out int named))
        {
            day = named;
            first += 2;
            if (args.Count <= first)
                return true;
        }
        else if (args.Count > first + 1 && TryParseInt(args[first], out int leading))
        {
            day = leading;
            first++;
        }
        if (!TryReadClock(args[first], out time))
        {
            error = "Unknown time. Use night, dawn, morning, day, noon, evening, HH:mm, an hour 0-24 or 0-1.";
            return false;
        }
        for (int i = first + 1; i + 1 < args.Count; i++)
        {
            if (IsWord(args[i], "day") && TryParseInt(args[i + 1], out int trailing))
            {
                day = trailing;
                i++;
            }
        }
        return true;
    }

    private static bool TryReadClock(string token, out float time)
    {
        time = 0f;
        if (string.IsNullOrWhiteSpace(token))
            return false;
        string word = token.Trim();
        if (TimeWords.TryGetValue(word, out time))
            return true;
        if (word.Contains(":"))
        {
            string[] parts = word.Split(':');
            if (parts.Length != 2 || !TryParseInt(parts[0], out int hour) || !TryParseInt(parts[1], out int minute) ||
                hour < 0 || hour > 24 || minute < 0 || minute > 59 || hour == 24 && minute != 0)
                return false;
            time = Mathf.Clamp01((hour + minute / 60f) / 24f);
            return true;
        }
        if (!TryParseFloat(word, out float number) || number < 0f || number > 24f)
            return false;
        // A fraction of the day, or an hour.
        time = number <= 1f ? number : Mathf.Clamp01(number / 24f);
        return true;
    }

    private static string Clock(float time)
    {
        float hours = Mathf.Clamp01(time) * 24f;
        int hour = Mathf.FloorToInt(hours), minute = Mathf.RoundToInt((hours - hour) * 60f);
        if (minute >= 60)
        {
            minute -= 60;
            hour++;
        }
        return $"{hour % 24:00}:{minute:00} ({time.ToString("0.###", CultureInfo.InvariantCulture)})";
    }

    private static string[] CompleteTime(Typing typing)
    {
        if (typing.Index == 1)
            return MatchPrefix(typing.Partial, new[] { "set", "status" });
        if (typing.Index == 2 && IsWord(typing.Args[1], "set"))
            return MatchPrefix(typing.Partial, new[] { "dawn", "morning", "day", "noon", "evening", "night", "midnight" });
        return null;
    }
}
