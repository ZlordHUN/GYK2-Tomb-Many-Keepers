using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using GYK2.TombManyKeepers.Features.MultiplayerKeepers;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.Presentation;
using GYK2.TombManyKeepers.Multiplayer.Progression;
using GYK2.TombManyKeepers.Multiplayer.Session;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Discovery;
using GYK2.TombManyKeepers.Network.Steam;
using LazyBearTechnology;
using Steamworks;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Session;

// The host gathers players in a lobby, admits them into keeper slots 2-4 by their identity and
// starts or continues its campaign once everyone is ready, releasing the party together once all
// of them have loaded. Everyone's loading screen opens at the start and follows the host's loading,
// the transfer of its game and everyone's own loading. A later player enters the lobby, readies up
// and joins the running game.
// The host keeps every joined player's character in the campaign, decides every rescue, orders every
// player's story transitions and relays every keeper's state and every player's changes to the
// world. Each message starts with its type and the keeper slot it concerns.
internal sealed class CoopSession : MonoBehaviour
{
    internal const int MaxPlayers = 4;
    // The host is always the lobby's first keeper.
    internal const int HostSlot = 1;
    private const ushort GamePort = 34272;
    private const byte Protocol = 7;
    private const float StateInterval = 0.05f;
    // Reliable messages are limited to 512 KiB, so the game travels in parts.
    private const int WorldPartSize = 256 * 1024;
    // A starting player who never finishes loading does not hold the others forever.
    private const float BarrierTimeout = 120f;
    // The opening places the bays right after the story starts.
    private const float BaysTimeout = 15f;
    // Players starting together hear how the host's loading goes at least this often.
    private const float EntryStatusInterval = 1f;
    private const float ReceiveReportInterval = 0.25f;
    // A joined player gives up on a host that stops sending anything towards its entry.
    private const float EntryTimeout = 60f;

    private enum Message : byte
    {
        Hello,
        Welcome,
        Refused,
        Joined,
        Left,
        Ready,
        Started,
        JoinGame,
        Loaded,
        Release,
        Bays,
        Character,
        State,
        Trigger,
        World,
        WorldPart,
        Struggle,
        Freed,
        Changes,
        Motion,
        Clock,
        QuestRequest,
        QuestApproved,
        Cut,
        Claim,
        Granted,
        Borrow,
        Lend,
        Return,
        Rest,
        LoadStatus,
        LoadReport,
        Cue
    }

    private readonly Dictionary<HSteamNetConnection, int> peers = new Dictionary<HSteamNetConnection, int>();
    private readonly string[] names = new string[MaxPlayers + 1];
    // Each player's identity in the campaign.
    private readonly string[] keys = new string[MaxPlayers + 1];
    private readonly ulong[] accounts = new ulong[MaxPlayers + 1];
    private readonly bool[] ready = new bool[MaxPlayers + 1];
    // Keepers that started chained in their bay: a new campaign's lobby, and players new to the
    // campaign joining while the host was chained.
    private readonly bool[] chained = new bool[MaxPlayers + 1];
    // Players known to everyone; one joining a running game is introduced with the game.
    private readonly bool[] introduced = new bool[MaxPlayers + 1];
    private readonly List<HSteamNetConnection> awaitingWorld = new List<HSteamNetConnection>();
    // Players who have the host's game and receive every change to it.
    private readonly bool[] playing = new bool[MaxPlayers + 1];
    // Host: players the starting party still waits for before anyone plays.
    private readonly HashSet<int> starting = new HashSet<int>();
    // Host: every player starting together, loaded or not, and how much of the game each has received.
    private readonly HashSet<int> entrants = new HashSet<int>();
    private readonly int[] receivedPercent = new int[MaxPlayers + 1];
    private readonly bool[] loadingGame = new bool[MaxPlayers + 1];
    // Changes to the host's game that arrive while it loads apply once it is running.
    private readonly List<(Message message, byte[] data)> pendingWorld = new List<(Message, byte[])>();
    private readonly bool[] freed = new bool[MaxPlayers + 1];
    // Players asleep; the night passes quickly only while everyone playing sleeps.
    private readonly bool[] resting = new bool[MaxPlayers + 1];
    // Chain changes that arrive while the host's game loads apply once it is running.
    private readonly List<(Message message, int slot, int trigger)> deferred = new List<(Message, int, int)>();
    private readonly MemoryStream packet = new MemoryStream();
    private BinaryWriter writer;
    private SteamTransport transport;
    private LanDiscovery discovery;
    private HSteamNetConnection host;
    private WorldSnapshot.Download download;
    // The host's game has arrived; it loads once the loading screen has shown so.
    private WorldSnapshot.Download arrived;
    private int arrivedFrame;
    private SaveSlotData campaign;
    private bool newCampaign;
    private bool worldArrived;
    private bool released;
    private byte[] pendingBays;
    private float holdingSince = -1f;
    private float releasedAt = -1f;
    private float nextState;
    private bool hostLoaded;
    private int sharedEntry = -1;
    private float nextEntryStatus;
    // Joined: when the host last sent anything towards this player's entry.
    private float hostContact;
    private float nextReceiveReport;

