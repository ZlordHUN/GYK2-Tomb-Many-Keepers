using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Discovery;

// The hosts a player keeps as favourites, known by their Steam accounts, with the name and address each was
// last seen with; a host only seen online has no address. They are kept beside the player's profile, and the
// browser also asks each at its address, so a favourite answers from beyond the local network's broadcasts too.
internal static class FavoriteHosts
{
    internal sealed class Host
    {
        internal ulong Account;
        internal string Name;
        // Null for a host only seen online.
        internal IPAddress Address;
    }

    private const string FileName = "tmk_favorites.dat";
    private const byte Format = 1;
    private static List<Host> hosts;

    internal static IReadOnlyList<Host> All => Hosts;

    internal static bool Contains(ulong account) => Find(account) != null;

    internal static void Add(LanDiscovery.Game game)
    {
        if (game.Host == 0 || Contains(game.Host))
            return;
        Hosts.Add(new Host { Account = game.Host, Name = game.Name, Address = game.Lobby != 0 ? null : game.Endpoint?.Address });
        Save();
    }

    internal static void Remove(ulong account)
    {
        if (Hosts.RemoveAll(host => host.Account == account) > 0)
            Save();
    }

    // A favourite seen again keeps its latest name, and its network address over the loopback one a host on the
    // same machine also answers from; an online game has no address to keep.
    internal static void Seen(LanDiscovery.Game game)
    {
        var host = Find(game.Host);
        if (host == null)
            return;
        var address = game.Lobby != 0 ? null : game.Endpoint?.Address;
        bool moved = address != null && !address.Equals(host.Address) &&
                     (host.Address == null || !IPAddress.IsLoopback(address) || IPAddress.IsLoopback(host.Address));
        if (host.Name == game.Name && !moved)
            return;
        host.Name = game.Name;
        if (moved)
            host.Address = address;
        Save();
    }

    private static Host Find(ulong account) => Hosts.Find(host => host.Account == account);

    private static List<Host> Hosts => hosts ??= Load();

    private static string PathOf() => Path.Combine(Application.persistentDataPath, FileName);

    private static List<Host> Load()
    {
        var loaded = new List<Host>();
        string path = PathOf();
        if (!File.Exists(path))
            return loaded;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadByte() != Format)
                return loaded;
            for (int count = reader.ReadInt32(); count > 0; count--)
            {
                var host = new Host { Account = reader.ReadUInt64(), Name = reader.ReadString() };
                string text = reader.ReadString();
                host.Address = text.Length == 0 ? null : IPAddress.TryParse(text, out var address) ? address : IPAddress.Loopback;
                loaded.Add(host);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Multiplayer] Could not read the favourite hosts: " + exception.Message);
        }
        return loaded;
    }

    private static void Save()
    {
        try
        {
            using var writer = new BinaryWriter(File.Create(PathOf()));
            writer.Write(Format);
            writer.Write(Hosts.Count);
            foreach (var host in Hosts)
            {
                writer.Write(host.Account);
                writer.Write(host.Name);
                writer.Write(host.Address?.ToString() ?? string.Empty);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Multiplayer] Could not save the favourite hosts: " + exception.Message);
        }
    }
}
