using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Progression;

// The game keeps part of the campaign's state on the keeper rather than the world: whether a sermon
// is due, the battle ready, the order board up and the resurrection powered; how far the church,
// garden and vineyard are upgraded and which basement blockages are cleared; how many bodies and
// zombies there are and how the donkey and the nun bring them; and the story's own counters and
// choices. Each game kept its own copy, so a change reached only the game that made it, the host's
// for everything its world runs: a joined keeper could never preach, and the church's next upgrade
// stayed locked when its building and its technology came from different keepers. These values now
// belong to the campaign: every change reaches every keeper, and a joining keeper takes on the
// campaign's values. They are not rewards, so none is handed out per character.
[HarmonyPatch]
internal static class CampaignValues
{
    private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        // Readiness the schedule and the story switch.
        "sermon_ready", "battle_ready", "chalk_board_enabled", "resurrection_has_power", "panic_reduction_duration",
        "milestones_activated", "zombie_conveyor_transporter_ready",
        // The church's upgrades.
        "church_better", "church_best",
        // Bodies and zombies.
        "cur_bodies_count", "cur_zombies_count", "bodies_tier", "zombies_limit_mechanic", "donkey_ready",
        "donkey_body_drop_chance", "nun_body_drop_ready", "nun_many_body_drop", "global_lock_on_drop_bodies",
        // Sermons.
        "last_sermon_with_choir_activity", "last_sermon_with_organ_activity", "old_god_ceremony", "old_god_start_asking",
        // The story's counters and choices.
        "clearing_A", "elections_ready", "fragments_portal", "dark_final_battle", "help_portal", "help_portal_not",
        "red_eye_keep", "astrologer_project", "2_intro_scout_cross_the_corner_in_progress"
    };
    // The garden's and vineyard's levels, and the basement's blockages.
    private static readonly string[] Families = { "g_garden_", "g_vineyard_", "basement_blockage_" };
    // An expression's own change to a keeper's value, as the game parses it.
    private static readonly Regex Change = new Regex("^\\s*(?:Add|Set|Dec|Multiply|Divide)PPar\\(\\s*\"([^\"]+)\"");
    private static readonly AccessTools.FieldRef<PlayerData, GameRes> Resources =
        AccessTools.FieldRefAccess<PlayerData, GameRes>("res");

    internal static bool Shared(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (Names.Contains(name))
            return true;
        foreach (string family in Families)
        {
            if (name.StartsWith(family, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Whether a parsed expression changes one of these values.
    internal static bool Changes(string expression) =>
        !string.IsNullOrEmpty(expression) && Change.Match(expression) is { Success: true } match && Shared(match.Groups[1].Value);

    internal static void Apply(BinaryReader reader)
    {
        var player = MainGame.PlayerData;
        for (int count = reader.ReadInt32(); count > 0; count--)
        {
            string name = reader.ReadString();
            float value = reader.ReadSingle();
            if (Shared(name))
                player.SetRes(name, value);
        }
    }

    // Host: a joining character takes on the campaign's values, the host's own.
    internal static void Copy(PlayerData character)
    {
        var campaign = Resources(MainGame.PlayerData);
        var own = Resources(character);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var atom in campaign.List.Concat(own.List))
        {
            if (Shared(atom.type))
                names.Add(atom.type);
        }
        foreach (string name in names)
            own.SetWithoutSystemsCheck(name, campaign.Get(name));
    }

    [HarmonyPatch]
    private static class Changed
    {
        private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(PlayerData))
            .Where(method => method.Name is "AddRes" or "SetRes" or "SubRes" or "MultiplyRes" or "AddResWithoutSystemsCheck" or
                "SetResWithoutSystemsCheck" or "AddResWithoutGlobalChangeEvent");

        // A change travels as the value it leaves, as the world's own values do. A keeper's own values change
        // all the time, energy with every stroke, so only a shared one costs anything.
        private static void Postfix(PlayerData __instance, object __0)
        {
            if (!WorldSync.Sharing || __instance != MainGame.PlayerData)
                return;
            List<(string name, float value)> values = null;
            if (__0 is string name)
                Add(ref values, __instance, name);
            else if (__0 is GameRes resources)
            {
                foreach (var atom in resources.List)
                    Add(ref values, __instance, atom.type);
            }
            if (values == null)
                return;
            WorldSync.Queue(WorldSync.Change.CampaignValue, Guid.Empty, writer =>
            {
                writer.Write(values.Count);
                foreach (var (shared, value) in values)
                {
                    writer.Write(shared);
                    writer.Write(value);
                }
            });
        }

        private static void Add(ref List<(string name, float value)> values, PlayerData player, string name)
        {
            if (!Shared(name) || values != null && values.Exists(value => value.name == name))
                return;
            (values ??= new List<(string, float)>()).Add((name, player.GetRes(name)));
        }
    }
}
