using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Session;

// Which version of the game a player runs, as Unity reports it and the main menu shows it. Games play together only
// on the same version, as GYK1's did, since another may load, run and save the world differently; the mod's build is
// compared first. As in GYK1, only a version-shaped token counts, and one missing or malformed matches nothing, not
// even itself. Matching versions cannot prove the same game files.
internal static class GameVersion
{
    private const int Longest = 24;
    private static string local;

    // This game's version, or empty when it reports none fit to compare.
    internal static string Local => local ??= Read();

    internal static bool Matches(string version) => Local.Length > 0 && Normalize(version) == Local;

    // Why a player cannot join the host's game, as GYK1 said a game build mismatch, for the player joining.
    internal static string Mismatch(string yours, string host) =>
        "Cannot join: game version mismatch.\n" +
        $"Your game version: {Shown(yours)}\n" +
        $"Host game version: {Shown(host)}\n" +
        "Both players need the same game version.";

    private static string Shown(string version) => Normalize(version) ?? "unavailable";

    // The version without surrounding spaces, or null unless it is at most 24 letters, digits, dots, dashes,
    // underscores and pluses.
    private static string Normalize(string version)
    {
        string trimmed = version?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > Longest)
            return null;
        foreach (char letter in trimmed)
        {
            if (!(letter >= '0' && letter <= '9' || letter >= 'A' && letter <= 'Z' || letter >= 'a' && letter <= 'z' ||
                  letter == '.' || letter == '-' || letter == '_' || letter == '+'))
                return null;
        }
        return trimmed;
    }

    private static string Read()
    {
        string version = Normalize(Application.version);
        if (version != null)
            return version;
        Debug.LogWarning($"[Multiplayer] The game reports no version fit to compare ('{Application.version}'), so no game can join it or be joined");
        return string.Empty;
    }
}