    internal static CoopSession Current { get; private set; }
    internal static event Action Admitted;
    // A joined player's entry into the host's game begins: the host started, or this player asked to join.
    internal static event Action Loading;
    internal static event Action<string> Failed;
    // A joined game lost its host after it started loading.
    internal static event Action<string> Closed;
    internal bool IsHost { get; private set; }
    internal int LocalSlot { get; private set; }
    // Players gather until the host starts the game.
    internal bool InLobby { get; private set; }
    // A joined player who was in the lobby when the host started.
    internal bool StartedTogether { get; private set; }
    // A joined player asked to join the running game.
    internal bool Joining { get; private set; }
    internal string CampaignLabel { get; private set; }
    // Host: the saved campaign hosted, or none for a new one.
    internal SaveSlotData Campaign => campaign;
    // Joined players follow the host's campaign instead of their own.
    internal static bool IsGuest => Current != null && !Current.IsHost;
    internal static bool IsHosting => Current != null && Current.IsHost;

    // The world's changes are shared while someone else plays in it.
    internal static bool SharesWorld
    {
        get
        {
            var session = Current;
            if (session == null || session.LocalSlot == 0 || !KeeperSpawn.Active)
                return false;
            if (!session.IsHost)
                return true;
            foreach (int slot in session.peers.Values)
            {
                if (session.playing[slot])
                    return true;
            }
            return false;
        }
    }

    // The host can start once everyone in the lobby is ready.
    internal bool AllReady
    {
        get
        {
            for (int slot = 1; slot <= MaxPlayers; slot++)
            {
                if (names[slot] != null && !ready[slot])
                    return false;
            }
            return true;
        }
    }

    // Hosts a lobby for a new campaign, or for the saved one given.
    internal static bool Host(SaveSlotData saved, out string error)
    {
        if (!Create(out error))
            return false;
        var session = Current;
        try
        {
            session.discovery = LanDiscovery.Host();
        }
        catch (SocketException exception)
        {
            Stop();
            error = "LAN discovery is unavailable: " + exception.Message;
            return false;
        }
        if (!session.transport.Listen(GamePort))
        {
            Stop();
            error = $"UDP port {GamePort} is already in use.";
            return false;
        }
        session.IsHost = true;
        session.InLobby = true;
        session.LocalSlot = HostSlot;
        session.campaign = saved;
        session.CampaignLabel = saved == null ? "New game" : $"Saved game, day {saved.day}";
        CharacterRecords.Clear();
        if (saved != null)
            CharacterRecords.LoadCampaign(saved);
        session.names[1] = SteamFriends.GetPersonaName();
        session.accounts[1] = PlayerIdentity.SteamId;
        session.ready[1] = true;
        session.introduced[1] = true;
        KeeperSpawn.KeeperAdded += session.ShareRescue;
        KeeperSpawn.BaysOpened += session.ShareBays;
        Debug.Log($"[Multiplayer] Hosting a lobby for {session.CampaignLabel} on UDP port {GamePort}");
        return true;
    }

    internal static bool Join(IPEndPoint endpoint, out string error)
    {
        if (!Create(out error))
            return false;
        Current.host = Current.transport.Connect(endpoint);
        KeeperSpawn.CutRequested += Current.RequestCut;
        Debug.Log($"[Multiplayer] Connecting to {endpoint}");
        return true;
    }

    internal static void Stop()
    {
        var session = Current;
        if (session == null)
            return;
        // A joined player's borrowed stations and keeper travel back to the host's campaign as they leave.
        if (!session.IsHost && session.LocalSlot != 0 && session.playing[session.LocalSlot] && KeeperSpawn.Active)
        {
            StationLeases.ReturnAll();
            session.ShareCharacter(leaving: true);
        }
        Current = null;
        MainGame.OnGoToMainMenu -= Stop;
        MainGame.OnGameStarted -= session.HostStarted;
        KeeperSpawn.KeeperAdded -= session.ShareRescue;
        KeeperSpawn.BaysOpened -= session.ShareBays;
        KeeperSpawn.CutRequested -= session.RequestCut;
        session.transport.Dispose();
        session.discovery?.Dispose();
        EntryBarrier.Cancel();
        EntryStatus.End();
        SharedPresentation.Clear();
        RemoteKeeper.Clear();
        WorldSync.Clear();
        ObjectMotion.Clear();
        WorldDrops.Clear();
        WorldClock.Clear();
        CharacterRecords.Clear();
        PersonalGrants.Clear();
        DropClaims.Clear();
        StationLeases.Clear();
        RestAgreement.Clear();
        // A joined campaign's keepers belong to its host.
        if (!session.IsHost)
            KeeperSpawn.Disable();
        Destroy(session.gameObject);
    }

    internal string PlayerName(int slot) => names[slot];

    internal bool IsReady(int slot) => ready[slot];

    // Everyone in the lobby starts together; in a new campaign they all wake chained in their bays.
    internal bool StartGame()
    {
        if (!IsHost || !InLobby || !AllReady)
            return false;
        InLobby = false;
        newCampaign = campaign == null;
        EntryBarrier.Expect();
        if (newCampaign)
            KeeperSpawn.Enable(LocalSlot);
        else
            KeeperSpawn.EnableContinued(LocalSlot);
        MainGame.OnGameStarted += HostStarted;
        chained[LocalSlot] = newCampaign;
        hostLoaded = false;
        sharedEntry = -1;
        EntryStatus.BeginHost(peers.Count + 1);
        foreach (var peer in peers)
        {
            starting.Add(peer.Value);
            entrants.Add(peer.Value);
            receivedPercent[peer.Value] = 0;
            loadingGame[peer.Value] = false;
            awaitingWorld.Add(peer.Key);
            if (!newCampaign)
                continue;
            chained[peer.Value] = true;
            KeeperSpawn.Join(peer.Value, true);
        }
        Compose(Message.Started, LocalSlot).Write(newCampaign);
        Broadcast(true);
        Debug.Log($"[Multiplayer] Starting {CampaignLabel} with {peers.Count + 1} players");
        return true;
    }

