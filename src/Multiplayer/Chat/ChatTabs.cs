using System;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace GYK2.TombManyKeepers.Multiplayer.Chat;

// The chat's tabs, Players and NPCs, each showing only its own lines. The lobby's chat and the game's show the same
// tab: the one the player chose last, in either, kept with the mod's settings through restarts and left out of its
// Mods window, as it is chosen in the chat itself.
internal static class ChatTabs
{
    internal enum Tab
    {
        Players,
        Npcs
    }

    internal static readonly string[] Names = { "Players", "NPCs" };
    private static ConfigEntry<Tab> chosen;

    internal static event Action Changed;

    internal static Tab Current
    {
        get => Chosen.Value;
        set
        {
            if (Chosen.Value == value)
                return;
            Chosen.Value = value;
            Changed?.Invoke();
        }
    }

    internal static void Next() => Current = (Tab)(((int)Current + 1) % Names.Length);

    private static ConfigEntry<Tab> Chosen => chosen ??= Chainloader.ManagerObject.GetComponent<Plugin>().Config.Bind("Chat", "Tab",
        Tab.Players, new ConfigDescription("The chat's tab chosen last.", null, new ConfigurationManagerAttributes { Browsable = false }));

    // BepInEx's convention for how settings managers show an entry, which the Mods window follows.
    private sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
    }
}
