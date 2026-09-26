using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Steam;

// Messages over Steam networking sockets. LAN peers connect by IP address.
internal sealed class SteamTransport : IDisposable
{
    private const int MaxBatch = 64;
    // A joining player receives the whole game at once; the native limits suit small messages.
    private const int BufferSize = 64 * 1024 * 1024;
    private const int MaxSendRate = 64 * 1024 * 1024;
    private static readonly SteamNetworkingConfigValue_t[] Options =
    {
        // LAN peers may lack Steam certificates, for example while Steam is offline.
        Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_IP_AllowWithoutAuth, 2),
        Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, BufferSize),
        Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize, BufferSize),
        Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, MaxSendRate)
    };

    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> statusChanged;
    private readonly HSteamNetPollGroup pollGroup = SteamNetworkingSockets.CreatePollGroup();
    private readonly HashSet<HSteamNetConnection> connections = new HashSet<HSteamNetConnection>();
    private readonly IntPtr[] batch = new IntPtr[MaxBatch];
    private HSteamListenSocket listenSocket = HSteamListenSocket.Invalid;
    private byte[] received = new byte[512];
    private IntPtr sendBuffer = IntPtr.Zero;
    private int sendCapacity;
    private bool disposed;

    internal event Action<HSteamNetConnection> Connected;
    // Reports whether the peer closed the connection itself.
    internal event Action<HSteamNetConnection, bool> Disconnected;

    internal SteamTransport() =>
        statusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);

    internal bool Listen(ushort port)
    {
        var address = new SteamNetworkingIPAddr { m_ipv6 = new byte[16], m_port = port };
        listenSocket = SteamNetworkingSockets.CreateListenSocketIP(ref address, Options.Length, Options);
        return listenSocket != HSteamListenSocket.Invalid;
    }

    internal HSteamNetConnection Connect(IPEndPoint endpoint)
    {
        var address = new SteamNetworkingIPAddr { m_ipv6 = new byte[16] };
        address.ParseString(endpoint.ToString());
        var connection = SteamNetworkingSockets.ConnectByIPAddress(ref address, Options.Length, Options);
        Track(connection);
        return connection;
    }

    internal bool Send(HSteamNetConnection connection, byte[] data, int offset, int length, bool reliable)
    {
        if (sendCapacity < length)
        {
            Marshal.FreeHGlobal(sendBuffer);
            sendBuffer = Marshal.AllocHGlobal(length);
            sendCapacity = length;
        }
        Marshal.Copy(data, offset, sendBuffer, length);
        return SteamNetworkingSockets.SendMessageToConnection(connection, sendBuffer, (uint)length,
            reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoNagle,
            out _) == EResult.k_EResultOK;
    }

    internal void Receive(Action<HSteamNetConnection, byte[], int> handle)
    {
        int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, batch, MaxBatch);
        for (int i = 0; i < count; i++)
        {
            var message = SteamNetworkingMessage_t.FromIntPtr(batch[i]);
            if (received.Length < message.m_cbSize)
                received = new byte[message.m_cbSize];
            Marshal.Copy(message.m_pData, received, 0, message.m_cbSize);
            SteamNetworkingMessage_t.Release(batch[i]);
            // A handled message can end the session; the rest of the batch is only released.
            if (disposed)
                continue;
            try
            {
                handle(message.m_conn, received, message.m_cbSize);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }

    internal void Close(HSteamNetConnection connection)
    {
        // Lingering delivers the reliable messages already queued, such as a refusal.
        if (connections.Remove(connection))
            SteamNetworkingSockets.CloseConnection(connection, 0, null, true);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        statusChanged.Dispose();
        foreach (var connection in connections)
            SteamNetworkingSockets.CloseConnection(connection, 0, null, true);
        connections.Clear();
        if (listenSocket != HSteamListenSocket.Invalid)
            SteamNetworkingSockets.CloseListenSocket(listenSocket);
        SteamNetworkingSockets.DestroyPollGroup(pollGroup);
        Marshal.FreeHGlobal(sendBuffer);
    }

    private void Track(HSteamNetConnection connection)
    {
        connections.Add(connection);
        SteamNetworkingSockets.SetConnectionPollGroup(connection, pollGroup);
    }

    private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t status)
    {
        var connection = status.m_hConn;
        switch (status.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                // Outgoing connections report no listen socket.
                if (listenSocket == HSteamListenSocket.Invalid || status.m_info.m_hListenSocket != listenSocket)
                    return;
                if (SteamNetworkingSockets.AcceptConnection(connection) == EResult.k_EResultOK)
                    Track(connection);
                else
                    SteamNetworkingSockets.CloseConnection(connection, 0, null, false);
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                if (connections.Contains(connection))
                    Connected?.Invoke(connection);
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                if (!connections.Contains(connection))
                    return;
                // Messages received before the close stay readable until the handle is closed.
                Disconnected?.Invoke(connection, status.m_info.m_eState ==
                    ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer);
                if (connections.Remove(connection))
                    SteamNetworkingSockets.CloseConnection(connection, 0, null, false);
                break;
        }
    }

    private static SteamNetworkingConfigValue_t Option(ESteamNetworkingConfigValue value, int number) =>
        new SteamNetworkingConfigValue_t
        {
            m_eValue = value,
            m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = number }
        };
}
