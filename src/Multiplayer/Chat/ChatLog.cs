using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;

namespace GYK2.TombManyKeepers.Multiplayer.Chat;

// Everything said in the session, the one log both the lobby's chat and the game's show, as GYK1's chat kept it: the
// players' lines in the order the host relays them and the host's notices of who came and went, which everyone has,
// and this game's own notices, all under the chat's Players tab; and under its NPCs tab every line of a conversation
// or cutscene in any player's game, which everyone has too. A player arriving reads from the host what everyone has,
// ahead of their own notices.
internal static class ChatLog
{
    internal const int MaxLength = 120;
    // Each tab keeps its latest lines, so a long cutscene lets go of no player's line.
    private const int Kept = 100;
    // How long ago a line a player arriving reads can have been said.
    private const float OldestAge = 7f * 24f * 60f * 60f;
    private static readonly List<Line> lines = new List<Line>();
    // How many lines each tab has had, counting those let go since, so a line arriving shows as new even in a full log.
    private static readonly int[] totals = new int[ChatTabs.Names.Length];

    internal readonly struct Line
    {
        internal readonly int Slot;
        // No name for a notice or a character's line.
        internal readonly string Name;
        // A player's line as typed; a conversation's in the game's own words, shown in each player's language.
        internal readonly string Text;
        // The speaker's colour as they spoke, or none.
        internal readonly int Color;
        // When it was said, by this computer's clock.
        internal readonly DateTime Time;
        // Everyone has it, not only this game.
        internal readonly bool Shared;
        // The chat's tab that shows it.
        internal readonly ChatTabs.Tab Tab;
        // A character's line of a conversation: who said it, by the game's id.
        internal readonly string Npc;

        internal Line(int slot, string name, string text, int color, DateTime time, bool shared,
            ChatTabs.Tab tab = ChatTabs.Tab.Players, string npc = null)
        {
            Slot = slot;
            Name = name;
            Text = text;
            Color = color;
            Time = time;
            Shared = shared;
            Tab = tab;
            Npc = npc;
        }
    }

    internal static event Action Changed;

    internal static IReadOnlyList<Line> Lines => lines;

    internal static int Said(ChatTabs.Tab tab) => totals[(int)tab];

    // One line, trimmed to what the chat takes.
    internal static string Clean(string text)
    {
        if (text == null)
            return string.Empty;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
    }

    // A line everyone has: a player's, or the host's notice.
    internal static void Add(int slot, string name, string text, int color) =>
        Keep(new Line(slot, name, text, color, DateTime.Now, shared: true));

    // A notice for this game's player alone.
    internal static void Notice(string text) => Keep(new Line(0, null, text, PlayerColors.None, DateTime.Now, shared: false));

    // A line of a conversation or cutscene, which everyone has: a character's, or a keeper's with their player's name
    // and colour.
    internal static void Spoken(int slot, string name, string npc, string text, int color) =>
        Keep(new Line(slot, name, text, color, DateTime.Now, shared: true, ChatTabs.Tab.Npcs, npc));

    // Host: what everyone has, for a player arriving, with how long ago each line was said.
    internal static void WriteShared(BinaryWriter writer)
    {
        var now = DateTime.Now;
        int count = 0;
        foreach (var line in lines)
        {
            if (line.Shared)
                count++;
        }
        writer.Write((ushort)count);
        foreach (var line in lines)
        {
            if (!line.Shared)
                continue;
            writer.Write((byte)line.Slot);
            writer.Write(line.Name ?? string.Empty);
            writer.Write(line.Text);
            writer.Write((sbyte)line.Color);
            writer.Write((float)Math.Max(0.0, (now - line.Time).TotalSeconds));
            writer.Write((byte)line.Tab);
            writer.Write(line.Npc ?? string.Empty);
        }
    }

    // A player arriving: what everyone had said before comes ahead of this game's own notices.
    internal static void Recall(BinaryReader reader)
    {
        var now = DateTime.Now;
        int count = reader.ReadUInt16();
        var said = new List<Line>(count);
        for (int i = 0; i < count; i++)
        {
            int slot = reader.ReadByte();
            string name = reader.ReadString();
            string text = Clean(reader.ReadString());
            int color = reader.ReadSByte();
            float age = reader.ReadSingle();
            var tab = reader.ReadByte() == (byte)ChatTabs.Tab.Npcs ? ChatTabs.Tab.Npcs : ChatTabs.Tab.Players;
            string npc = reader.ReadString();
            age = age >= 0f ? Math.Min(age, OldestAge) : 0f;
            said.Add(new Line(slot, slot == 0 ? null : name, text, PlayerColors.Valid(color) ? color : PlayerColors.None,
                now - TimeSpan.FromSeconds(age), shared: true, tab, tab == ChatTabs.Tab.Npcs && npc.Length > 0 ? npc : null));
        }
        lines.InsertRange(0, said);
        foreach (var line in said)
            totals[(int)line.Tab]++;
        Trim();
        Changed?.Invoke();
    }

    internal static void Clear()
    {
        if (lines.Count == 0)
            return;
        lines.Clear();
        Changed?.Invoke();
    }

    private static void Keep(Line line)
    {
        lines.Add(line);
        totals[(int)line.Tab]++;
        Trim();
        Changed?.Invoke();
    }

    // Each tab lets go of its oldest lines past what it keeps.
    private static void Trim()
    {
        var counts = new int[ChatTabs.Names.Length];
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (++counts[(int)lines[i].Tab] > Kept)
                lines.RemoveAt(i);
        }
    }
}
