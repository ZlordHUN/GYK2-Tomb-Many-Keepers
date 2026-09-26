namespace GYK2.TombManyKeepers.Multiplayer.Session;

// What this game is doing on its way into a multiplayer game, for its loading screen. The host
// loads its campaign, sends it and waits for everyone starting together. A joined player waits for
// the host's game, or asks for it when joining later, downloads it, loads it and waits for everyone.
internal static class EntryStatus
{
    internal enum Phase
    {
        None,
        // This game loads: the host its campaign, a joined player the host's game.
        Loading,
        // A joined player starting together waits while the host's own game loads.
        HostLoading,
        // A later player has asked the host for its running game.
        Requesting,
        Downloading,
        // This game has loaded; everyone starting together waits until all have.
        Waiting
    }

    internal static Phase Current { get; private set; }
    // Counts entries, so each one's loading screen starts afresh.
    internal static int Entry { get; private set; }
    internal static bool IsHost { get; private set; }
    // A later player joining the running game rather than starting together.
    internal static bool Later { get; private set; }
    internal static string HostName { get; private set; }
    // Joined, starting together: how far the host's own loading has come.
    internal static float HostProgress { get; private set; }
    // Joined: the share of the host's game received.
    internal static float Received { get; private set; }
    // Host: the smallest share of its game a starting player has received; below zero once all have it.
    internal static float Sent { get; private set; } = -1f;
    internal static int Loaded { get; private set; }
    internal static int Total { get; private set; }
    // This game's own loading, as its loading screen measures it.
    internal static float OwnProgress { get; set; }

    internal static void BeginHost(int total)
    {
        Reset(Phase.Loading);
        IsHost = true;
        Total = total;
    }

    internal static void BeginJoined(string host, bool later)
    {
        Reset(later ? Phase.Requesting : Phase.HostLoading);
        Later = later;
        HostName = host;
    }

    // Joined, starting together: the host's report on its loading and on everyone's.
    internal static void HostReport(float progress, int loaded, int total)
    {
        HostProgress = progress;
        Loaded = loaded;
        Total = total;
    }

    // Host: who has loaded, and how much of its game the slowest starting player has received.
    internal static void Count(int loaded, int total, float sent)
    {
        Loaded = loaded;
        Total = total;
        Sent = sent;
    }

    internal static void Download(float received)
    {
        Current = Phase.Downloading;
        Received = received;
    }

    internal static void LoadGame() => Current = Phase.Loading;

    internal static void Wait() => Current = Phase.Waiting;

    internal static void End() => Current = Phase.None;

    private static void Reset(Phase phase)
    {
        Entry++;
        Current = phase;
        IsHost = Later = false;
        HostName = null;
        HostProgress = Received = OwnProgress = 0f;
        Sent = -1f;
        Loaded = Total = 0;
    }
}
