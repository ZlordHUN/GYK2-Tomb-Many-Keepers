using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// The items /give knows, built from the game's own item definitions as GYK1's catalog was, so every item the game has
// is there: each by its id and by a name made from what the game calls it in this player's language, the quality of a
// starred item in its name. A name several items share asks for one of theirs.
internal static class ItemCatalog
{
    private sealed class Entry
    {
        internal ItemDef Definition;
        internal string Id;
        internal string Name;
        // The name with its quality, as the list shows it.
        internal string Shown;
        internal string Base;
        // The one name that always means this item, which Tab types.
        internal string Alias;
        internal string Label;
        internal readonly List<string> Aliases = new List<string>();
    }

    // Money and energy are a player's own values, not items.
    internal static readonly string[] Personal = { "energy", "money" };
    private static List<ItemDef> builtFrom;
    private static int builtCount = -1;
    private static string builtLanguage;
    private static Entry[] entries = Array.Empty<Entry>();
    private static Dictionary<string, Entry> byId = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, List<Entry>> byAlias = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

    internal static bool TryResolve(string nameOrId, out string id, out string name, out string error)
    {
        Build();
        id = name = error = null;
        string requested = (nameOrId ?? string.Empty).Trim();
        if (requested.Length == 0)
        {
            error = "Name an item.";
            return false;
        }
        if (byId.TryGetValue(requested, out var exact))
        {
            id = exact.Id;
            name = exact.Shown;
            return true;
        }
        if (!byAlias.TryGetValue(Normalize(requested), out var matches) || matches.Count == 0)
        {
            error = $"No item or item name {nameOrId}.";
            return false;
        }
        if (matches.Count > 1)
        {
            var choices = new List<string>();
            foreach (var match in matches)
                choices.Add($"{match.Alias} [{match.Id}]");
            error = $"Several items are called {nameOrId}: {string.Join(", ", choices)}.";
            return false;
        }
        id = matches[0].Id;
        name = matches[0].Shown;
        return true;
    }

    // The game has this item.
    internal static bool Exists(string id)
    {
        Build();
        return !string.IsNullOrEmpty(id) && byId.ContainsKey(id);
    }

    // What the item is called, with its quality, or its id.
    internal static string NameOf(string id)
    {
        Build();
        return id != null && byId.TryGetValue(id, out var entry) ? entry.Shown : id;
    }

    // A word /give takes as an item, even one several items share, so a player of that name does not change what it means.
    internal static bool Recognizes(string token)
    {
        Build();
        string requested = (token ?? string.Empty).Trim();
        return requested.Length > 0 && (Array.IndexOf(Personal, requested.ToLowerInvariant()) >= 0 || byId.ContainsKey(requested) ||
            byAlias.ContainsKey(Normalize(requested)));
    }

    // The items the letters typed so far begin, by id, by the game's name or by an alias: the id when the player began
    // one, and otherwise the name that always means the item. Money and energy first, for this player's own.
    internal static string[] Candidates(string partial, bool personal)
    {
        Build();
        string raw = (partial ?? string.Empty).Trim().Trim('"');
        string normalized = Normalize(raw);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (personal)
        {
            foreach (string value in Personal)
            {
                if ((raw.Length == 0 || value.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) && seen.Add(value))
                    result.Add(value);
            }
        }
        foreach (var entry in entries)
        {
            bool byTheId = entry.Id.StartsWith(raw, StringComparison.OrdinalIgnoreCase);
            if (!byTheId && !Named(entry, raw, normalized))
                continue;
            string completion = byTheId && raw.Length > 0 ? entry.Id : entry.Alias;
            if (seen.Add(completion))
                result.Add(completion);
        }
        return result.ToArray();
    }

