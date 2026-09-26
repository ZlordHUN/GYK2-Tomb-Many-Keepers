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
// line stays until the speaking player's game closes it.
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
    private static readonly Action<UIBasicBubble, Vector3, UIBasicBubble.ForceCornerPosition> Place =
        AccessTools.MethodDelegate<Action<UIBasicBubble, Vector3, UIBasicBubble.ForceCornerPosition>>(AccessTools.Method(
            typeof(UIBasicBubble), "UpdatePositionAndCorner", new[] { typeof(Vector3), typeof(UIBasicBubble.ForceCornerPosition) }));
    private static readonly List<Line> Shown = new List<Line>();
    // Bubbles showing another player's keeper or wisp line, placed above theirs until the bubble goes
    // or shows another line: a closing bubble lingers for two frames and its fade.
    private static readonly Dictionary<UIDialogBubble, Line> Placed = new Dictionary<UIDialogBubble, Line>();
    private static int lines;
    private static Line showing;

    // Shared as the bubble opens, with the corner the game chose for it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIDialogBubble), nameof(UIDialogBubble.ShowMessage))]
    private static void Say(ref PhraseData data)
    {
        if (SharedPresentation.Applying || !CoopSession.SharesWorld || !data.isPlayer && data.npcWgoData == null)
            return;
        var speaker = data.isPlayer ? Speaker.Keeper : data.npcWgoData.id == "player_wisp" ? Speaker.Wisp : Speaker.Wgo;
        int line = ++lines;
        var said = data;
        SharedPresentation.Send(SharedPresentation.Cue.Talk, writer =>
        {
            writer.Write(line);
            writer.Write((byte)speaker);
            if (speaker == Speaker.Wgo)
                SharedPresentation.WriteCharacter(writer, said.npcWgoData);
            writer.Write(said.text ?? string.Empty);
            writer.Write((byte)said.speechType);
            writer.Write((byte)said.cornerPosition);
            writer.Write(said.isOverBlackout);
            writer.Write(said.fixedShowTimeValue);
        });
        var finished = data.onFinished;
        data.onFinished = () =>
        {
            SharedPresentation.Send(SharedPresentation.Cue.TalkEnd, writer => writer.Write(line));
            finished?.Invoke();
        };
    }

    internal static void Show(int slot, BinaryReader reader)
    {
        int id = reader.ReadInt32();
        var speaker = (Speaker)reader.ReadByte();
        var character = speaker == Speaker.Wgo ? SharedPresentation.ReadCharacter(reader) : null;
        string text = reader.ReadString();
        var type = (SpeechBubbleType)reader.ReadByte();
        var corner = (UIBasicBubble.ForceCornerPosition)reader.ReadByte();
        bool overBlackout = reader.ReadBoolean();
        float fixedTime = reader.ReadSingle();
        if (!SharedPresentation.Watches(slot))
            return;
        // A wisp's line needs a wisp's voice and look; this game's own wisp provides them.
        var npc = speaker == Speaker.Wgo ? character
            : speaker == Speaker.Wisp ? MainGame.PlayerController.WispController?.GetWispWgoData() : null;
        if (speaker != Speaker.Keeper && npc == null)
            return;
        // A character speaking through its portrait does so above the keeper it talks to.
        bool portrait = speaker == Speaker.Wgo && npc.Definition != null && npc.Definition.usePortraitInDialogues;
        var anchor = speaker == Speaker.Keeper || portrait ? RemoteKeeper.BubblePoint(slot)
            : speaker == Speaker.Wisp ? RemoteWisps.BubblePoint(slot) : null;
        if (anchor == null && (speaker != Speaker.Wgo || portrait))
            return;
        var line = new Line { Slot = slot, Id = id, Speaker = speaker, Anchor = anchor, Anchored = anchor != null,
            Position = anchor != null ? anchor.position : default };
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

    internal static void End(int slot, BinaryReader reader)
    {
        int id = reader.ReadInt32();
        foreach (var line in Shown)
        {
            if (line.Slot == slot && line.Id == id)
                line.Ended = true;
        }
    }

    internal static void Forget(int slot)
    {
        foreach (var line in Shown)
        {
            if (line.Slot == slot)
                line.Ended = true;
        }
    }

    // A shared line speaks for the other player's keeper and is neither skipped nor timed out here.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIDialogBubble), nameof(UIDialogBubble.ShowMessage))]
    private static void Mirrored(UIDialogBubble __result)
    {
        var line = showing;
        if (line == null || __result == null)
            return;
        CanSkip(__result) = false;
        ForceHide(__result) = () => Hide(line);
        if (line.Speaker == Speaker.Keeper)
            __result.animationComponent = RemoteKeeper.Talking(line.Slot);
        else if (line.Speaker == Speaker.Wisp)
            __result.animationComponent = null;
        if (!line.Anchored)
            return;
        var gone = new List<UIDialogBubble>();
        foreach (var bubble in Placed.Keys)
        {
            if (bubble == null)
                gone.Add(bubble);
        }
        foreach (var bubble in gone)
            Placed.Remove(bubble);
        Placed[__result] = line;
        // The game opened it above this game's own keeper or wisp.
        PlaceAbove(__result, line);
    }

    // The game asks every frame until the bubble has gone; the line closes once.
    private static bool Hide(Line line)
    {
        if (!line.Ended || line.Hidden)
            return false;
        line.Hidden = true;
        return true;
    }

    // A shared line opens a bubble of its own. The game would reuse the open bubble of the same speaker
    // and end its line, and here that speaker is this game's own keeper, wisp or character.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UISpeechBubble), nameof(UISpeechBubble.ShowMessage), typeof(int), typeof(string), typeof(Vector2),
        typeof(SpeechBubblePreset), typeof(VoiceID), typeof(Action), typeof(Action), typeof(Func<Vector2>), typeof(Func<bool>),
        typeof(UIBasicBubble.ForceCornerPosition), typeof(float), typeof(bool), typeof(Vector3))]
    private static void OwnBubble(ref int speakerId)
    {
        var line = showing;
        if (line != null)
            speakerId = HashCode.Combine(line.Slot, line.Speaker, speakerId);
    }

    // Lines of another player's keeper or wisp follow that player's through camera moves, where the game
    // keeps its own above this game's keeper or wisp. PhraseData.GetTargetPos, a struct's method
    // returning a struct, breaks under Mono once patched.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIDialogBubble), "UpdatePos")]
    private static bool Follow(UIDialogBubble __instance)
    {
        if (!Placed.TryGetValue(__instance, out var line) || PhraseOf(__instance).onFinished != line.Closed)
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
        internal int Slot;
        internal int Id;
        internal Speaker Speaker;
        internal Transform Anchor;
        // Placed above the anchor here, where the game would place it above this game's own.
        internal bool Anchored;
        internal Vector3 Position;
        internal Action Closed;
        internal bool Ended;
        internal bool Hidden;
    }
}
