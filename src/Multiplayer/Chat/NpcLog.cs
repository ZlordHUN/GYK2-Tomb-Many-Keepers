using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.Presentation;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Chat;

// The lines of this game's conversations and cutscenes, and the answers chosen in them, for every player's chat under
// NPCs: a character's line under the character, a keeper's under the player whose turn it was; and the names this game
// shows by the characters its keeper faces. Another player's line shown here and the chat's own bubbles are theirs to log.
[HarmonyPatch]
internal static class NpcLog
{
    private static readonly AccessTools.FieldRef<LazyWidget<UINpcWidgetData>, UINpcWidgetData> Faced =
        AccessTools.FieldRefAccess<LazyWidget<UINpcWidgetData>, UINpcWidgetData>("data");

    // A session's game is running, with or without other players yet: a player arriving later reads it all.
    private static bool Logs => CoopSession.Current is CoopSession session && session.LocalSlot != 0 && KeeperSpawn.Active;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIDialogBubble), nameof(UIDialogBubble.ShowMessage))]
    private static void Said(PhraseData data)
    {
        if (!Conversation.Saying || SharedPresentation.Applying || !Logs || string.IsNullOrEmpty(data.text))
            return;
        if (data.isPlayer)
            CoopSession.LogLine(DialogueTurns.Turn, null, data.text);
        else if (data.npcWgoData != null)
            CoopSession.LogLine(0, data.npcWgoData.id, data.text);
    }

    // The answer chosen in this game's conversation, as the keeper whose turn it was says it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GlobalEventsSystem), nameof(GlobalEventsSystem.FireTrigger))]
    private static void Answered(GlobalEventsSystem.Event.Type type, string id)
    {
        if (type == GlobalEventsSystem.Event.Type.MultiAnswerSay && !SharedPresentation.Applying && Logs && !string.IsNullOrEmpty(id))
            CoopSession.LogLine(DialogueTurns.Turn, null, id);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UINpcWidget), nameof(UINpcWidget.Redraw))]
    private static void Shown(UINpcWidget __instance)
    {
        if (Logs && Faced(__instance)?.WgoDef is WGODef character)
            CoopSession.NameShown(character.id);
    }
}
