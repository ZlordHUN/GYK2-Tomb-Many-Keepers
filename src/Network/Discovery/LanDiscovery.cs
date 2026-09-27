using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Discovery;

// UDP broadcast discovery. Browsers probe the local networks, and any host they name, and hosts answer with
// their game description, which says who the game is open to. A probe carries the moment it left, which the answer
// returns, so the browser times the round trip; both ends take packets on a thread of their own as they arrive, so
// no frame's wait counts. A probe may also carry a lobby's key from an invite: an unlisted game answers only that.
internal sealed class LanDiscovery : IDisposable
{
    internal sealed class Game
    {
        // Trips kept for the ping, of which the quickest counts: a slower one waited on something besides the network.
        private const int Trips = 4;
        private readonly float[] trips = new float[Trips];
        private int timed;

        internal int Id;
        // Where the host is reached, or null for an online game reached through Steam's network by its account.
        internal IPEndPoint Endpoint;
        // The Steam lobby that lists an online game, or 0 for a game the local network answered for.
        internal ulong Lobby;
        // The host's Steam account, by which the player's favourites know the game.
        internal ulong Host;
        internal string Name;
        // The hosted campaign, such as a new game or a saved one's day.
        internal string Campaign;
        internal int Players;
        // How many keepers the host's settings let the game take.
        internal int Capacity;
        // Players can still join the lobby before the host starts the game.
        internal bool InLobby;
        // Who the host's settings open the game to, as the host's settings name them.
        internal byte Access;
        // The round trip to the host in milliseconds, or less than zero before one is timed.
        internal float Ping = -1f;
        internal float SeenAt;

        internal void Timed(float milliseconds)
        {
            trips[timed++ % Trips] = milliseconds;
            Ping = float.MaxValue;
            for (int i = 0; i < Math.Min(timed, Trips); i++)
                Ping = Math.Min(Ping, trips[i]);
        }
    }

    // An answer as it arrived: the game it describes, where from, and the round trip it timed.
    private sealed class Arrival
    {
        internal byte[] Description;
        internal IPAddress From;
        internal float Trip;
    }

    private const int Port = 34271;
    private const int StampedLength = 8;
    // A lobby's key, as an invite carries it: this many letters.
    internal const int KeyLength = 16;
    // Answers wait at most this many for the main thread, which takes them every frame.
    private const int Waiting = 256;
    // Longer than any trip on a network; an answer this late, or naming another moment, is not timed.
    private const float LongestTrip = 5000f;
    private static readonly byte[] Probe = { (byte)'T', (byte)'M', (byte)'K', (byte)'?' };
    // The last byte is the answer's format; games of another format ignore each other's probes and answers.
    private static readonly byte[] Answer = { (byte)'T', (byte)'M', (byte)'K', 4 };
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly int id = UnityEngine.Random.Range(1, int.MaxValue);
    private readonly Queue<Arrival> arrivals = new Queue<Arrival>();
    private List<IPAddress> targets;
    // The host's game as its answers describe it, replaced whole when something in it changes, and whether it
    // answers every probe or only those carrying its key.
    private volatile byte[] description;
    private volatile bool listed;
    private volatile byte[] key;
    private (ulong, string, string, int, int, bool, ushort, byte, bool, string) described;
    private volatile bool closed;

    private LanDiscovery(int port, bool host)
    {
        socket.EnableBroadcast = true;
        // The thread looks up from its wait now and then, for runtimes where closing the socket does not end it.
        socket.ReceiveTimeout = 250;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        new Thread(host ? new ThreadStart(AnswerProbes) : TakeAnswers) { IsBackground = true, Name = "LAN discovery" }.Start();
    }

    internal static LanDiscovery Host() => new LanDiscovery(Port, host: true);

    internal static LanDiscovery Browser() => new LanDiscovery(0, host: false);

    // Probes the local networks and the hosts at these addresses wherever they are, and, with a lobby's key, the
    // addresses an invite names.
    internal void Search(IEnumerable<IPAddress> hosts, IEnumerable<IPAddress> invited, string lobbyKey)
    {
        targets ??= Targets();
        var probe = new byte[StampedLength];
        Buffer.BlockCopy(Probe, 0, probe, 0, Probe.Length);
        BitConverter.GetBytes(Now()).CopyTo(probe, Probe.Length);
        foreach (var address in targets)
            Send(probe, new IPEndPoint(address, Port));
        foreach (var address in hosts)
            if (!targets.Contains(address))
                Send(probe, new IPEndPoint(address, Port));
        if (lobbyKey == null || lobbyKey.Length != KeyLength)
            return;
        var keyed = new byte[StampedLength + KeyLength];
        Buffer.BlockCopy(probe, 0, keyed, 0, StampedLength);
        System.Text.Encoding.ASCII.GetBytes(lobbyKey, 0, KeyLength, keyed, StampedLength);
        foreach (var address in invited)
            Send(keyed, new IPEndPoint(address, Port));
    }

