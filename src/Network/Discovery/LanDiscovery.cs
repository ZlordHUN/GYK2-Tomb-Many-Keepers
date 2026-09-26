using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Discovery;

// UDP broadcast discovery, polled on the main thread. Browsers probe the local
// networks and hosts answer with their game description.
internal sealed class LanDiscovery : IDisposable
{
    internal sealed class Game
    {
        internal int Id;
        internal IPEndPoint Endpoint;
        internal string Name;
        internal int Players;
        // Players can still join the lobby before the host starts the game.
        internal bool InLobby;
        internal float SeenAt;
    }

    private const int Port = 34271;
    private const int MaxPackets = 32;
    private static readonly byte[] Probe = { (byte)'T', (byte)'M', (byte)'K', (byte)'?' };
    private static readonly byte[] Answer = { (byte)'T', (byte)'M', (byte)'K', (byte)'!' };

    private readonly Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly byte[] buffer = new byte[512];
    private readonly int id = UnityEngine.Random.Range(1, int.MaxValue);
    private List<IPAddress> targets;
    private EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

    private LanDiscovery(int port)
    {
        socket.EnableBroadcast = true;
        socket.Blocking = false;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    internal static LanDiscovery Host() => new LanDiscovery(Port);

    internal static LanDiscovery Browser() => new LanDiscovery(0);

    internal void Search()
    {
        targets ??= Targets();
        foreach (var address in targets)
            Send(Probe, new IPEndPoint(address, Port));
    }

    internal void Reply(string name, int players, bool inLobby, ushort gamePort)
    {
        byte[] reply = null;
        while (TryReceive(out int length))
        {
            if (length != Probe.Length || !Starts(Probe))
                continue;
            if (reply == null)
            {
                var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Answer);
                    writer.Write(id);
                    writer.Write(gamePort);
                    writer.Write((byte)players);
                    writer.Write(inLobby);
                    writer.Write(name);
                }
                reply = stream.ToArray();
            }
            Send(reply, sender);
        }
    }

    internal void Collect(List<Game> games)
    {
        while (TryReceive(out int length))
        {
            if (length <= Answer.Length || !Starts(Answer))
                continue;
            using var reader = new BinaryReader(new MemoryStream(buffer, Answer.Length, length - Answer.Length));
            int game = reader.ReadInt32();
            var entry = games.Find(item => item.Id == game);
            if (entry == null)
                games.Add(entry = new Game { Id = game });
            // Loopback and network answers from one host share its id; either address works.
            entry.Endpoint = new IPEndPoint(((IPEndPoint)sender).Address, reader.ReadUInt16());
            entry.Players = reader.ReadByte();
            entry.InLobby = reader.ReadBoolean();
            entry.Name = reader.ReadString();
            entry.SeenAt = Time.unscaledTime;
        }
    }

    public void Dispose() => socket.Close();

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
    }

    private bool TryReceive(out int length)
    {
        length = 0;
        for (int attempt = 0; attempt < MaxPackets && socket.Available > 0; attempt++)
        {
            try
            {
                length = socket.ReceiveFrom(buffer, ref sender);
                return true;
            }
            catch (SocketException)
            {
                // Windows reports an unanswered probe as a reset on the next receive.
            }
        }
        return false;
    }

    private bool Starts(byte[] magic)
    {
        for (int i = 0; i < magic.Length; i++)
            if (buffer[i] != magic[i])
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
