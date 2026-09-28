using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using GYK2.TombManyKeepers.Network.Session;
using Steamworks;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Discovery;

// Online games, listed as Steam lobbies. A host's lobby only describes its game, with the build of the mod it runs
// and the version of the game, as its LAN answers do: nobody else joins the lobby, so it ends with the host's game,
// and players reach the host through Steam's network by the host's account. A public or password game's lobby is
// public and every search finds it; a friends' game's is for the host's Steam friends; a private game has none. The
// description never holds the password or the lobby's key. A search asks for lobbies of this session protocol
// worldwide, and each game's ping is estimated from where its host said it stands on Steam's relay network.
internal sealed class SteamLobbies : IDisposable
{
    // The lobby's data; the protocol's key also marks a lobby as this mod's.
    private const string ProtocolKey = "tmk_protocol", HostKey = "host", NameKey = "name", CampaignKey = "campaign",
        PlayersKey = "players", CapacityKey = "capacity", InLobbyKey = "in_lobby", AccessKey = "access", PortKey = "port",
        PingKey = "ping", BuildKey = "tmk_build", GameVersionKey = "game_version";
    // A lobby Steam did not make is asked for again after this long, and a search Steam never answers is given up.
    private const float Retry = 30f, SearchTimeout = 20f;
    // How often the host looks at where it stands on the relay network, which moves rarely, and how soon again
    // while the relays have not answered.
    private const float PlaceCheck = 10f, PlaceWait = 1f;
    private static readonly string Protocol = CoopSession.Protocol.ToString(CultureInfo.InvariantCulture);

    private readonly CallResult<LobbyCreated_t> created;
    private readonly CallResult<LobbyMatchList_t> matched;
    // Host: the lobby, the data it holds and the game it should describe.
    private readonly Dictionary<string, string> published = new Dictionary<string, string>();
    private CSteamID lobby = CSteamID.Nil;
    private ELobbyType type;
    private bool creating;
    private float retryAt;
    private float nextPlace;
    private (ulong host, string name, string campaign, int players, int capacity, bool inLobby, ushort port, byte access, string build,
        string gameVersion) described;
    private bool stale = true;
    // Browser: the games the latest search found, each kept by its lobby so a game stays the same as searches
    // repeat, and where each host stands, for its ping.
    private readonly Dictionary<ulong, LanDiscovery.Game> known = new Dictionary<ulong, LanDiscovery.Game>();
    private readonly Dictionary<ulong, SteamNetworkPingLocation_t> places = new Dictionary<ulong, SteamNetworkPingLocation_t>();
    private readonly List<LanDiscovery.Game> found = new List<LanDiscovery.Game>();
    private float searchedAt = -1f;
    private bool disposed;

    private SteamLobbies()
    {
        // Pings and connections through Steam's network both need its relays.
        SteamNetworkingUtils.InitRelayNetworkAccess();
        created = CallResult<LobbyCreated_t>.Create(OnCreated);
        matched = CallResult<LobbyMatchList_t>.Create(OnMatched);
    }

    internal static SteamLobbies Host() => new SteamLobbies();

    internal static SteamLobbies Browser() => new SteamLobbies();

    // A search is out and Steam has not answered yet.
    internal bool Searching => searchedAt >= 0f && Time.unscaledTime - searchedAt < SearchTimeout;

    // The game the host's lobby describes from now on. A private game has no lobby; the others make one as needed.
    internal void Describe(ulong host, string name, string campaign, int players, int capacity, bool inLobby, ushort gamePort, byte access,
        string build, string gameVersion)
    {
        var game = (host, name, campaign, players, capacity, inLobby, gamePort, access, build, gameVersion);
        if (!game.Equals(described))
        {
            described = game;
            stale = true;
        }
        if (access == (byte)HostSettings.Access.Private)
        {
            Leave();
            return;
        }
        var wanted = access == (byte)HostSettings.Access.Friends ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic;
        if (lobby == CSteamID.Nil)
        {
            if (creating || Time.unscaledTime < retryAt)
                return;
            var call = SteamMatchmaking.CreateLobby(wanted, CoopSession.MaxPlayers);
            if (call == SteamAPICall_t.Invalid)
            {
                retryAt = Time.unscaledTime + Retry;
                return;
            }
            creating = true;
            type = wanted;
            created.Set(call);
            return;
        }
        if (stale)
        {
            if (type != wanted && SteamMatchmaking.SetLobbyType(lobby, wanted))
                type = wanted;
            // What Steam did not take is given again the next frame.
            bool taken = Publish(HostKey, host.ToString(CultureInfo.InvariantCulture)) & Publish(NameKey, name ?? string.Empty) &
                         Publish(CampaignKey, campaign ?? string.Empty) & Publish(PlayersKey, players.ToString(CultureInfo.InvariantCulture)) &
                         Publish(CapacityKey, capacity.ToString(CultureInfo.InvariantCulture)) & Publish(InLobbyKey, inLobby ? "1" : "0") &
                         Publish(AccessKey, access.ToString(CultureInfo.InvariantCulture)) &
                         Publish(PortKey, gamePort.ToString(CultureInfo.InvariantCulture)) & Publish(BuildKey, build ?? string.Empty) &
                         Publish(GameVersionKey, gameVersion ?? string.Empty);
            // Searches match the protocol's key, so a new lobby is found only once the rest describes its game.
            stale = !(taken && Publish(ProtocolKey, Protocol) && type == wanted);
        }
        if (Time.unscaledTime >= nextPlace)
        {
            // Before the relays answer, the host has no place yet.
            bool placed = SteamNetworkingUtils.GetLocalPingLocation(out var place) >= 0f;
            if (placed)
            {
                SteamNetworkingUtils.ConvertPingLocationToString(ref place, out string text, Constants.k_cchMaxSteamNetworkingPingLocationString);
                placed = Publish(PingKey, text);
            }
            nextPlace = Time.unscaledTime + (placed ? PlaceCheck : PlaceWait);
        }
    }