    // The game the host's answers describe from now on: who it is open to, whether every probe is answered or
    // only those carrying the lobby's key.
    internal void Describe(ulong host, string name, string campaign, int players, int capacity, bool inLobby, ushort gamePort,
        byte access, bool answersAll, string lobbyKey)
    {
        var game = (host, name, campaign, players, capacity, inLobby, gamePort, access, answersAll, lobbyKey);
        if (description != null && game.Equals(described))
            return;
        described = game;
        listed = answersAll;
        key = System.Text.Encoding.ASCII.GetBytes(lobbyKey ?? string.Empty);
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(id);
            writer.Write(gamePort);
            writer.Write((byte)players);
            writer.Write((byte)capacity);
            writer.Write(inLobby);
            writer.Write(host);
            writer.Write(name);
            writer.Write(campaign);
            writer.Write(access);
        }
        description = stream.ToArray();
    }

    internal void Collect(List<Game> games)
    {
        while (true)
        {
            Arrival arrival;
            lock (arrivals)
            {
                if (arrivals.Count == 0)
                    return;
                arrival = arrivals.Dequeue();
            }
            try
            {
                Read(arrival, games);
            }
            catch (Exception exception) when (exception is EndOfStreamException || exception is IOException)
            {
                // An answer cut short describes no game.
            }
        }
    }

    public void Dispose()
    {
        closed = true;
        socket.Close();
    }

    private static void Read(Arrival arrival, List<Game> games)
    {
        using var reader = new BinaryReader(new MemoryStream(arrival.Description));
        int game = reader.ReadInt32();
        ushort gamePort = reader.ReadUInt16();
        int players = reader.ReadByte();
        int capacity = reader.ReadByte();
        bool inLobby = reader.ReadBoolean();
        ulong host = reader.ReadUInt64();
        string name = reader.ReadString();
        string campaign = reader.ReadString();
        byte access = reader.ReadByte();
        var entry = games.Find(item => item.Id == game);
        if (entry == null)
            games.Add(entry = new Game { Id = game });
        // Loopback and network answers from one host share its id; either address works.
        entry.Endpoint = new IPEndPoint(arrival.From, gamePort);
        entry.Players = players;
        entry.Capacity = capacity;
        entry.InLobby = inLobby;
        entry.Host = host;
        entry.Name = name;
        entry.Campaign = campaign;
        entry.Access = access;
        entry.SeenAt = Time.unscaledTime;
        if (arrival.Trip >= 0f)
            entry.Timed(arrival.Trip);
    }

    // The host's thread: each probe is answered as it arrives, with the time it carried; an unlisted game answers
    // only probes carrying its key.
    private void AnswerProbes() => Receive((packet, length, from) =>
    {
        byte[] game = description;
        if (game == null || (length != StampedLength && length != StampedLength + KeyLength) || !Starts(packet, Probe))
            return;
        if (!listed && (length == StampedLength || !Keyed(packet, key)))
            return;
        var reply = new byte[StampedLength + game.Length];
        Buffer.BlockCopy(Answer, 0, reply, 0, Answer.Length);
        Buffer.BlockCopy(packet, Probe.Length, reply, Answer.Length, StampedLength - Probe.Length);
        Buffer.BlockCopy(game, 0, reply, StampedLength, game.Length);
        Send(reply, from);
    });

    // The browser's thread: each answer is timed as it arrives, and read on the main thread.
    private void TakeAnswers() => Receive((packet, length, from) =>
    {
        if (length <= StampedLength || !Starts(packet, Answer))
            return;
        float trip = unchecked(Now() - BitConverter.ToUInt32(packet, Answer.Length)) / 1000f;
        var arrival = new Arrival
        {
            Description = new byte[length - StampedLength],
            From = ((IPEndPoint)from).Address,
            Trip = trip <= LongestTrip ? trip : -1f
        };
        Buffer.BlockCopy(packet, StampedLength, arrival.Description, 0, arrival.Description.Length);
        lock (arrivals)
            if (arrivals.Count < Waiting)
                arrivals.Enqueue(arrival);
    });

    private void Receive(Action<byte[], int, EndPoint> handle)
    {
        var packet = new byte[512];
        while (!closed)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                length = socket.ReceiveFrom(packet, ref from);
            }
            catch (SocketException)
            {
                // The wait ran out, or, on Windows, an unanswered probe came back as a reset.
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            handle(packet, length, from);
        }
    }

    // Microseconds on this game's clock, wrapping around; only differences count.
    private static uint Now() => unchecked((uint)(long)(Clock.Elapsed.TotalMilliseconds * 1000.0));

    private void Send(byte[] data, EndPoint target)
    {
        try
        {
            socket.SendTo(data, target);
        }
        catch (SocketException)
        {
            // Adapters without a route to the target are skipped.
        }
        catch (ObjectDisposedException)
        {
            // The thread answered as the discovery closed.
        }
    }

    private static bool Keyed(byte[] packet, byte[] lobbyKey)
    {
        if (lobbyKey == null || lobbyKey.Length != KeyLength)
            return false;
        for (int i = 0; i < KeyLength; i++)
            if (packet[StampedLength + i] != lobbyKey[i])
                return false;
        return true;
    }

    private static bool Starts(byte[] packet, byte[] magic)
    {
        for (int i = 0; i < magic.Length; i++)
            if (packet[i] != magic[i])
                return false;
        return true;
    }

    // The limited broadcast does not cross every adapter, so each subnet is probed
    // too; loopback finds a host running on the same machine.
    private static List<IPAddress> Targets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                        continue;
                    uint address = BitConverter.ToUInt32(unicast.Address.GetAddressBytes(), 0);
                    uint mask = BitConverter.ToUInt32(unicast.IPv4Mask.GetAddressBytes(), 0);
                    var broadcast = new IPAddress(BitConverter.GetBytes(address | ~mask));
                    if (!targets.Contains(broadcast))
                        targets.Add(broadcast);
                }
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException || exception is NotImplementedException)
        {
            // Some runtimes cannot enumerate adapters; the limited broadcast remains.
        }
        return targets;
    }
}
