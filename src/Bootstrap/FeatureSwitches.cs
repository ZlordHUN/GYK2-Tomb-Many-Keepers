using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace GYK2.TombManyKeepers;

// The mod's own settings, shown in its Mods window: whether its widescreen support, multiplayer and manual saves load.
// Each is on unless the player turns it off, and is switched as the game starts: a feature that is off has none of its
// patches applied, so it runs nothing and changes nothing, and the files and saves it made stay as they are for when
// it is on again. A feature is the code under its own folders.
internal static class FeatureSwitches
{
    private const string Section = "Features";
    private const string Restart = " Takes effect after a restart.";
    private static readonly (string space, Func<bool> on)[] Owners =
    {
        ("GYK2.TombManyKeepers.Patches.Widescreen", () => Widescreen),
        ("GYK2.TombManyKeepers.Features.Widescreen", () => Widescreen),
        ("GYK2.TombManyKeepers.Multiplayer", () => Multiplayer),
        ("GYK2.TombManyKeepers.Network", () => Multiplayer),
        ("GYK2.TombManyKeepers.UI.Multiplayer", () => Multiplayer),
        ("GYK2.TombManyKeepers.Features.ManualSaves", () => ManualSaves),
        ("GYK2.TombManyKeepers.UI.Saves", () => ManualSaves)
    };

    // Whether each feature loaded as the game started; changing its setting takes effect the next time.
    internal static bool Widescreen { get; private set; } = true;
    internal static bool Multiplayer { get; private set; } = true;
    internal static bool ManualSaves { get; private set; } = true;

    internal static void Bind(ConfigFile config)
    {
        Widescreen = config.Bind(Section, "Widescreen support", true,
            "The wide main menu, more screen sizes and fixes for wide and tall screens." + Restart).Value;
        Multiplayer = config.Bind(Section, "Multiplayer", true,
            "The Multiplayer menu: hosting, joining and playing together." + Restart).Value;
        ManualSaves = config.Bind(Section, "Manual saves", true,
            "Save Game and Load Game in the pause menu." + Restart).Value;
    }

    // The features that are off, by name.
    internal static IEnumerable<string> Off
    {
        get
        {
            if (!Widescreen)
                yield return "widescreen support";
            if (!Multiplayer)
                yield return "multiplayer";
            if (!ManualSaves)
                yield return "manual saves";
        }
    }

    // Whether a class of the mod loads: those of a feature that is off do not; the rest of the mod always does.
    internal static bool Loads(Type type)
    {
        string space = type.Namespace ?? string.Empty;
        foreach (var (owner, on) in Owners)
            if (space == owner || space.StartsWith(owner + ".", StringComparison.Ordinal))
                return on();
        return true;
    }
}