    internal void SetReady(bool isReady)
    {
        if (IsHost || LocalSlot == 0 || Joining || playing[LocalSlot] || !InLobby && StartedTogether)
            return;
        ready[LocalSlot] = isReady;
        Compose(Message.Ready, LocalSlot).Write(isReady);
        Broadcast(true);
    }

    // A player in the lobby of a running game asks for the game once ready.
    internal void JoinGame()
    {
        if (IsHost || InLobby || StartedTogether || Joining || !ready[LocalSlot])
            return;
        Joining = true;
        Compose(Message.JoinGame, LocalSlot);
        Broadcast(true);
        BeginEntry(later: true);
    }

    // A joined game has loaded the host's game; a starting party waits for the host's release.
    internal void Entered(bool barrier)
    {
        if (pendingBays != null)
        {
            WorldSnapshot.OpenHostBays(new BinaryReader(new MemoryStream(pendingBays)));
            pendingBays = null;
        }
        if (!barrier || released)
        {
            if (barrier)
                EntryBarrier.Release();
            EntryStatus.End();
            return;
        }
        EntryBarrier.Hold();
        EntryStatus.Wait();
        Compose(Message.Loaded, LocalSlot);
        Broadcast(true);
    }

    // A joined player's loading screen replaces its lobby as soon as its entry begins.
    private void BeginEntry(bool later)
    {
        EntryStatus.BeginJoined(names[1], later);
        hostContact = Time.unscaledTime;
        Loading?.Invoke();
    }

    internal static void SendTrigger(int trigger)
    {
        var session = Current;
        // A joined player's chains follow the host, which shares every chain motion itself.
        if (session == null || session.LocalSlot == 0 || !RemoteKeeper.CanShare ||
            !session.IsHost && KeeperChains.IsChainTrigger(trigger))
            return;
        session.Compose(Message.Trigger, session.LocalSlot).Write(trigger);
        session.Broadcast(true);
    }

    // A joined player asks the host to struggle against its last shackle.
    internal static void RequestStruggle()
    {
        var session = Current;
        if (session == null || session.IsHost)
            return;
        session.Compose(Message.Struggle, session.LocalSlot);
        session.Broadcast(true);
    }

    // A joined player's story transition waits for the host's approval.
    internal static void RequestQuest(QuestSync.Transition transition, string id, float argument) =>
        Current?.SendToHost(Message.QuestRequest, writer => QuestSync.WriteRequest(writer, transition, id, argument));

    // A joined player's pickup waits for the host to hand the item over.
    internal static void RequestClaim(Action<BinaryWriter> write) => Current?.SendToHost(Message.Claim, write);

    // A joined player borrows a chest or station from the host and gives it back.
    internal static void RequestStation(Action<BinaryWriter> write) => Current?.SendToHost(Message.Borrow, write);

    internal static void ReturnStation(Action<BinaryWriter> write) => Current?.SendToHost(Message.Return, write);

    // This game's keeper fell asleep or woke.
    internal static void ShareRest(bool isResting)
    {
        var session = Current;
        if (session == null)
            return;
        if (session.IsHost)
            session.Rested(session.LocalSlot, isResting);
        else
            session.SendToHost(Message.Rest, writer => writer.Write(isResting));
    }

    internal static void ShareChanges(byte[] data, int length) => Current?.ShareWorld(Message.Changes, data, length, true);

    internal static void ShareMotion(byte[] data, int length) => Current?.ShareWorld(Message.Motion, data, length, false);

    internal static void ShareClock(byte[] data) => Current?.ShareWorld(Message.Clock, data, data.Length, true);

    // What this game shows of its cutscenes and conversations, for the players in its scene.
    internal static void ShareCue(byte[] data, int length, bool reliable) => Current?.ShareWorld(Message.Cue, data, length, reliable);

    private static bool Create(out string error)
    {
        Stop();
        error = SteamManager.Initialized ? null : "Multiplayer requires Steam to be running.";
        if (error != null)
            return false;
        var owner = new GameObject("Multiplayer Session");
        DontDestroyOnLoad(owner);
        Current = owner.AddComponent<CoopSession>();
        return true;
    }

    private void Awake()
    {
        writer = new BinaryWriter(packet);
        transport = new SteamTransport();
        transport.Connected += OnConnected;
        transport.Disconnected += OnDisconnected;
        MainGame.OnGoToMainMenu += Stop;
    }

