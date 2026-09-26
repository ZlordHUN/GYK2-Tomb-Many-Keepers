using System;
using System.Collections.Generic;

namespace GYK2.TombManyKeepers.Multiplayer.Session;

// What the players say in the lobby, in the order the host relays it, with the host's notices of who came
// and went; the lobby shows the latest lines.
internal static class LobbyChat
{
    internal const int MaxLength = 120;
    private const int Kept = 100;
    private static readonly List<Line> lines = new List<Line>();

    internal readonly struct Line
    {
        internal readonly int Slot;
        // No name for the host's notices.
        internal readonly string Name;
        internal readonly string Text;

        internal Line(int slot, string name, string text)
        {
            Slot = slot;
            Name = name;
            Text = text;
        }
    }

    internal static event Action Changed;

    internal static IReadOnlyList<Line> Lines => lines;

    // One line, trimmed to what the lobby shows.
    internal static string Clean(string text)
    {
        if (text == null)
            return string.Empty;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
    }

    internal static void Add(int slot, string name, string text)
    {
        lines.Add(new Line(slot, name, text));
        if (lines.Count > Kept)
            lines.RemoveAt(0);
        Changed?.Invoke();
    }

    internal static void Clear()
    {
        if (lines.Count == 0)
            return;
        lines.Clear();
        Changed?.Invoke();
    }
}
