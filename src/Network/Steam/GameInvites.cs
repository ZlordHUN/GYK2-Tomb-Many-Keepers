using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Steamworks;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Steam;

// Steam's own game invites. An invite names the host's Steam account, the game's port, the lobby's key and the
// addresses the host is reached at, and Steam hands it to the friend's game once they accept: on the command line of
// the game it starts for them, or to their game already running. The key lets the friend find and join a private or
// password game without its password. Invites do not use Steam's network yet: the friend's game finds the host at
// those addresses, so an invite works on the same network or across a VPN.
internal static class GameInvites
{
    internal sealed class Invite
    {
        internal ulong Host;
        internal ushort Port;
        internal string Key;
        internal IPAddress[] Addresses;
        // The friend who sent it, or 0 for one given on the command line.
        internal ulong From;
    }

    private const string Command = "+tmk_invite";
    // Steam keeps an invite's text to 256 bytes, which this many addresses leave room for.
    private const int MostAddresses = 8;
    private static Callback<GameRichPresenceJoinRequested_t> requested;

    // The invite the player accepted and has not acted on yet.
    internal static Invite Pending { get; private set; }

    // An accepted invite arrived while the game runs.
    internal static event Action Arrived;

    // Listens for invites the player accepts, and takes the one this game was started with.
    internal static void Listen()
    {
        if (requested != null)
            return;
        requested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRequested);
        Pending = Parse(string.Join(" ", Environment.GetCommandLineArgs()), from: 0);
        if (Pending != null)
            Debug.Log($"[Multiplayer] Started with an invite to {Pending.Host}'s game");
    }

    internal static Invite Take()
    {
        var invite = Pending;
        Pending = null;
        return invite;
    }

    // Sends the friend Steam's invite to the game hosted by this account, with its lobby's key, at these addresses
    // and port.
    internal static bool Send(ulong friend, ulong host, ushort port, string key, IReadOnlyList<IPAddress> addresses)
    {
        var connect = new System.Text.StringBuilder(Command).Append(' ').Append(host).Append(' ').Append(port).Append(' ').Append(key);
        for (int i = 0; i < addresses.Count && i < MostAddresses; i++)
            connect.Append(' ').Append(addresses[i]);
        bool sent = SteamFriends.InviteUserToGame(new CSteamID(friend), connect.ToString());
        Debug.Log($"[Multiplayer] Invite to {friend} {(sent ? "sent" : "refused by Steam")}: {connect}");
        return sent;
    }

    // Where a game hosted on this machine is reached from the networks it is on: its IPv4 addresses, without
    // loopback and self-assigned ones.
    internal static List<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    byte[] bytes = address.GetAddressBytes();
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                        !(bytes[0] == 169 && bytes[1] == 254) && !addresses.Contains(address))
                        addresses.Add(address);
                }
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException || exception is NotImplementedException)
        {
            Debug.LogWarning("[Multiplayer] Could not list this machine's addresses: " + exception.Message);
        }
        return addresses;
    }

    private static void OnRequested(GameRichPresenceJoinRequested_t request)
    {
        var invite = Parse(request.m_rgchConnect, request.m_steamIDFriend.m_SteamID);
        if (invite == null)
            return;
        Debug.Log($"[Multiplayer] Accepted an invite from {invite.From} to {invite.Host}'s game");
        Pending = invite;
        Arrived?.Invoke();
    }

    // An invite's text: the command, then the host's account, the port, the lobby's key and the addresses.
    internal static Invite Parse(string text, ulong from)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        string[] words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int at = Array.IndexOf(words, Command);
        if (at < 0 || at + 4 >= words.Length || !ulong.TryParse(words[at + 1], out ulong host) || host == 0 ||
            !ushort.TryParse(words[at + 2], out ushort port) || port == 0 || words[at + 3].Length != Discovery.LanDiscovery.KeyLength)
            return null;
        string key = words[at + 3];
        var addresses = new List<IPAddress>();
        for (int i = at + 4; i < words.Length && addresses.Count < MostAddresses; i++)
        {
            if (!IPAddress.TryParse(words[i], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                break;
            addresses.Add(address);
        }
        return addresses.Count == 0 ? null : new Invite { Host = host, Port = port, Key = key, Addresses = addresses.ToArray(), From = from };
    }
}
