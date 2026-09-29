using System;
using System.Collections.Generic;
using GYK2.TombManyKeepers.Multiplayer.Players;

namespace GYK2.TombManyKeepers.Multiplayer.Session;

// What the players say in the lobby, in the order the host relays it, with the host's notices of who came
// and went; the lobby shows the latest lines, each name in its player's colour.
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
        // The speaker's colour as they spoke, or none.
        internal readonly int Color;

        internal Line(int slot, string name, string text, int color)
        {
            Slot = slot;
            Name = name;
            Text = text;
            Color = color;
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

    internal static void Add(int slot, string name, string text, int color = PlayerColors.None)
    {
        lines.Add(new Line(slot, name, text, color));
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