    private void Update()
    {
        transport.Receive(Handle);
        if (Current != this)
            return;
        if (IsHost)
        {
            discovery.Reply(names[1], peers.Count + 1, InLobby, GamePort);
            SendWorlds();
            ShareEntry();
            TryRelease();
            if (releasedAt >= 0f && Time.unscaledTime - releasedAt > BaysTimeout)
                ReleasePlayers();
            FreeStranded();
        }
        else
        {
            if (!WatchEntry())
                return;
            LoadArrivedGame();
            if (KeeperSpawn.Active)
            {
                foreach (var (message, slot, trigger) in deferred)
                    ApplyChains(message, slot, trigger);
                deferred.Clear();
                foreach (var (message, data) in pendingWorld)
                    ApplyWorld(message, new BinaryReader(new MemoryStream(data)));
                pendingWorld.Clear();
                WorldClock.Advance();
                ShareCharacter(leaving: false);
            }
        }
        if (KeeperSpawn.Active)
        {
            ObjectMotion.Follow();
            StationLeases.Update();
            if (!EntryBarrier.Waiting)
                PersonalGrants.Deliver();
        }
        if (LocalSlot == 0 || !RemoteKeeper.CanShare || Time.unscaledTime < nextState)
            return;
        nextState = Time.unscaledTime + StateInterval;
        RemoteKeeper.WriteLocal(Compose(Message.State, LocalSlot));
        Broadcast(false);
    }

    // The frame's changes to the world go out once it has made them all.
    private void LateUpdate()
    {
        if (Current != this || !SharesWorld)
            return;
        ObjectMotion.Share();
        WorldDrops.Settle();
        WorldClock.Share();
        WorldSync.Flush();
        WatchedCutscene.Share();
        RemoteWisps.Share();
    }

    private void OnApplicationQuit() => Stop();

    private void OnConnected(HSteamNetConnection connection)
    {
        if (IsHost)
            return;
        Compose(Message.Hello, 0).Write(Protocol);
        writer.Write(SteamFriends.GetPersonaName());
        writer.Write(PlayerIdentity.SteamId);
        writer.Write(PlayerIdentity.Profile);
        Send(connection, true);
    }

    private void OnDisconnected(HSteamNetConnection connection, bool closedByPeer)
    {
        if (IsHost)
        {
            if (!peers.TryGetValue(connection, out int slot))
                return;
            peers.Remove(connection);
            awaitingWorld.Remove(connection);
            if (introduced[slot])
            {
                Compose(Message.Left, slot);
                Broadcast(true);
            }
            Leave(slot);
            return;
        }
        // A refusal can arrive just before the host closes the connection.
        transport.Receive(Handle);
        if (Current != this)
            return;
        // The lobby ends with its host.
        string reason = LocalSlot == 0 ? "Could not reach the host."
            : !closedByPeer ? "Lost connection to the host."
            : InLobby ? "The host closed the lobby." : "The host closed the game.";
        if (!WorldSnapshot.IsLoading && !KeeperSpawn.Active)
        {
            Fail(reason);
            return;
        }
        Debug.Log("[Multiplayer] " + reason);
        Stop();
        Closed?.Invoke(reason);
    }

    private void Handle(HSteamNetConnection connection, byte[] data, int length)
    {
        if (length < 2 || data[1] > MaxPlayers)
            return;
        var message = (Message)data[0];
        int slot = data[1];
        var reader = new BinaryReader(new MemoryStream(data, 2, length - 2));
        if (IsHost)
        {
            if (message == Message.Hello)
            {
                Admit(connection, reader.ReadByte(), reader.ReadString(), reader.ReadUInt64(), reader.ReadString());
                return;
            }
            // Players speak only for their own keeper.
            if (!peers.TryGetValue(connection, out slot) || !HandlePlayer(connection, message, slot, reader, data, length))
                return;
        }
        else if (HandleJoined(message, slot, reader, data, length) || slot == 0 ||
                 slot == LocalSlot && message == Message.State)
        {
            return;
        }
        switch (message)
        {
            case Message.State:
                RemoteKeeper.Read(slot, reader);
                break;
            case Message.Trigger:
            case Message.Freed:
                int trigger = message == Message.Trigger ? reader.ReadInt32() : 0;
                if (!IsHost && !KeeperSpawn.Active)
                    deferred.Add((message, slot, trigger));
                else
                    ApplyChains(message, slot, trigger);
                break;
        }
    }