    // How the list shows a completion: the item's name and quality, its alias and its id.
    internal static string Label(string candidate)
    {
        Build();
        if (string.Equals(candidate, "energy", StringComparison.OrdinalIgnoreCase))
            return "Energy — your energy";
        if (string.Equals(candidate, "money", StringComparison.OrdinalIgnoreCase))
            return "Money — your money";
        if (candidate != null && byId.TryGetValue(candidate, out var exact))
            return exact.Label;
        return byAlias.TryGetValue(Normalize(candidate), out var matches) && matches.Count == 1 ? matches[0].Label : null;
    }

    private static bool Named(Entry entry, string raw, string normalized)
    {
        if (raw.Length == 0 || entry.Name.StartsWith(raw, StringComparison.OrdinalIgnoreCase) ||
            entry.Shown.StartsWith(raw, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (string alias in entry.Aliases)
        {
            if (alias.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Built again as the game's items or this player's language change.
    private static void Build()
    {
        var definitions = GameBalance.Me?.itemDefs;
        int count = definitions?.Count ?? 0;
        string language = LLBase.CurrentLang ?? string.Empty;
        if (ReferenceEquals(definitions, builtFrom) && count == builtCount && language == builtLanguage)
            return;
        builtFrom = definitions;
        builtCount = count;
        builtLanguage = language;
        var found = new List<Entry>();
        var ids = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (definitions != null)
        {
            foreach (var definition in definitions)
            {
                string id = definition?.id;
                if (string.IsNullOrWhiteSpace(id) || id == ItemDef.EMPTY_ITEM_ID || ids.ContainsKey(id))
                    continue;
                string name = NameIn(definition);
                string alias = Normalize(name);
                var entry = new Entry { Definition = definition, Id = id, Name = name, Base = alias.Length > 0 ? alias : Normalize(id) };
                found.Add(entry);
                ids.Add(id, entry);
            }
        }
        found.Sort((left, right) =>
        {
            int byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return byName != 0 ? byName : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
        });
        var groups = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in found)
        {
            if (!groups.TryGetValue(entry.Base, out var group))
                groups.Add(entry.Base, group = new List<Entry>());
            group.Add(entry);
        }
        var aliases = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>(groups.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            var group = groups[key];
            group.Sort((left, right) =>
            {
                int byQuality = left.Definition.quality.CompareTo(right.Definition.quality);
                return byQuality != 0 ? byQuality : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
            });
            bool family = group.Count > 1 && group.TrueForAll(entry => Quality(entry.Definition).Length > 0);
            foreach (var entry in group)
            {
                string quality = Quality(entry.Definition);
                string proposed;
                if (quality.Length > 0)
                {
                    proposed = quality + "_" + entry.Base;
                    int stars = entry.Definition.quality;
                    entry.Shown = $"{entry.Name} ({stars} star{(stars == 1 ? string.Empty : "s")})";
                }
                else
                {
                    proposed = group.Count == 1 ? entry.Base : entry.Base + "_" + Normalize(entry.Id);
                    entry.Shown = entry.Name;
                }
                entry.Alias = Unique(proposed, entry, owners, aliases, ids);
                entry.Label = $"{entry.Shown} — {entry.Alias} [{entry.Id}]";
                Register(aliases, entry.Alias, entry, owners);
                Register(aliases, Normalize(entry.Id), entry, owners);
                Remember(entry, entry.Base);
                if (quality.Length > 0)
                    Register(aliases, entry.Base + "_" + quality, entry, owners);
            }
            // One item, or one item's qualities, goes by its plain name too, the lowest quality first; items that only
            // share a name keep it, so asking for it lists them.
            if (group.Count == 1 || family)
                Register(aliases, group[0].Base, group[0], owners);
            else
            {
                foreach (var entry in group)
                    Register(aliases, entry.Base, entry, owners);
            }
        }
        entries = found.ToArray();
        byId = ids;
        byAlias = aliases;
    }

    private static void Register(Dictionary<string, List<Entry>> aliases, string alias, Entry entry, Dictionary<string, Entry> owners)
    {
        alias = Normalize(alias);
        if (alias.Length == 0 || Array.IndexOf(Personal, alias) >= 0 || owners.TryGetValue(alias, out var owner) && owner != entry)
            return;
        if (!aliases.TryGetValue(alias, out var matches))
            aliases.Add(alias, matches = new List<Entry>());
        if (!matches.Contains(entry))
            matches.Add(entry);
        Remember(entry, alias);
    }

    private static void Remember(Entry entry, string alias)
    {
        alias = Normalize(alias);
        if (alias.Length > 0 && !entry.Aliases.Contains(alias))
            entry.Aliases.Add(alias);
    }

    // The proposed name, or one made longer with the id until no other item or id has it.
    private static string Unique(string proposed, Entry entry, Dictionary<string, Entry> owners, Dictionary<string, List<Entry>> aliases,
        Dictionary<string, Entry> ids)
    {
        string candidate = Normalize(proposed);
        if (candidate.Length == 0)
            candidate = Normalize(entry.Id);
        foreach (string next in new[] { candidate, candidate + "_" + Normalize(entry.Id) })
        {
            if (Free(next, entry, owners, aliases, ids))
            {
                owners[next] = entry;
                return next;
            }
        }
        string hashed = candidate + "_" + Normalize(entry.Id) + "_" + Hash(entry.Id).ToString("x8", CultureInfo.InvariantCulture);
        owners[hashed] = entry;
        return hashed;
    }

    private static bool Free(string alias, Entry entry, Dictionary<string, Entry> owners, Dictionary<string, List<Entry>> aliases,
        Dictionary<string, Entry> ids)
    {
        if (Array.IndexOf(Personal, alias) >= 0 || ids.TryGetValue(alias, out var exact) && exact != entry ||
            owners.TryGetValue(alias, out var owner) && owner != entry)
            return false;
        return !aliases.TryGetValue(alias, out var matches) || matches.TrueForAll(match => match == entry);
    }

    // A starred item's quality in its alias, such as 2star.
    private static string Quality(ItemDef definition) =>
        definition.qualityType == ItemDef.QualityType.Star && definition.quality > 0 ? definition.quality + "star" : string.Empty;

    // What the game calls the item in this player's language, without its markup; its id in words where it has no name.
    private static string NameIn(ItemDef definition)
    {
        string name = Plain(LLBase.L(definition.id));
        return name.Length == 0 || string.Equals(name, definition.id, StringComparison.OrdinalIgnoreCase) ? Words(definition.id) : name;
    }

    private static string Plain(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var result = new StringBuilder(value.Length);
        bool tag = false, space = false;
        foreach (char c in value)
        {
            if (c == '[' || c == '<')
            {
                tag = true;
                continue;
            }
            if (tag)
            {
                if (c == ']' || c == '>')
                    tag = false;
                continue;
            }
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = result.Length > 0;
                continue;
            }
            if (space)
            {
                result.Append(' ');
                space = false;
            }
            result.Append(c);
        }
        return result.ToString().Trim();
    }

    private static string Words(string id)
    {
        var result = new StringBuilder(id.Length);
        bool start = true;
        foreach (char c in id)
        {
            if (!char.IsLetterOrDigit(c))
            {
                if (result.Length > 0 && result[result.Length - 1] != ' ')
                    result.Append(' ');
                start = true;
                continue;
            }
            result.Append(start ? char.ToUpperInvariant(c) : c);
            start = false;
        }
        return result.ToString().Trim();
    }

    // Lower-case letters and digits joined by underscores, so a name reads as one argument.
    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var result = new StringBuilder(value.Length);
        bool gap = false;
        foreach (char c in value.Trim().Trim('"').ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (gap && result.Length > 0)
                    result.Append('_');
                result.Append(c);
                gap = false;
            }
            else
                gap = true;
        }
        return result.ToString();
    }

    private static uint Hash(string value)
    {
        uint hash = 2166136261u;
        foreach (char c in value)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }
}
