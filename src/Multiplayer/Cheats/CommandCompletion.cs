using System;
using System.Collections.Generic;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// GYK1's completions of a command being typed: the commands, their words, the players and the game's items, weather
// states and times of day. Each completion is the whole line as Tab would make it, and a label for the list.
internal static partial class ChatCommands
{
    internal sealed class Completion
    {
        internal readonly string Text;
        internal readonly string Label;

        internal Completion(string text, string label)
        {
            Text = text;
            Label = label;
        }
    }

    private sealed class Typing
    {
        internal readonly string Text;
        internal readonly List<string> Args;
        // The argument being typed, counting the command as 0, where it starts in the text, and its letters so far.
        internal readonly int Index;
        internal readonly int Start;
        internal readonly string Partial;

        internal Typing(string text, List<string> args, int index, int start, string partial)
        {
            Text = text;
            Args = args;
            Index = index;
            Start = start;
            Partial = partial;
        }
    }

    // What the line typed so far can become, in order; none for a line that is no command, or while this player's cheats
    // are off.
    internal static Completion[] Complete(string text)
    {
        if (!Suggests(text))
            return Array.Empty<Completion>();
        var typing = Read(text);
        if (typing.Args.Count == 0)
            return Array.Empty<Completion>();
        string[] candidates = Candidates(typing);
        if (candidates == null || candidates.Length == 0)
            return Array.Empty<Completion>();
        var completions = new List<Completion>(candidates.Length);
        foreach (string candidate in candidates)
        {
            string completed = Completed(typing, candidate);
            if (!string.Equals(text, completed, StringComparison.Ordinal))
                completions.Add(new Completion(completed, Label(typing, candidate)));
        }
        return completions.ToArray();
    }

    private static string[] Candidates(Typing typing)
    {
        string command = Normalize(typing.Args[0]);
        if (typing.Index == 0)
            return MatchPrefix(command, Names);
        switch (command)
        {
            case "help":
                return typing.Index == 1 ? MatchPrefix(typing.Partial, Names) : null;
            case "give":
                return CompleteGive(typing);
            case "spawn":
                return CompleteSpawn(typing);
            case "time":
                return CompleteTime(typing);
            case "tp":
                return typing.Index == 1 ? MatchPrefix(typing.Partial, PlayerNames()) : null;
            case "weather":
                return CompleteWeather(typing);
        }
        return null;
    }

    private static string Label(Typing typing, string candidate) =>
        Normalize(typing.Args[0]) == "give" && typing.Index > 0 ? ItemCatalog.Label(candidate) ?? candidate : candidate;

    private static Typing Read(string text)
    {
        var args = Split(text);
        if (text.Length > 0 && char.IsWhiteSpace(text[text.Length - 1]))
            args.Add(string.Empty);
        return new Typing(text, args, args.Count - 1, TokenStart(text), args.Count > 0 ? args[args.Count - 1] : string.Empty);
    }

    // Where the argument being typed starts, a quoted one at its quote.
    internal static int TokenStart(string text)
    {
        bool quoted = false, started = false;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (!started)
                {
                    start = i;
                    started = true;
                }
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                started = false;
                start = i + 1;
            }
            else if (!started)
            {
                start = i;
                started = true;
            }
        }
        return start;
    }

    // The line with the argument being typed completed, and a space for the next.
    private static string Completed(Typing typing, string candidate) =>
        typing.Text.Substring(0, typing.Start) + (typing.Index == 0 ? "/" + candidate : Quoted(candidate)) + " ";

    private static string Quoted(string value)
    {
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
                return "\"" + value + "\"";
        }
        return value;
    }

    private static string[] Merge(string[] first, string[] second)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidates in new[] { first, second })
        {
            if (candidates == null)
                continue;
            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                    result.Add(candidate);
            }
        }
        return result.ToArray();
    }

    private static string[] MatchPrefix(string prefix, string[] candidates)
    {
        if (candidates == null || candidates.Length == 0)
            return null;
        var matches = new List<string>();
        foreach (string candidate in candidates)
        {
            if (candidate != null && candidate.StartsWith(prefix ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
        }
        return matches.Count > 0 ? matches.ToArray() : null;
    }

    // A value the command line can hold: no quote and no control character.
    private static bool Completable(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('"') >= 0)
            return false;
        foreach (char c in value)
        {
            if (char.IsControl(c))
                return false;
        }
        return true;
    }
}
