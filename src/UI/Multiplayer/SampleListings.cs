using System;
using System.Collections.Generic;
using System.Net;
using GYK2.TombManyKeepers.Network.Discovery;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// TEMPORARY, for reviewing the browser's design before online play: one sample game on each of the Internet,
// Friends and LAN tabs, with a ping of each colour; the Internet one asks for a password, the Friends one runs
// another build of the mod and the LAN one another version of the game. A sample can be picked and kept in the
// favourites like a real game, but has no game behind it, so joining one says so. Remove this file and its uses in
// ServerBrowser once real games fill the tabs.
internal static class SampleListings
{
    // Probes turn the samples off, so the browser lists only what the network finds.
    internal static bool Shown = true;

    private static readonly LanDiscovery.Game[][] Tabs =
    {
        new[] { Sample(1, "Sample Internet Game", "New game", 2, 4, inLobby: true, ping: 72f, password: true) },
        new[] { Sample(2, "Sample Friend's Game", "Saved game, day 23", 3, 4, inLobby: false, ping: 140f, otherBuild: true) },
        Array.Empty<LanDiscovery.Game>(),
        new[] { Sample(3, "Sample LAN Game", "New game", 1, 3, inLobby: true, ping: 1f, otherVersion: true) }
    };
    private static readonly string[] Sources = { "Internet", "Friends", null, "LAN" };

    // The samples a tab lists.
    internal static IReadOnlyList<LanDiscovery.Game> On(int tab) => Shown ? Tabs[tab] : Array.Empty<LanDiscovery.Game>();

    internal static bool Contains(LanDiscovery.Game game) => TabOf(game) >= 0;

    // The sample kept in the favourites under this account, if one is shown.
    internal static LanDiscovery.Game Find(ulong host)
    {
        for (int tab = 0; Shown && tab < Tabs.Length; tab++)
            foreach (var game in Tabs[tab])
                if (game.Host == host)
                    return game;
        return null;
    }

    // Where a sample says it was found, or null for a game the network found.
    internal static string SourceOf(LanDiscovery.Game game)
    {
        int tab = TabOf(game);
        return tab < 0 ? null : Sources[tab];
    }

    private static int TabOf(LanDiscovery.Game game)
    {
        for (int tab = 0; tab < Tabs.Length; tab++)
            if (Array.IndexOf(Tabs[tab], game) >= 0)
                return tab;
        return -1;
    }

    private static LanDiscovery.Game Sample(int number, string name, string campaign, int players, int capacity, bool inLobby, float ping,
        bool password = false, bool otherBuild = false, bool otherVersion = false)
    {
        var game = new LanDiscovery.Game
        {
            // Discovery's ids and Steam accounts are never this small.
            Id = -number,
            Host = (ulong)number,
            // A favourite is also asked at its address; a sample's stays on this machine.
            Endpoint = new IPEndPoint(IPAddress.Parse("127.0.0." + (10 + number)), 34272),
            Name = name,
            Campaign = campaign,
            Players = players,
            Capacity = capacity,
            InLobby = inLobby,
            Access = (byte)(password ? Network.Session.HostSettings.Access.Password : Network.Session.HostSettings.Access.Public),
            Build = otherBuild ? new string('0', 64) : Network.Session.ModBuild.Id,
            GameVersion = otherVersion ? "1.005" : Network.Session.GameVersion.Local
        };
        game.Timed(ping);
        return game;
    }
}