    // Messages the host receives from a player; returns whether the host shows them as well.
    private bool HandlePlayer(HSteamNetConnection connection, Message message, int slot, BinaryReader reader,
        byte[] data, int length)
    {
        switch (message)
        {
            case Message.Ready:
                // Ready counts in the lobby, and for a later player before joining the running game.
                if (!InLobby && (playing[slot] || awaitingWorld.Contains(connection)))
                    return false;
                ready[slot] = reader.ReadBoolean();
                Compose(Message.Ready, slot).Write(ready[slot]);
                Broadcast(true);
                if (!introduced[slot])
                    Send(connection, true);
                return false;
            case Message.JoinGame:
                if (!InLobby && ready[slot] && !playing[slot] && !awaitingWorld.Contains(connection))
                    awaitingWorld.Add(connection);
                return false;
            case Message.Loaded:
                if (starting.Remove(slot))
                    Debug.Log($"[Multiplayer] Keeper {slot} has loaded the campaign");
                return false;
            case Message.LoadReport:
                if (starting.Contains(slot))
                {
                    loadingGame[slot] = reader.ReadBoolean();
                    receivedPercent[slot] = reader.ReadByte();
                }
                return false;
            case Message.Character:
                if (playing[slot])
                    CharacterRecords.Store(keys[slot], slot, reader);
                return false;
            case Message.Struggle:
                var chains = KeeperSpawn.Keeper(slot);
                if (chains != null)
                    chains.TryStruggle();
                return false;
            case Message.Cut:
                // A free keeper's pickaxe cuts another keeper's shackles; everyone sees the motion.
                int cut = reader.ReadByte();
                var shackles = cut > 1 && cut <= MaxPlayers && cut != slot && playing[slot] &&
                    RemoteKeeper.ReportedShackles(slot) == 0 ? KeeperSpawn.Keeper(cut) : null;
                if (shackles != null && !shackles.IsFree)
                    shackles.ApplyPickaxeHit();
                return false;
            case Message.Claim:
                if (!playing[slot] || !KeeperSpawn.Active)
                    return false;
                // The answer reaches the player ahead of the drop's change, sent with this frame's changes.
                DropClaims.Grant(reader, Compose(Message.Granted, slot));
                Send(connection, true);
                return false;
            case Message.Borrow:
                if (!playing[slot] || !KeeperSpawn.Active)
                    return false;
                StationLeases.Lend(slot, reader, Compose(Message.Lend, slot));
                Send(connection, true);
                return false;
            case Message.Return:
                if (playing[slot] && KeeperSpawn.Active)
                    StationLeases.TakeBack(slot, reader);
                return false;
            case Message.Rest:
                if (playing[slot])
                    Rested(slot, reader.ReadBoolean());
                return false;
            case Message.QuestRequest:
                if (!playing[slot] || !KeeperSpawn.Active)
                    return false;
                var (transition, id, argument) = QuestSync.ReadRequest(reader);
                // The approval reaches the player ahead of the quest's new state, sent with this frame's changes.
                if (QuestSync.Approve(transition, id, argument))
                {
                    QuestSync.WriteRequest(Compose(Message.QuestApproved, slot), transition, id, 0f);
                    Send(connection, true);
                }
                return false;
            case Message.Cue:
                if (!playing[slot] || !KeeperSpawn.Active || length < 3)
                    return false;
                SharedPresentation.Apply(slot, reader);
                // The other players in its scene see it too.
                data[1] = (byte)slot;
                foreach (var peer in peers)
                {
                    if (peer.Key != connection && playing[peer.Value])
                        transport.Send(peer.Key, data, 0, length, !SharedPresentation.IsStream(data[2]));
                }
                return false;
            case Message.Changes:
            case Message.Motion:
                if (!playing[slot])
                    return false;
                ApplyWorld(message, reader);
                // The other players' games change too.
                data[1] = (byte)slot;
                foreach (var peer in peers)
                {
                    if (peer.Key != connection && playing[peer.Value])
                        transport.Send(peer.Key, data, 0, length, message == Message.Changes);
                }
                return false;
            case Message.State:
            case Message.Trigger:
                // Only the host moves chains.
                if (message == Message.Trigger && (length < 6 || KeeperChains.IsChainTrigger(BitConverter.ToInt32(data, 2))))
                    return false;
                // The other players see this keeper too.
                data[1] = (byte)slot;
                foreach (var peer in peers)
                {
                    if (peer.Key != connection && introduced[peer.Value])
                        transport.Send(peer.Key, data, 0, length, message == Message.Trigger);
                }
                return true;
        }
        return false;
    }

    // Messages only a joined player receives; returns whether the message was consumed.
    private bool HandleJoined(Message message, int slot, BinaryReader reader, byte[] data, int length)
    {
        switch (message)
        {
            case Message.Welcome:
                LocalSlot = slot;
                InLobby = reader.ReadBoolean();
                CampaignLabel = reader.ReadString();
                KeeperSpawn.EnableJoined(slot);
                Debug.Log($"[Multiplayer] Joined as Keeper {slot}" + (InLobby ? " in the lobby" : " while the game runs"));
                Admitted?.Invoke();
                return true;
            case Message.Refused:
                Fail(reader.ReadString());
                return true;
            case Message.Joined:
                if (slot == 0)
                    return true;
                names[slot] = reader.ReadString();
                ready[slot] = reader.ReadBoolean();
                chained[slot] = reader.ReadBoolean();
                if (slot == LocalSlot || introduced[slot])
                    return true;
                introduced[slot] = true;
                KeeperSpawn.Join(slot, chained[slot]);
                Notify(InLobby || !KeeperSpawn.Active ? $"{names[slot]} joined the lobby" : $"{names[slot]} joined as Keeper {slot}");
                return true;
            case Message.Left:
                Leave(slot);
                return true;
            case Message.Ready:
                ready[slot] = reader.ReadBoolean();
                return true;
            case Message.Started:
                InLobby = false;
                StartedTogether = true;
                // Everyone in a new campaign's lobby wakes chained in their own bay.
                if (reader.ReadBoolean())
                {
                    for (int member = 1; member <= MaxPlayers; member++)
                    {
                        if (names[member] == null)
                            continue;
                        chained[member] = true;
                        KeeperSpawn.Join(member, true);
                    }
                }
                Debug.Log("[Multiplayer] The host is starting the game");
                BeginEntry(later: false);
                return true;
            case Message.LoadStatus:
                if (!StartedTogether || EntryStatus.Current == EntryStatus.Phase.None)
                    return true;
                hostContact = Time.unscaledTime;
                EntryStatus.HostReport(reader.ReadByte() / 100f, reader.ReadByte(), reader.ReadByte());
                return true;
            case Message.Cue:
                if (KeeperSpawn.Active && worldArrived)
                    SharedPresentation.Apply(slot, reader);
                return true;
            case Message.QuestApproved:
                if (KeeperSpawn.Active)
                {
                    var (transition, id, _) = QuestSync.ReadRequest(reader);
                    QuestSync.Execute(transition, id);
                }
                return true;
            case Message.Granted:
                if (KeeperSpawn.Active)
                    DropClaims.Receive(reader);
                return true;
            case Message.Lend:
                if (KeeperSpawn.Active)
                    StationLeases.Receive(reader);
                return true;
            case Message.Rest:
                if (reader.ReadBoolean() && slot != LocalSlot && KeeperSpawn.Active)
                    Notify($"{names[slot]} is sleeping.");
                return true;
            case Message.Release:
                released = true;
                if (KeeperSpawn.Active)
                {
                    EntryBarrier.Release();
                    EntryStatus.End();
                }
                return true;
            case Message.Bays:
                if (KeeperSpawn.Active)
                    WorldSnapshot.OpenHostBays(reader);
                else
                    pendingBays = reader.ReadBytes(length - 2);
                return true;
            case Message.World:
                if (LocalSlot == 0 || InLobby || download != null || arrived != null || WorldSnapshot.IsLoading || KeeperSpawn.Active)
                    return true;
                // The game was captured after every chain change sent before it.
                deferred.Clear();
                download = WorldSnapshot.Download.Begin(reader);
                if (download == null)
                {
                    Fail("The host's game could not be received.");
                    return true;
                }
                worldArrived = true;
                playing[LocalSlot] = true;
                if (EntryStatus.Current == EntryStatus.Phase.None)
                    BeginEntry(later: !StartedTogether);
                hostContact = Time.unscaledTime;
                EntryStatus.Download(0f);
                return true;
            case Message.Changes:
            case Message.Motion:
            case Message.Clock:
                // Changes before the host's game was captured are part of it.
                if (!worldArrived)
                    return true;
                if (KeeperSpawn.Active)
                    ApplyWorld(message, reader);
                else if (message != Message.Motion)
                    pendingWorld.Add((message, reader.ReadBytes(length - 2)));
                return true;
            case Message.WorldPart:
                if (download == null)
                    return true;
                if (!download.Add(data, 2, length - 2))
                {
                    download = null;
                    Fail("The host's game could not be received.");
                    return true;
                }
                hostContact = Time.unscaledTime;
                EntryStatus.Download(download.Progress);
                if (!download.Complete)
                    return true;
                arrived = download;
                arrivedFrame = Time.frameCount;
                download = null;
                return true;
        }
        return false;
    }

