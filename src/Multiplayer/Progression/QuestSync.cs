using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Multiplayer.Progression;

// One story for the whole campaign. Any player's action can start or finish a quest: the host
// orders these transitions so each happens once, and the player whose action caused one plays it
// out in their own game, so its dialogue, costs and rewards are theirs. Everyone else takes on the
// resulting quest state without replaying it.
[HarmonyPatch]
internal static class QuestSync
{
    internal enum Transition : byte
    {
        Start,
        Await,
        Complete,
        Cancel,
        Hidden,
        Unknown
    }

    private static readonly AccessTools.FieldRef<QuestSystemData, Action<QuestData>> Started =
        AccessTools.FieldRefAccess<QuestSystemData, Action<QuestData>>(nameof(QuestSystemData.OnQuestStarted));
    private static readonly AccessTools.FieldRef<QuestSystemData, Action<QuestData>> Completed =
        AccessTools.FieldRefAccess<QuestSystemData, Action<QuestData>>(nameof(QuestSystemData.OnQuestCompleted));
    private static readonly AccessTools.FieldRef<QuestSystemData, Action<QuestData>> Canceled =
        AccessTools.FieldRefAccess<QuestSystemData, Action<QuestData>>(nameof(QuestSystemData.OnQuestCanceled));
    // A joined player's game plays out a transition the host approved; what it causes runs natively too.
    private static int executing;

    private static QuestSystemData Quests => MainGame.Instance.GameSave.questSystemData;

    // Host: approves a joined player's transition if it still applies, taking on its state at once
    // so later requests see it. Returns whether the player's game plays it out; the host runs a
    // delayed transition itself when it falls due.
    internal static bool Approve(Transition transition, string id, float argument)
    {
        if (!Quests.questCollection.questsCache.TryGetValue(id, out var quest))
            return false;
        var status = quest.status;
        switch (transition)
        {
            case Transition.Hidden:
                Quests.ChangeQuestHiddenState(id, argument > 0f);
                return false;
            case Transition.Unknown:
                Quests.ChangeQuestUnknownState(id, argument > 0f);
                return false;
            case Transition.Start when status is QuestStatus.InProgress or QuestStatus.Completed or QuestStatus.Canceled:
            case Transition.Await when status is QuestStatus.Awaiting or QuestStatus.Completed or QuestStatus.Canceled:
            case Transition.Complete when status is QuestStatus.Completed or QuestStatus.Canceled:
            case Transition.Cancel when status is QuestStatus.Completed or QuestStatus.Canceled:
                return false;
        }
        if (argument > 0f)
        {
            Run(transition, id, argument);
            return false;
        }
        Quests.delayedQuests.Remove(id);
        Mirror(quest, Target(transition));
        Share(quest);
        return true;
    }

    // Joined player: plays out a transition the host approved.
    internal static void Execute(Transition transition, string id)
    {
        // The approval arrives before the host's state for the quest; should the state come first,
        // the transition still plays out here.
        if (Quests.questCollection.questsCache.TryGetValue(id, out var quest) && quest.status == Target(transition) &&
            transition is Transition.Start or Transition.Complete)
        {
            if (transition == Transition.Start && quest.Definition.finishCheck.hasTrigger)
                MainGame.Instance.GameSave.globalEventsSystem.RemoveEvent(quest.Definition.finishCheck);
            quest.status = transition == Transition.Start ? QuestStatus.Awaiting : QuestStatus.InProgress;
        }
        executing++;
        try
        {
            Run(transition, id, 0f);
        }
        finally
        {
            executing--;
        }
    }

    internal static void WriteRequest(BinaryWriter writer, Transition transition, string id, float argument)
    {
        writer.Write((byte)transition);
        writer.Write(id);
        writer.Write(argument);
    }

    internal static (Transition transition, string id, float argument) ReadRequest(BinaryReader reader) =>
        ((Transition)reader.ReadByte(), reader.ReadString(), reader.ReadSingle());

    // Another player's quest change, taken on as state.
    internal static void ApplyState(BinaryReader reader)
    {
        string id = reader.ReadString();
        var status = (QuestStatus)reader.ReadByte();
        bool hidden = reader.ReadBoolean();
        bool unknown = reader.ReadBoolean();
        if (!Quests.questCollection.questsCache.TryGetValue(id, out var quest))
            return;
        // A delayed transition someone else made has happened.
        if (Quests.delayedQuests.TryGetValue(id, out var delayed) &&
            (delayed.status == status || status is QuestStatus.Completed or QuestStatus.Canceled))
            Quests.delayedQuests.Remove(id);
        Mirror(quest, status);
        quest.isHidden = hidden;
        quest.isUnknown = unknown;
    }

    private static void Run(Transition transition, string id, float delay)
    {
        switch (transition)
        {
            case Transition.Start:
                Quests.StartQuest(id, delay);
                break;
            case Transition.Await:
                Quests.AwaitQuest(id, delay);
                break;
            case Transition.Complete:
                Quests.CompleteQuest(id, delay);
                break;
            case Transition.Cancel:
                Quests.CancelQuest(id);
                break;
        }
    }

