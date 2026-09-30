using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Every line a player's game shows in a speech bubble, from its keeper, its wisp or someone in the
// world, appears in the same native bubble above the same speaker for the players in its scene. The
// line stays until the speaking player's game closes it. A line of a conversation or cutscene moves on
// as the game's own press moves it on, pressed by any player who sees it, as GYK1's dialogue did; the
// keeper's lines are said by the keeper whose turn it is. What a player says in the chat shows the same
// way above their keeper, as GYK1's chat bubbles, for the game's own time for a line of its length.
[HarmonyPatch]
internal static class SharedSpeech
{
    private enum Speaker : byte
    {
        Keeper,
        Wisp,
        Wgo
    }

    // A shared line closes with the speaking player's, never on its own.
    private const float HeldOpen = 3600f;
    private static readonly AccessTools.FieldRef<UISpeechBubble, bool> CanSkip =
        AccessTools.FieldRefAccess<UISpeechBubble, bool>("canSkipBubble");
    private static readonly AccessTools.FieldRef<UISpeechBubble, Func<bool>> ForceHide =
        AccessTools.FieldRefAccess<UISpeechBubble, Func<bool>>("onForceHideCondition");
    private static readonly AccessTools.FieldRef<UIDialogBubble, PhraseData> PhraseOf =
        AccessTools.FieldRefAccess<UIDialogBubble, PhraseData>("phraseData");
    private static readonly AccessTools.FieldRef<UISpeechBubble> NativeBubble =
        AccessTools.StaticFieldRefAccess<UISpeechBubble>(AccessTools.Field(typeof(UISpeechBubble), "instance"));
    private static readonly AccessTools.FieldRef<UISpeechBubble, SpeechBubbleSettings> BubbleSettings =
        AccessTools.FieldRefAccess<UISpeechBubble, SpeechBubbleSettings>("settings");
    private static readonly AccessTools.FieldRef<UISpeechBubble, TextAnimator> Letters =
        AccessTools.FieldRefAccess<UISpeechBubble, TextAnimator>("textAnimator");
    private static readonly AccessTools.FieldRef<UISpeechBubble, float> ShowTime =
        AccessTools.FieldRefAccess<UISpeechBubble, float>("bubbleShowTime");
    private static readonly AccessTools.FieldRef<UISpeechBubble, bool> Disappearing =
        AccessTools.FieldRefAccess<UISpeechBubble, bool>("disappearing");
    private static readonly AccessTools.FieldRef<UISpeechBubble, List<GameKey>> SkipKeys =
        AccessTools.FieldRefAccess<UISpeechBubble, List<GameKey>>("keysToSkip");
    private static readonly AccessTools.FieldRef<Func<bool>> SkipCondition =
        AccessTools.StaticFieldRefAccess<Func<bool>>(AccessTools.Field(typeof(UISpeechBubble), "customDialogSkipCondition"));
    private static readonly AccessTools.FieldRef<bool> Paused =
        AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(typeof(UISpeechBubble), "isPaused"));
    private static readonly Action<UIBasicBubble, Vector3, UIBasicBubble.ForceCornerPosition> Place =
        AccessTools.MethodDelegate<Action<UIBasicBubble, Vector3, UIBasicBubble.ForceCornerPosition>>(AccessTools.Method(
            typeof(UIBasicBubble), "UpdatePositionAndCorner", new[] { typeof(Vector3), typeof(UIBasicBubble.ForceCornerPosition) }));
    private static readonly List<Line> Shown = new List<Line>();
    // The bubbles showing a line of this game's conversation, another player's line or a chat line, until the bubble
    // goes or shows another line: a closing bubble lingers for two frames and its fade.
    private static readonly Dictionary<UIDialogBubble, Line> Bubbles = new Dictionary<UIDialogBubble, Line>();
    private static int lines;
    // The line this game opens a bubble for now: another player's or a chat line, or its own.
    private static Line showing;
    private static Line saying;
    // A press on this game's own line, as it was before the press.
    private static bool wasWriting;
    private static float wasShowTime;

    // Shared as the bubble opens, with the corner the game chose for it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIDialogBubble), nameof(UIDialogBubble.ShowMessage))]
    private static void Say(ref PhraseData data)
    {
        saying = null;
        if (SharedPresentation.Applying || !CoopSession.SharesWorld || !data.isPlayer && data.npcWgoData == null)
            return;
        var speaker = data.isPlayer ? Speaker.Keeper : data.npcWgoData.id == "player_wisp" ? Speaker.Wisp : Speaker.Wgo;
        int local = CoopSession.Current.LocalSlot;
        bool conversation = Conversation.Saying;
        // A conversation's keeper line is said by the keeper whose turn it is, above their head.
        int keeper = speaker == Speaker.Keeper && conversation ? DialogueTurns.Turn : local;
        var anchor = keeper == local ? null : RemoteKeeper.BubblePoint(keeper);
        if (anchor == null)
            keeper = local;
        int id = ++lines;
        var line = new Line { Slot = local, Id = id, Speaker = speaker, Keeper = keeper, Conversation = conversation, Own = true,
            Anchor = anchor, Anchored = anchor != null, Position = anchor != null ? anchor.position : default, ByKeeper = anchor != null };
        var said = data;
        SharedPresentation.Send(SharedPresentation.Cue.Talk, writer =>
        {
            writer.Write(id);
            writer.Write((byte)speaker);
            if (speaker == Speaker.Wgo)
                SharedPresentation.WriteCharacter(writer, said.npcWgoData);
            if (speaker == Speaker.Keeper)
                writer.Write((byte)keeper);
            writer.Write(conversation);
            writer.Write(said.text ?? string.Empty);
            writer.Write((byte)said.speechType);
            writer.Write((byte)said.cornerPosition);
            writer.Write(said.isOverBlackout);
            writer.Write(said.fixedShowTimeValue);
        });
        var finished = data.onFinished;
        data.onFinished = line.Closed = () =>
        {
            if (conversation)
                DialogueTurns.Active();
            SharedPresentation.Send(SharedPresentation.Cue.TalkEnd, writer => writer.Write(id));
            finished?.Invoke();
        };
        if (conversation)
            DialogueTurns.Active();
        saying = line;
    }

    internal static void Show(int slot, BinaryReader reader)
    {
        int id = reader.ReadInt32();
        var speaker = (Speaker)reader.ReadByte();
        var character = speaker == Speaker.Wgo ? SharedPresentation.ReadCharacter(reader) : null;
        int keeper = speaker == Speaker.Keeper ? reader.ReadByte() : slot;
        bool conversation = reader.ReadBoolean();
        string text = reader.ReadString();
        var type = (SpeechBubbleType)reader.ReadByte();
        var corner = (UIBasicBubble.ForceCornerPosition)reader.ReadByte();
        bool overBlackout = reader.ReadBoolean();
        float fixedTime = reader.ReadSingle();
        if (!SharedPresentation.Watches(slot))
            return;
        // The keeper whose turn it is here says it above this game's own keeper, as the game has it.
        bool mine = keeper == CoopSession.Current.LocalSlot;
        if (!mine && keeper != slot && RemoteKeeper.BubblePoint(keeper) == null)
            keeper = slot;
        // A wisp's line needs a wisp's voice and look; this game's own wisp provides them.
        var npc = speaker == Speaker.Wgo ? character
            : speaker == Speaker.Wisp ? MainGame.PlayerController.WispController?.GetWispWgoData() : null;
        if (speaker != Speaker.Keeper && npc == null)
            return;
        // A character speaking through its portrait does so above the keeper it talks to.
        bool portrait = speaker == Speaker.Wgo && npc.Definition != null && npc.Definition.usePortraitInDialogues;
        var anchor = mine ? null : speaker == Speaker.Keeper ? RemoteKeeper.BubblePoint(keeper) : portrait ? RemoteKeeper.BubblePoint(slot)
            : speaker == Speaker.Wisp ? RemoteWisps.BubblePoint(slot) : null;
        if (!mine && anchor == null && (speaker != Speaker.Wgo || portrait))
            return;
        var line = new Line { Slot = slot, Id = id, Speaker = speaker, Keeper = keeper, Conversation = conversation, Mine = mine,
            Anchor = anchor, Anchored = anchor != null, Position = anchor != null ? anchor.position : default,
            ByKeeper = speaker != Speaker.Wgo || portrait };
        line.Closed = () => Shown.Remove(line);
        Shown.Add(line);
        showing = line;
        try
        {
            Bubble.Talk(new PhraseData(speaker == Speaker.Keeper, npc, text, line.Closed, null, type, corner,
                fixedTime > 0f ? fixedTime : HeldOpen, overBlackout));
        }
        finally
        {
            showing = null;
        }
    }

    // A player's chat line, in a bubble above their keeper in every game that shows it, their own too. Their next line
    // replaces it; it never stands in for a line of their conversation.
    internal static void Chat(int slot, string text)
    {
        var session = CoopSession.Current;
        if (session == null || !KeeperSpawn.Active || MainGame.Instance.gameState != MainGame.GameState.InGame ||
            MainGame.PlayerController == null || LazyUI.Get<UILoadingOverlay>().IsShown)
            return;
        bool mine = slot == session.LocalSlot;
        var anchor = mine || !SharedPresentation.Watches(slot) ? null : RemoteKeeper.BubblePoint(slot);
        if (!mine && anchor == null)
            return;
        var line = new Line { Slot = slot, Speaker = Speaker.Keeper, Keeper = slot, Anchor = anchor, Anchored = anchor != null,
            Position = anchor != null ? anchor.position : default, ByKeeper = true, Chat = true, Mine = mine };
        line.Closed = () => Shown.Remove(line);
        Shown.Add(line);
        SharedPresentation.Mirror(() =>
        {
            showing = line;
            try
            {
                Bubble.Talk(new PhraseData(true, null, Typed(text), line.Closed, null, SpeechBubbleType.Talk,
                    UIBasicBubble.ForceCornerPosition.Auto, BubbleSettings(NativeBubble()).CalculateBubbleShowingTime(text)));
            }
            finally
            {
                showing = null;
            }
        });
    }

    // What a player typed shows as typed, and stays as long as the game keeps a line of that length; the game's own
    // [highlights] stay as they are for its speech.
    private static string Typed(string text) => text.Replace("<", "<noparse><</noparse>");

    internal static void End(int slot, BinaryReader reader)
    {
        int id = reader.ReadInt32();
        foreach (var line in Shown)
        {
            if (line.Slot == slot && line.Id == id)
                line.Ended = true;
        }
    }

    // Another player pressed on a line of this game's conversation: it moves on as this game's press would move it,
    // and theirs is the turn.
    internal static void Skip(int slot, BinaryReader reader)
    {
        int id = reader.ReadInt32();
        int owner = reader.ReadByte();
        bool ending = reader.ReadBoolean();
        if (owner != CoopSession.Current.LocalSlot || !SharedPresentation.Watches(slot) || Paused())
            return;
        foreach (var said in Bubbles)
        {
            var bubble = said.Key;
            var line = said.Value;
            if (!line.Own || line.Id != id || !Showing(bubble, line) || Disappearing(bubble))
                continue;
            SharedPresentation.Unmirrored(() =>
            {
                DialogueTurns.Progressed(slot);
                if (Letters(bubble).IsAnimating)
                {
                    Letters(bubble).Complete();
                    ShareShown(id);
                }
                else if (ending)
                    ShowTime(bubble) = -1f;
            });
            return;
        }
    }

    // The whole of the line shows in its player's game, as a press there showed it.
    internal static void Complete(int slot, BinaryReader reader)
    {
        int id = reader.ReadInt32();
        foreach (var shown in Bubbles)
        {
            var line = shown.Value;
            if (!line.Own && line.Slot == slot && line.Id == id && Showing(shown.Key, line) && Letters(shown.Key).IsAnimating)
                Letters(shown.Key).Complete();
        }
    }

    private static void ShareShown(int id) => SharedPresentation.Send(SharedPresentation.Cue.TalkShown, writer => writer.Write(id));

    internal static void Forget(int slot)
    {
        foreach (var line in Shown)
        {
            if (line.Slot == slot)
                line.Ended = true;
        }
    }

    // A bubble by this player's keeper's head, where their name tag would be, shows a line of their keeper, their wisp or
    // a portrait character until the game takes it down: the line ends as the bubble starts closing, and the bubble
    // lingers two frames and may fade before it goes.
    internal static bool Speaking(int slot)
    {
        foreach (var shown in Bubbles)
        {
            var line = shown.Value;
            if (line.Keeper == slot && line.ByKeeper && Showing(shown.Key, line) && shown.Key.gameObject.activeInHierarchy)
                return true;
        }
        return false;
    }

    private static bool Showing(UIDialogBubble bubble, Line line) => bubble != null && PhraseOf(bubble).onFinished == line.Closed;

    // A shared line speaks for the other player's keeper and is neither timed out here nor skipped but in its player's
    // game; this game's own keeper line said by another keeper speaks above theirs.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIDialogBubble), nameof(UIDialogBubble.ShowMessage))]
    private static void Opened(UIDialogBubble __result)
    {
        var line = showing ?? saying;
        saying = null;
        if (line == null || __result == null)
            return;
        var gone = new List<UIDialogBubble>();
        foreach (var bubble in Bubbles.Keys)
        {
            if (bubble == null)
                gone.Add(bubble);
        }
        foreach (var bubble in gone)
            Bubbles.Remove(bubble);
        Bubbles[__result] = line;
        if (!line.Own)
        {
            CanSkip(__result) = line.Conversation;
            ForceHide(__result) = () => Hide(line);
        }
        // This game's own keeper talks as the game has it.
        if (line.Speaker == Speaker.Keeper && line.Keeper != CoopSession.Current.LocalSlot)
            __result.animationComponent = RemoteKeeper.Talking(line.Keeper);
        else if (line.Speaker == Speaker.Wisp && !line.Own)
            __result.animationComponent = null;
        // The game opened it above this game's own keeper or wisp.
        if (line.Anchored)
            PlaceAbove(__result, line);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(UIDialogBubble), nameof(UIDialogBubble.ShowMessage))]
    private static void Done() => saying = null;

    // The game asks every frame until the bubble has gone; the line closes once.
    private static bool Hide(Line line)
    {
        if (!line.Ended || line.Hidden)
            return false;
        line.Hidden = true;
        return true;
    }

    // A press moves on a line of this game's conversation as the game has it, and the turn is this game's player's;
    // on another player's line it moves the line on in their game, and shows its whole text here at once.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UISpeechBubble), "CheckSkip")]
    private static bool Press(UISpeechBubble __instance, out bool __state)
    {
        __state = false;
        if (!(__instance is UIDialogBubble bubble) || !Bubbles.TryGetValue(bubble, out var line) || !line.Conversation ||
            !Showing(bubble, line))
            return true;
        if (line.Own)
        {
            __state = true;
            wasWriting = Letters(bubble).IsAnimating;
            wasShowTime = ShowTime(bubble);
            return true;
        }
        if (!SkipPressed(bubble))
            return false;
        LazyInput.ClearAllKeysDown();
        bool ending = !Letters(bubble).IsAnimating;
        if (!ending)
            Letters(bubble).Complete();
        int owner = line.Slot;
        int id = line.Id;
        SharedPresentation.Send(SharedPresentation.Cue.TalkSkip, writer =>
        {
            writer.Write(id);
            writer.Write((byte)owner);
            writer.Write(ending);
        });
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UISpeechBubble), "CheckSkip")]
    private static void Pressed(UISpeechBubble __instance, bool __state)
    {
        if (!__state)
            return;
        bool shown = wasWriting && !Letters(__instance).IsAnimating;
        if (!shown && ShowTime(__instance) == wasShowTime)
            return;
        DialogueTurns.Progressed(CoopSession.Current.LocalSlot);
        if (shown)
            ShareShown(Bubbles[(UIDialogBubble)__instance].Id);
    }

    // The game's own press on a line: a click, the story's own condition or a key that skips lines.
    private static bool SkipPressed(UISpeechBubble bubble)
    {
        var condition = SkipCondition();
        if (Input.GetMouseButtonDown(0) || condition != null && condition())
            return true;
        foreach (var key in SkipKeys(bubble))
        {
            if (LazyInput.GetKeyDown(key))
                return true;
        }
        return false;
    }

    // A shared line opens a bubble of its own. The game would reuse the open bubble of the same speaker
    // and end its line, and here that speaker is this game's own keeper, wisp or character. A player's chat
    // lines share a bubble of their own, apart from their conversation's, and each keeper's lines their own.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UISpeechBubble), nameof(UISpeechBubble.ShowMessage), typeof(int), typeof(string), typeof(Vector2),
        typeof(SpeechBubblePreset), typeof(VoiceID), typeof(Action), typeof(Action), typeof(Func<Vector2>), typeof(Func<bool>),
        typeof(UIBasicBubble.ForceCornerPosition), typeof(float), typeof(bool), typeof(Vector3))]
    private static void OwnBubble(ref int speakerId)
    {
        var line = showing ?? (saying != null && saying.Anchored ? saying : null);
        if (line != null)
            speakerId = HashCode.Combine(line.Slot, line.Keeper, line.Speaker, line.Chat, speakerId);
    }

    // Lines above another player's keeper or wisp follow theirs through camera moves, where the game
    // keeps its own above this game's keeper or wisp. PhraseData.GetTargetPos, a struct's method
    // returning a struct, breaks under Mono once patched.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIDialogBubble), "UpdatePos")]
    private static bool Follow(UIDialogBubble __instance)
    {
        if (!Bubbles.TryGetValue(__instance, out var line) || !line.Anchored || !Showing(__instance, line))
            return true;
        if (__instance.gameObject.activeSelf)
            PlaceAbove(__instance, line);
        return false;
    }

    private static void PlaceAbove(UIDialogBubble bubble, Line line)
    {
        if (line.Anchor != null)
            line.Position = line.Anchor.position;
        Place(bubble, CameraSystem.WorldToScreenPoint(line.Position), PhraseOf(bubble).cornerPosition);
    }

    // Showing another player's line says nothing in this game's own story.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GlobalEventsSystem), nameof(GlobalEventsSystem.FireTrigger))]
    private static bool Quiet(GlobalEventsSystem.Event.Type type) =>
        !SharedPresentation.Applying || type != GlobalEventsSystem.Event.Type.SpeechSay;

    private sealed class Line
    {
        // The player whose game says it.
        internal int Slot;
        internal int Id;
        internal Speaker Speaker;
        // The keeper by whose head it shows: the one who says a keeper's line, or the player whose wisp or portrait
        // character says it.
        internal int Keeper;
        // A line of a conversation or cutscene, which every player watching it can move on.
        internal bool Conversation;
        // This game's own line, shared with the others.
        internal bool Own;
        // Said by this game's own keeper: a turn of another player's conversation, or a chat line.
        internal bool Mine;
        internal Transform Anchor;
        // Placed above the anchor here, where the game would place it above this game's own.
        internal bool Anchored;
        // Shown by a keeper's head: a keeper's line, or a wisp's or a portrait character's.
        internal bool ByKeeper;
        // Said in the chat, above the keeper of this game's player or another's.
        internal bool Chat;
        internal Vector3 Position;
        internal Action Closed;
        internal bool Ended;
        internal bool Hidden;
    }
}
