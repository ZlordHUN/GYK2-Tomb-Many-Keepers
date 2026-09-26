using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// A player's dialogue choices appear above their keeper for the players in the same scene, in the
// native answer bubble. The option they point at is highlighted and their choice plays out as it
// does for them; only the choosing player can answer.
[HarmonyPatch]
internal static class SharedAnswers
{
    private static readonly AccessTools.FieldRef<List<UIMultiAnswer>> Menus =
        AccessTools.StaticFieldRefAccess<List<UIMultiAnswer>>(AccessTools.Field(typeof(UIMultiAnswer), "multiAnswers"));
    private static readonly AccessTools.FieldRef<UIMultiAnswer, List<UIMultiAnswerOption>> Options =
        AccessTools.FieldRefAccess<UIMultiAnswer, List<UIMultiAnswerOption>>("answerOptions");
    private static readonly AccessTools.FieldRef<UIMultiAnswer, CanvasGroup> Canvas =
        AccessTools.FieldRefAccess<UIMultiAnswer, CanvasGroup>("canvas");
    private static readonly AccessTools.FieldRef<UIMultiAnswer, bool> Interactable =
        AccessTools.FieldRefAccess<UIMultiAnswer, bool>("interactable");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, UIMultiAnswer> MenuOf =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, UIMultiAnswer>("multiAnswer");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, AnswerVisualData> AnswerOf =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, AnswerVisualData>("visualData");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, bool> Over =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, bool>("isOver");
    private static readonly Action<UIMultiAnswer> Disappear =
        AccessTools.MethodDelegate<Action<UIMultiAnswer>>(AccessTools.Method(typeof(UIMultiAnswer), "StartDisappearAnimation"));
    // This game's own menus shared with the others, and the other players' menus shown here.
    private static readonly Dictionary<UIMultiAnswer, int> Own = new Dictionary<UIMultiAnswer, int>();
    private static readonly Dictionary<(int slot, int id), UIMultiAnswer> Mirrors = new Dictionary<(int, int), UIMultiAnswer>();
    private static int menus;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswer), nameof(UIMultiAnswer.ShowAnswers), typeof(List<AnswerVisualData>), typeof(Transform), typeof(WgoData),
        typeof(Action<string>), typeof(Action), typeof(UIBasicBubble.ForceCornerPosition), typeof(bool), typeof(bool))]
    private static void Shown(Transform targetTransform, WgoData dialogParticipant, UIBasicBubble.ForceCornerPosition forceCornerPosition, bool isOverBlackout)
    {
        var list = Menus();
        if (SharedPresentation.Applying || !CoopSession.SharesWorld || list.Count == 0 || targetTransform != MainGame.PlayerController.BubblePoint)
            return;
        var menu = list[list.Count - 1];
        int id = ++menus;
        Own[menu] = id;
        var answers = new List<AnswerVisualData>();
        foreach (var option in Options(menu))
            answers.Add(AnswerOf(option));
        SharedPresentation.Send(SharedPresentation.Cue.Answers, writer =>
        {
            writer.Write(id);
            writer.Write((byte)answers.Count);
            foreach (var answer in answers)
            {
                writer.Write(answer.id);
                writer.Write(answer.hiddenByDefault);
            }
            SharedPresentation.WriteCharacter(writer, dialogParticipant);
            writer.Write((byte)forceCornerPosition);
            writer.Write(isOverBlackout);
        });
        // A gamepad points at an answer as the menu opens.
        foreach (var option in Options(menu))
        {
            if (Over(option))
                ShareHover(option, true);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), nameof(UIMultiAnswerOption.OnItemOver))]
    private static void Pointed(UIMultiAnswerOption __instance) => ShareHover(__instance, true);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), nameof(UIMultiAnswerOption.OnItemOut))]
    private static void Left(UIMultiAnswerOption __instance) => ShareHover(__instance, false);

    private static void ShareHover(UIMultiAnswerOption option, bool over)
    {
        var menu = MenuOf(option);
        if (menu == null || !Own.TryGetValue(menu, out int id))
            return;
        string answer = AnswerOf(option).id;
        SharedPresentation.Send(SharedPresentation.Cue.AnswerHover, writer =>
        {
            writer.Write(id);
            writer.Write(answer);
            writer.Write(over);
        });
    }

    // Only the choosing player answers; another player's menu here only plays out their choice.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswer), nameof(UIMultiAnswer.OnAnswerSelect))]
    private static bool Chosen(UIMultiAnswer __instance, string answerId)
    {
        if (Mirrors.ContainsValue(__instance))
            return SharedPresentation.Applying;
        if (Interactable(__instance) && Own.TryGetValue(__instance, out int id))
        {
            SharedPresentation.Send(SharedPresentation.Cue.AnswerChosen, writer =>
            {
                writer.Write(id);
                writer.Write(answerId);
            });
        }
        return true;
    }

    // Another player's menu here takes no gamepad input.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswer), "Update")]
    private static bool Navigate(UIMultiAnswer __instance) => !Mirrors.ContainsValue(__instance);

    // Another player's menu shows the answers exactly as theirs does.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswer), "FormVisibleAnswers")]
    private static bool Visible(List<AnswerVisualData> answers, ref List<AnswerVisualData> __result)
    {
        if (!SharedPresentation.Applying)
            return true;
        __result = answers;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswer), "DisableBubble")]
    private static void Closed(UIMultiAnswer __instance)
    {
        if (Own.TryGetValue(__instance, out int id))
        {
            Own.Remove(__instance);
            SharedPresentation.Send(SharedPresentation.Cue.AnswersClosed, writer => writer.Write(id));
        }
        foreach (var mirror in Mirrors)
        {
            if (mirror.Value != __instance)
                continue;
            Mirrors.Remove(mirror.Key);
            break;
        }
    }

    internal static void Apply(int slot, SharedPresentation.Cue cue, BinaryReader reader)
    {
        int id = reader.ReadInt32();
        switch (cue)
        {
            case SharedPresentation.Cue.Answers:
                Show(slot, id, reader);
                break;
            case SharedPresentation.Cue.AnswerHover:
                string pointed = reader.ReadString();
                bool over = reader.ReadBoolean();
                var hovered = Option(slot, id, pointed);
                if (hovered == null)
                    return;
                if (over)
                    hovered.OnItemOver();
                else
                    hovered.OnItemOut();
                break;
            case SharedPresentation.Cue.AnswerChosen:
                string chosen = reader.ReadString();
                var option = Option(slot, id, chosen);
                if (option == null)
                    return;
                option.OnItemOver();
                Mirrors[(slot, id)].OnAnswerSelect(chosen);
                break;
            case SharedPresentation.Cue.AnswersClosed:
                Close(slot, id);
                break;
        }
    }

    private static void Show(int slot, int id, BinaryReader reader)
    {
        int count = reader.ReadByte();
        var answers = new List<AnswerVisualData>();
        for (int i = 0; i < count; i++)
        {
            string answer = reader.ReadString();
            bool hidden = reader.ReadBoolean();
            // A story answer shows the requirements its quest asks for.
            AnswerData data = GameBalance.Me.questDefByReqPhrase.TryGetValue(answer, out var quest) ? quest.finishCheck.GetAnswerDataByReqs() : null;
            answers.Add(new AnswerVisualData { id = answer, hiddenByDefault = hidden, answerData = data });
        }
        var talker = SharedPresentation.ReadCharacter(reader);
        var corner = (UIBasicBubble.ForceCornerPosition)reader.ReadByte();
        bool overBlackout = reader.ReadBoolean();
        var anchor = RemoteKeeper.BubblePoint(slot);
        if (!SharedPresentation.Watches(slot) || anchor == null || answers.Count == 0)
            return;
        UIMultiAnswer.ShowAnswers(answers, anchor, talker, _ => { }, null, corner, isMainMultianswer: false, overBlackout);
        var list = Menus();
        var menu = list[list.Count - 1];
        Mirrors[(slot, id)] = menu;
        // The pointer here neither highlights nor chooses another player's answers.
        Canvas(menu).blocksRaycasts = false;
    }

    private static UIMultiAnswerOption Option(int slot, int id, string answer)
    {
        if (!Mirrors.TryGetValue((slot, id), out var menu) || menu == null)
            return null;
        foreach (var option in Options(menu))
        {
            if (AnswerOf(option).id == answer)
                return option;
        }
        return null;
    }

    private static void Close(int slot, int id)
    {
        if (!Mirrors.TryGetValue((slot, id), out var menu))
            return;
        Mirrors.Remove((slot, id));
        if (menu != null && Menus().Contains(menu) && Interactable(menu))
            Disappear(menu);
    }

    internal static void Forget(int slot)
    {
        var closing = new List<int>();
        foreach (var mirror in Mirrors.Keys)
        {
            if (mirror.slot == slot)
                closing.Add(mirror.id);
        }
        foreach (int id in closing)
            Close(slot, id);
    }
}