    private static QuestStatus Target(Transition transition) => transition switch
    {
        Transition.Start => QuestStatus.InProgress,
        Transition.Await => QuestStatus.Awaiting,
        Transition.Complete => QuestStatus.Completed,
        _ => QuestStatus.Canceled
    };

    // Takes on a quest's state the way its native transition does, without running its expressions.
    private static void Mirror(QuestData quest, QuestStatus status)
    {
        if (quest.status == status)
            return;
        var definition = quest.Definition;
        var events = MainGame.Instance.GameSave.globalEventsSystem;
        if (quest.status == QuestStatus.Awaiting && definition.startCheck.hasTrigger)
            events.RemoveEvent(definition.startCheck);
        if (quest.status == QuestStatus.InProgress && definition.finishCheck.hasTrigger)
            events.RemoveEvent(definition.finishCheck);
        quest.status = status;
        var system = Quests;
        switch (status)
        {
            case QuestStatus.Awaiting:
                events.AddEvent(definition.startCheck);
                break;
            case QuestStatus.InProgress:
                if (definition.hasPosInBalance)
                    quest.isHidden = false;
                foreach (var other in system.questCollection.quests)
                {
                    if (other != null && other != quest && other.Definition != null && other.status == QuestStatus.Completed &&
                        other.Definition.TreePos == definition.TreePos)
                        other.isHidden = true;
                }
                if (definition.finishCheck.hasTrigger)
                    events.AddEvent(definition.finishCheck);
                Started(system)?.Invoke(quest);
                break;
            case QuestStatus.Completed:
                Completed(system)?.Invoke(quest);
                break;
            case QuestStatus.Canceled:
                if (!string.IsNullOrEmpty(definition.finishCheck.phrase))
                    MainGame.Instance.GameSave.knowledgeSystem.AddPhraseToBlackList(definition.finishCheck.phrase);
                Canceled(system)?.Invoke(quest);
                break;
        }
    }

    private static void Share(QuestData quest)
    {
        if (!WorldSync.Sharing)
            return;
        string id = quest.id;
        var status = quest.status;
        bool hidden = quest.isHidden;
        bool unknown = quest.isUnknown;
        WorldSync.Queue(WorldSync.Change.Quest, Guid.Empty, writer =>
        {
            writer.Write(id);
            writer.Write((byte)status);
            writer.Write(hidden);
            writer.Write(unknown);
        });
    }

    // Joined players' own transitions wait for the host's approval, and the host keeps time for
    // delayed ones; nothing else in their game advances the campaign's story.
    [HarmonyPatch]
    private static class Requests
    {
        private static readonly string[] Methods =
        {
            nameof(QuestSystemData.StartQuest), nameof(QuestSystemData.AwaitQuest), nameof(QuestSystemData.CompleteQuest),
            nameof(QuestSystemData.CancelQuest), nameof(QuestSystemData.ChangeQuestHiddenState),
            nameof(QuestSystemData.ChangeQuestUnknownState)
        };

        private static IEnumerable<MethodBase> TargetMethods() =>
            Methods.Select(name => (MethodBase)AccessTools.Method(typeof(QuestSystemData), name));

        private static bool Prefix(MethodBase __originalMethod, object[] __args)
        {
            // What another player's change causes plays out in that player's game.
            if (WorldSync.Applying)
                return false;
            if (!CoopSession.IsGuest)
                return true;
            var transition = (Transition)Array.IndexOf(Methods, __originalMethod.Name);
            float argument = __args.Length < 2 ? 0f : __args[1] is bool state ? state ? 1f : 0f : (float)__args[1];
            // What an approved transition causes plays out with it.
            if (executing > 0 && (transition > Transition.Complete || argument <= 0f))
                return true;
            if (CoopSession.SharesWorld)
                CoopSession.RequestQuest(transition, (string)__args[0], argument);
            return false;
        }
    }

    // Nor does another player's change fire this game's story triggers; theirs fired them.
    [HarmonyPatch(typeof(GlobalEventsSystem), nameof(GlobalEventsSystem.FireTrigger))]
    private static class Triggers
    {
        private static bool Prefix() => !WorldSync.Applying;
    }

    // Every quest change a game makes itself reaches the others as state.
    [HarmonyPatch]
    private static class Changes
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            nameof(QuestData.Start), nameof(QuestData.Await), nameof(QuestData.Complete), nameof(QuestData.Cancel)
        }.Select(name => (MethodBase)AccessTools.Method(typeof(QuestData), name));

        private static void Postfix(QuestData __instance) => Share(__instance);
    }

    [HarmonyPatch]
    private static class Visibility
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            nameof(QuestSystemData.ChangeQuestHiddenState), nameof(QuestSystemData.ChangeQuestUnknownState)
        }.Select(name => (MethodBase)AccessTools.Method(typeof(QuestSystemData), name));

        private static void Postfix(QuestSystemData __instance, string id)
        {
            if (__instance.questCollection.questsCache.TryGetValue(id, out var quest))
                Share(quest);
        }
    }
}
