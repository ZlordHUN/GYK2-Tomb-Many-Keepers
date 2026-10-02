using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Chat;

// The names of the game's people as the chat's NPCs tab shows them: ??? until the players learn one, as a line of a
// conversation says it or the game shows it by the character a keeper faces, and theirs from then on in every line,
// those before too. The host learns names for everyone, and each is kept with the campaign's save.
internal static class NpcNames
{
    internal const string Unknown = "???";
    // A world resource per character, which the campaign's save keeps and every game shares.
    private const string Mark = "tmk_named:";
    // Where the game gives a character no name of its own: the fairy calls herself Soul, and the skull in the prison's
    // sarcophagus is the game's Larry.
    private static readonly Dictionary<string, string> Told = new Dictionary<string, string> { ["player_wisp"] = "Soul" };
    private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string> { ["intro_prison_sarcophagus"] = "npc_larry" };
    // Names learned in this session, before or without the campaign's world, as in the lobby.
    private static readonly HashSet<string> learned = new HashSet<string>();

    internal static event Action Changed;

    internal static bool Known(string npc) =>
        learned.Contains(npc) || MainGame.WorldData != null && MainGame.WorldData.GetGameRes(Mark + npc) > 0f;

    // The character's name once learned, or ???.
    internal static string Shown(string npc) => Known(npc) ? NameOf(npc) ?? Unknown : Unknown;

    // The name a character goes by, learned or not; none for one the game never names.
    private static string NameOf(string npc)
    {
        if (Told.TryGetValue(npc, out string told))
            return told;
        string key = Aliases.TryGetValue(npc, out string alias) ? alias : npc;
        string name = LLBase.L(key);
        return string.IsNullOrEmpty(name) || name == key ? null : name;
    }

    // Host: a line learns the names it says of the characters in the log, and of its own speaker.
    internal static void Heard(string speaker, string text, Action<string> learn)
    {
        var said = new HashSet<string>();
        if (speaker != null)
            said.Add(speaker);
        foreach (var line in ChatLog.Lines)
        {
            if (line.Npc != null)
                said.Add(line.Npc);
        }
        foreach (string npc in said)
        {
            string name = Known(npc) ? null : NameOf(npc);
            if (name != null && Regex.IsMatch(text, @"(?<!\w)" + Regex.Escape(name) + @"(?!\w)"))
                learn(npc);
        }
    }

    // A name learned, shown in every line; the host keeps it with the campaign it plays, which shares it with every game.
    internal static bool Learn(string npc, bool keep)
    {
        if (string.IsNullOrEmpty(npc) || Known(npc) || NameOf(npc) == null)
            return false;
        learned.Add(npc);
        if (keep && MainGame.WorldData != null && MainGame.Instance.gameState == MainGame.GameState.InGame)
            MainGame.WorldData.SetGameRes(Mark + npc, 1f);
        Changed?.Invoke();
        return true;
    }

    // Host: the names learned among the characters of the log a player arriving reads.
    internal static void WriteKnown(BinaryWriter writer)
    {
        var known = new List<string>();
        foreach (var line in ChatLog.Lines)
        {
            if (line.Npc != null && line.Shared && Known(line.Npc) && !known.Contains(line.Npc))
                known.Add(line.Npc);
        }
        writer.Write((ushort)known.Count);
        foreach (string npc in known)
            writer.Write(npc);
    }

    internal static void ReadKnown(BinaryReader reader)
    {
        for (int count = reader.ReadUInt16(); count > 0; count--)
            learned.Add(reader.ReadString());
        Changed?.Invoke();
    }

    internal static void Clear() => learned.Clear();
}
