using System.Collections.Generic;
using BepInEx;

namespace GYK2.TombManyKeepers.UI.Mods;

// The keys a mod handles that none of its settings holds, which the Mods window's Controls section shows beside the keys
// its settings hold: what each does, the keys that do it, and where. Only this mod's are known, each while the feature
// that handles it is on; another mod's keys are found in its settings alone.
internal static class ModControls
{
    internal sealed class Control
    {
        internal readonly string Name, Keys, Description;

        internal Control(string name, string keys, string description)
        {
            Name = name;
            Keys = keys;
            Description = description;
        }
    }

    internal static List<Control> Of(PluginInfo plugin) => plugin?.Instance is Plugin ? new List<Control>(Own()) : new List<Control>();

    // The chat's keys and Join Game's come with multiplayer; skipping the startup logos is always there.
    private static IEnumerable<Control> Own()
    {
        if (FeatureSwitches.Multiplayer)
        {
            yield return new Control("Open the chat", "Enter or Y", "The game's chat, while no window is open and nothing else is typed.");
            yield return new Control("Send a line", "Enter", "From the chat's Players tab, in the lobby or the game, to every player.");
            yield return new Control("Drop the line", "Esc", "Closes the game's chat without sending what was typed.");
            yield return new Control("Switch chat tabs", "Tab", "Between Players and NPCs while typing in the game's chat. In the lobby, " +
                "click the tabs, or use a gamepad's bumpers.");
            yield return new Control("Complete a command", "Tab", "A cheat command being typed takes the suggestion shown.");
            yield return new Control("Pick a suggestion", "Up or Down", "Among the suggestions for a cheat command being typed.");
            yield return new Control("Scroll the chat", "Mouse wheel", "Over the chat's lines, back through what was said.");
            yield return new Control("Favorite a game", "Right-click", "In Join Game, on a game: its host is added to Favorites, " +
                "or taken off. With a gamepad, the item action button.");
        }
        yield return new Control("Skip the logos", "Space or left-click", "The logos before the main menu, as the game starts.");
    }
}
