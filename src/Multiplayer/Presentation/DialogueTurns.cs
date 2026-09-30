using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Whose turn it is in this game's conversations and cutscenes. As in GYK1, every player watching one can progress it,
// and the one who progressed it last has the turn: their keeper says its next line, above their own keeper, and they
// answer its next choice. A line that ends by itself leaves the turn where it was. Each conversation begins with this
// game's own player: one ends as the story gives the keeper back, or, where it never took them, once nothing of it has
// shown for a while. Until every keeper is free and Larry is out of his sarcophagus, the host's keeper answers the
// fairy and the skull of its opening, whoever progresses it.
[HarmonyPatch]
internal static class DialogueTurns
{
    private const float Quiet = 2f;
    // The opening's quest that ends as the keeper breaks Larry's sarcophagus open.
    private const string LarryOut = "1_intro_prison_sarchphage_removed";
    private static int turn;
    private static float lastActive = float.NegativeInfinity;

    // The keeper that says this game's next conversation line and answers its next choice: a player in its scene.
    internal static int Turn
    {
        get
        {
            var session = CoopSession.Current;
            int local = session?.LocalSlot ?? 0;
            if (local == 0)
                return local;
            if (turn != local && (turn == 0 || Ended || Opening(session) || !SharedPresentation.Watches(turn) ||
                RemoteKeeper.BubblePoint(turn) == null))
                turn = local;
            return turn;
        }
    }

    private static bool Ended => MainGame.PlayerController == null ||
        MainGame.PlayerController.IsControlEnabledByType(TakenControlType.ByFlow) && Time.unscaledTime - lastActive > Quiet;

    // A line or choice of this game's conversation shows or closes now.
    internal static void Active() => lastActive = Time.unscaledTime;

    // A player progressed this game's conversation: theirs is the turn, but in the host's opening.
    internal static void Progressed(int slot)
    {
        Active();
        var session = CoopSession.Current;
        if (session != null && !Opening(session))
            turn = slot;
    }

    internal static void Reset()
    {
        turn = 0;
        lastActive = float.NegativeInfinity;
    }

    // The story gives this game's keeper back: its conversation has ended.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SetControlTakenType))]
    private static void ControlTaken(TakenControlType t, bool isEnabled)
    {
        if (t == TakenControlType.ByFlow && isEnabled)
            turn = 0;
    }

    private static bool Opening(CoopSession session)
    {
        if (!session.IsHost)
            return false;
        var quests = MainGame.Instance?.GameSave?.questSystemData;
        if (quests == null || !quests.questCollection.questsCache.TryGetValue(LarryOut, out var quest) ||
            quest.status != QuestStatus.Completed || RemoteKeeper.LocalShackles() > 0)
            return true;
        for (int slot = 2; slot <= CoopSession.MaxPlayers; slot++)
        {
            var chains = KeeperSpawn.Keeper(slot);
            if (chains != null && !chains.IsFree || RemoteKeeper.ReportedShackles(slot) > 0)
                return true;
        }
        return false;
    }
}