    private static void ApplyWorld(Message message, BinaryReader reader)
    {
        switch (message)
        {
            case Message.Changes:
                WorldSync.Apply(reader);
                break;
            case Message.Motion:
                ObjectMotion.Apply(reader);
                break;
            case Message.Clock:
                WorldClock.Apply(reader);
                break;
        }
    }

    // The host's own chain motions come from its native opening; everyone else's from the host.
    private void ApplyChains(Message message, int slot, int trigger)
    {
        if (slot != LocalSlot)
        {
            if (message == Message.Trigger)
                RemoteKeeper.Trigger(slot, trigger);
            else
                RemoteKeeper.Free(slot);
            return;
        }
        var chains = KeeperSpawn.Keeper(slot);
        if (chains == null)
            return;
        if (message == Message.Trigger)
            chains.Mirror(trigger);
        else if (!chains.IsFree)
            chains.ReleaseNow();
    }

    private void Admit(HSteamNetConnection connection, int protocol, string name, ulong account, string profile)
    {
        if (peers.ContainsKey(connection))
            return;
        int slot = Array.IndexOf(names, null, 2);
        // Games sharing one Steam account are told apart by their profiles.
        bool sharedAccount = Array.IndexOf(accounts, account, 1) >= 0;
        string key = PlayerIdentity.Key(account, profile, sharedAccount);
        string refusal = protocol != Protocol ? "The host uses a different version of the mod."
            : Array.IndexOf(keys, key) >= 0 ? "You are already in this game."
            : slot < 0 ? "The game is full." : null;
        if (refusal != null)
        {
            Compose(Message.Refused, 0).Write(refusal);
            Send(connection, true);
            transport.Close(connection);
            return;
        }
        peers[connection] = slot;
        names[slot] = name;
        keys[slot] = key;
        accounts[slot] = account;
        ready[slot] = false;
        chained[slot] = false;
        freed[slot] = false;
        Compose(Message.Welcome, slot).Write(InLobby);
        writer.Write(CampaignLabel);
        Send(connection, true);
        // The lobby shows who is here; a player joining a running game is announced as they enter it.
        for (int member = 1; member <= MaxPlayers; member++)
        {
            if (member != slot && !introduced[member])
                continue;
            WriteMember(Compose(Message.Joined, member), member);
            Send(connection, true);
        }
        if (InLobby)
            Announce(slot);
    }

    // Everyone learns about a player as they enter the lobby or the running game.
    private void Announce(int slot)
    {
        introduced[slot] = true;
        WriteMember(Compose(Message.Joined, slot), slot);
        Broadcast(true);
        Notify(InLobby ? $"{names[slot]} joined the lobby" : $"{names[slot]} joined as Keeper {slot}");
    }

    private void WriteMember(BinaryWriter member, int slot)
    {
        member.Write(names[slot]);
        member.Write(ready[slot]);
        member.Write(chained[slot]);
    }

    private void HostStarted()
    {
        MainGame.OnGameStarted -= HostStarted;
        if (Current != this)
            return;
        EntryBarrier.Hold();
        holdingSince = Time.unscaledTime;
        hostLoaded = true;
        EntryStatus.Wait();
    }