    // Asks Steam for the games listed now, unless a search is still out.
    internal void Search()
    {
        if (Searching)
            return;
        SteamMatchmaking.AddRequestLobbyListStringFilter(ProtocolKey, Protocol, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        var call = SteamMatchmaking.RequestLobbyList();
        if (call == SteamAPICall_t.Invalid)
            return;
        searchedAt = Time.unscaledTime;
        matched.Set(call);
    }

    // The list starts over: the games found so far are forgotten until the next search answers.
    internal void Clear() => found.Clear();

    // The games the latest search found. A ping Steam could not estimate yet, before its relays answered, is
    // estimated again.
    internal void Collect(List<LanDiscovery.Game> games)
    {
        games.Clear();
        foreach (var game in found)
        {
            if (game.Ping < 0f && places.TryGetValue(game.Lobby, out var place))
                Estimate(game, place);
            games.Add(game);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Leave();
        // A lobby Steam is still making is left as it is made.
        if (!creating)
            created.Dispose();
        matched.Dispose();
    }

    // Whether the lobby holds this value now.
    private bool Publish(string key, string value)
    {
        if (published.TryGetValue(key, out string current) && current == value)
            return true;
        if (!SteamMatchmaking.SetLobbyData(lobby, key, value))
            return false;
        published[key] = value;
        return true;
    }

    private void Leave()
    {
        if (lobby == CSteamID.Nil)
            return;
        SteamMatchmaking.LeaveLobby(lobby);
        lobby = CSteamID.Nil;
        published.Clear();
        stale = true;
    }

    private void OnCreated(LobbyCreated_t result, bool failure)
    {
        creating = false;
        var made = new CSteamID(result.m_ulSteamIDLobby);
        bool ok = !failure && result.m_eResult == EResult.k_EResultOK;
        // The game ended, or turned private, while Steam made the lobby.
        if (disposed || described.access == (byte)HostSettings.Access.Private)
        {
            if (ok)
                SteamMatchmaking.LeaveLobby(made);
            return;
        }
        if (!ok)
        {
            Debug.LogWarning($"[Multiplayer] Steam did not make the game's online lobby ({(failure ? "no answer" : result.m_eResult.ToString())})");
            retryAt = Time.unscaledTime + Retry;
            return;
        }
        lobby = made;
        stale = true;
        nextPlace = 0f;
        Debug.Log($"[Multiplayer] Listed the game online as Steam lobby {made.m_SteamID}");
    }

    private void OnMatched(LobbyMatchList_t result, bool failure)
    {
        searchedAt = -1f;
        if (failure)
            return;
        found.Clear();
        var seen = new HashSet<ulong>();
        for (int i = 0; i < result.m_nLobbiesMatching; i++)
        {
            var game = Read(SteamMatchmaking.GetLobbyByIndex(i));
            if (game != null && seen.Add(game.Lobby))
                found.Add(game);
        }
        foreach (ulong gone in new List<ulong>(known.Keys))
        {
            if (seen.Contains(gone))
                continue;
            known.Remove(gone);
            places.Remove(gone);
        }
    }

    // A found lobby's game, or null for a lobby that describes none.
    private LanDiscovery.Game Read(CSteamID id)
    {
        string Data(string key) => SteamMatchmaking.GetLobbyData(id, key);
        if (Data(ProtocolKey) != Protocol ||
            !ulong.TryParse(Data(HostKey), NumberStyles.None, CultureInfo.InvariantCulture, out ulong host) ||
            !int.TryParse(Data(PlayersKey), NumberStyles.None, CultureInfo.InvariantCulture, out int players) ||
            !int.TryParse(Data(CapacityKey), NumberStyles.None, CultureInfo.InvariantCulture, out int capacity) ||
            !byte.TryParse(Data(AccessKey), NumberStyles.None, CultureInfo.InvariantCulture, out byte access) ||
            !ushort.TryParse(Data(PortKey), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) || port == 0)
            return null;
        // Steam's word on who holds the lobby counts over the lobby's own.
        ulong owner = SteamMatchmaking.GetLobbyOwner(id).m_SteamID;
        if (owner != 0)
            host = owner;
        if (host == 0)
            return null;
        if (!known.TryGetValue(id.m_SteamID, out var game))
            known[id.m_SteamID] = game = new LanDiscovery.Game { Lobby = id.m_SteamID };
        game.Host = host;
        game.Name = Data(NameKey);
        game.Campaign = Data(CampaignKey);
        game.Players = players;
        game.Capacity = capacity;
        game.InLobby = Data(InLobbyKey) == "1";
        game.Access = access;
        game.Build = Data(BuildKey);
        game.GameVersion = Data(GameVersionKey);
        game.SeenAt = Time.unscaledTime;
        // Steam's network does not join an account to itself, so a game the player's own account hosts, as another
        // copy of the game on this machine does, is reached at its port here.
        game.Endpoint = host == PlayerIdentity.SteamId ? new IPEndPoint(IPAddress.Loopback, port) : null;
        if (SteamNetworkingUtils.ParsePingLocationString(Data(PingKey), out var place))
        {
            places[id.m_SteamID] = place;
            Estimate(game, place);
        }
        return game;
    }

    private static void Estimate(LanDiscovery.Game game, SteamNetworkPingLocation_t place)
    {
        int milliseconds = SteamNetworkingUtils.EstimatePingTimeFromLocalHost(ref place);
        if (milliseconds >= 0)
            game.Timed(milliseconds);
    }
}