    // Everyone starting together sees how the host's loading goes and who has loaded; the host sees
    // how much of its game the players have received.
    private void ShareEntry()
    {
        if (!EntryStatus.IsHost || EntryStatus.Current == EntryStatus.Phase.None)
            return;
        int loaded = hostLoaded ? 1 : 0, lowest = 100;
        bool sending = false;
        foreach (int slot in entrants)
        {
            if (!starting.Contains(slot))
            {
                loaded++;
            }
            else if (!loadingGame[slot])
            {
                sending = true;
                lowest = Math.Min(lowest, receivedPercent[slot]);
            }
        }
        int total = entrants.Count + 1;
        // Nothing is sent before the host's own game has loaded.
        EntryStatus.Count(loaded, total, hostLoaded && sending ? lowest / 100f : -1f);
        int progress = hostLoaded ? 100 : Mathf.FloorToInt(Mathf.Clamp01(EntryStatus.OwnProgress) * 100f);
        int entry = progress | loaded << 8 | total << 16;
        if (entry == sharedEntry && Time.unscaledTime < nextEntryStatus)
            return;
        sharedEntry = entry;
        nextEntryStatus = Time.unscaledTime + EntryStatusInterval;
        Compose(Message.LoadStatus, LocalSlot).Write((byte)progress);
        writer.Write((byte)loaded);
        writer.Write((byte)total);
        Broadcast(true);
    }

    // A joined player tells the host how much of its game has arrived, and gives up on a host that
    // stops sending; false once it has.
    private bool WatchEntry()
    {
        var phase = EntryStatus.Current;
        if (phase is not (EntryStatus.Phase.HostLoading or EntryStatus.Phase.Requesting or EntryStatus.Phase.Downloading))
            return true;
        if (Time.unscaledTime - hostContact > EntryTimeout)
        {
            Fail("The host stopped sending its game.");
            return false;
        }
        if (phase == EntryStatus.Phase.Downloading && Time.unscaledTime >= nextReceiveReport)
        {
            nextReceiveReport = Time.unscaledTime + ReceiveReportInterval;
            ReportEntry(loading: false);
        }
        return true;
    }

    // Opening the host's game holds the frame for a while, so the loading screen first draws a
    // whole frame saying that it loads.
    private void LoadArrivedGame()
    {
        if (arrived == null || Time.frameCount <= arrivedFrame + 1)
            return;
        var world = arrived;
        arrived = null;
        WorldSnapshot.Load(world);
        ReportEntry(loading: true);
    }

    private void ReportEntry(bool loading) =>
        SendToHost(Message.LoadReport, report =>
        {
            report.Write(loading);
            report.Write((byte)Mathf.FloorToInt(Mathf.Clamp01(EntryStatus.Received) * 100f));
        });

    // The party plays once everyone starting together has loaded, or after the time limit.
    private void TryRelease()
    {
        if (holdingSince < 0f || starting.Count > 0 && Time.unscaledTime - holdingSince < BarrierTimeout)
            return;
        if (starting.Count > 0)
            Debug.LogWarning($"[Multiplayer] Starting without {starting.Count} players who did not finish loading");
        holdingSince = -1f;
        starting.Clear();
        entrants.Clear();
        EntryBarrier.Release();
        EntryStatus.End();
        // A new campaign's story places the bays before the others are released into them.
        releasedAt = Time.unscaledTime;
        if (!newCampaign || KeeperSpawn.BaysOpen)
            ReleasePlayers();
    }

    private void ReleasePlayers()
    {
        releasedAt = -1f;
        Compose(Message.Release, LocalSlot);
        ShareToPlaying(true);
    }

    // The bays opened with the host's story; players already in the game place their keepers.
    private void ShareBays()
    {
        if (Current != this)
            return;
        WorldSnapshot.WriteBays(Compose(Message.Bays, LocalSlot));
        ShareToPlaying(true);
        if (releasedAt >= 0f)
            ReleasePlayers();
    }

    private void ShareCharacter(bool leaving)
    {
        CharacterRecords.Report(Compose(Message.Character, LocalSlot), out bool due, leaving);
        if (due)
            Send(host, true);
    }

    // Players admitted while the host's game loads, and later players who chose to join, receive it
    // once it is running.
    private void SendWorlds()
    {
        if (awaitingWorld.Count == 0 || !WorldSnapshot.CanCapture)
            return;
        // Players already playing receive this frame's changes before the new players' copy is taken.
        WorldSync.Flush();
        var receivers = awaitingWorld.ToArray();
        awaitingWorld.Clear();
        var placements = new Dictionary<int, WorldSnapshot.Placement>();
        foreach (var connection in receivers)
        {
            if (!peers.TryGetValue(connection, out int slot))
                continue;
            var placement = WorldSnapshot.Place(keys[slot], slot, chained[slot] && starting.Contains(slot));
            placements[slot] = placement;
            chained[slot] = placement.Shackles > 0;
            KeeperSpawn.Join(slot, chained[slot]);
            if (placement.Shackles == 1)
                KeeperSpawn.Keeper(slot)?.Restore(1);
            if (!introduced[slot])
                Announce(slot);
        }
        byte[] world = WorldSnapshot.Capture();
        foreach (var connection in receivers)
        {
            if (!peers.TryGetValue(connection, out int slot) || !placements.TryGetValue(slot, out var placement))
                continue;
            bool sent = world.Length > 0;
            if (sent)
            {
                WorldSnapshot.WritePlan(Compose(Message.World, slot), placement, world.Length);
                sent = Send(connection, true);
            }
            for (int offset = 0; sent && offset < world.Length; offset += WorldPartSize)
            {
                Compose(Message.WorldPart, slot).Write(world, offset, Math.Min(WorldPartSize, world.Length - offset));
                sent = Send(connection, true);
            }
            playing[slot] = sent;
            if (sent)
            {
                // A player arriving awake keeps the others' night at its normal pace.
                UpdateRest();
                continue;
            }
            Debug.LogWarning($"[Multiplayer] Could not send the game to Keeper {slot}");
            Compose(Message.Refused, 0).Write("The host's game could not be sent.");
            Send(connection, true);
            transport.Close(connection);
            // A connection closed here reports no disconnection.
            OnDisconnected(connection, false);
        }
    }

    // The host decides every rescue in its prison; the players see and play its chain motions.
    private void ShareRescue(int slot, KeeperChains chains)
    {
        chains.MotionStarted += trigger =>
        {
            if (Current != this)
                return;
            Compose(Message.Trigger, slot).Write(trigger);
            Broadcast(true);
        };
    }

    private void RequestCut(int slot)
    {
        if (Current == this)
            SendToHost(Message.Cut, writer => writer.Write((byte)slot));
    }

    // Host: a player fell asleep or woke; everyone sees who sleeps.
    private void Rested(int slot, bool isResting)
    {
        if (resting[slot] == isResting)
            return;
        resting[slot] = isResting;
        Compose(Message.Rest, slot).Write(isResting);
        ShareToPlaying(true);
        if (isResting && slot != LocalSlot)
            Notify($"{names[slot]} is sleeping.");
        UpdateRest();
    }

    // Host: the night passes quickly only while everyone playing sleeps.
    private void UpdateRest()
    {
        bool everyone = resting[LocalSlot];
        foreach (int slot in peers.Values)
        {
            if (playing[slot] && !resting[slot])
                everyone = false;
        }
        RestAgreement.FastForward(everyone);
    }

    // Keepers still chained when the host's prison unloads are freed where they stand.
    private void FreeStranded()
    {
        if (!KeeperSpawn.BaysWereOpen || KeeperSpawn.BaysOpen)
            return;
        foreach (int slot in peers.Values)
        {
            if (freed[slot] || RemoteKeeper.ReportedShackles(slot) == 0)
                continue;
            freed[slot] = true;
            Compose(Message.Freed, slot);
            Broadcast(true);
            RemoteKeeper.Free(slot);
        }
    }

    private void Leave(int slot)
    {
        if (slot == 0 || names[slot] == null)
            return;
        if (IsHost && keys[slot] != null)
        {
            // A player returning to this session finds their keeper as they left it.
            var chains = KeeperSpawn.Keeper(slot);
            CharacterRecords.RememberChained(keys[slot], chains != null && !chains.IsFree ? chains.RemainingShackles : 0);
            StationLeases.Forget(slot);
        }
        if (introduced[slot])
            Notify(InLobby ? $"{names[slot]} left the lobby" : $"{names[slot]} left the game");
        names[slot] = null;
        keys[slot] = null;
        accounts[slot] = 0;
        ready[slot] = false;
        chained[slot] = false;
        introduced[slot] = false;
        playing[slot] = false;
        resting[slot] = false;
        starting.Remove(slot);
        entrants.Remove(slot);
        SharedPresentation.Forget(slot);
        if (IsHost && KeeperSpawn.Active)
            UpdateRest();
        RemoteKeeper.Release(slot);
        KeeperSpawn.Leave(slot);
    }

    // The host sends to every player who has its game; players send to the host.
    private void ShareWorld(Message message, byte[] data, int length, bool reliable)
    {
        Compose(message, LocalSlot).Write(data, 0, length);
        ShareToPlaying(reliable);
    }

    private void ShareToPlaying(bool reliable)
    {
        if (!IsHost)
        {
            Send(host, reliable);
            return;
        }
        foreach (var peer in peers)
        {
            if (playing[peer.Value])
                Send(peer.Key, reliable);
        }
    }

    private void SendToHost(Message message, Action<BinaryWriter> write)
    {
        if (IsHost)
            return;
        write(Compose(message, LocalSlot));
        Send(host, true);
    }

    private BinaryWriter Compose(Message message, int slot)
    {
        packet.SetLength(0);
        writer.Write((byte)message);
        writer.Write((byte)slot);
        return writer;
    }

    private bool Send(HSteamNetConnection connection, bool reliable) =>
        transport.Send(connection, packet.GetBuffer(), 0, (int)packet.Length, reliable);

    // The host sends to every introduced player; players send to the host.
    private void Broadcast(bool reliable)
    {
        if (!IsHost)
        {
            Send(host, reliable);
            return;
        }
        foreach (var peer in peers)
        {
            if (introduced[peer.Value])
                Send(peer.Key, reliable);
        }
    }

    private static void Fail(string reason)
    {
        Debug.Log("[Multiplayer] " + reason);
        Stop();
        Failed?.Invoke(reason);
    }

    private static void Notify(string text)
    {
        Debug.Log("[Multiplayer] " + text);
        if (MainGame.Instance.gameState == MainGame.GameState.InGame)
            LazySingleton<UINotificator>.Instance.ShowSimpleTextNotification(text);
    }
}
