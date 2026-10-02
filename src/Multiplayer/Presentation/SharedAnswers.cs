using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// A player's dialogue choices appear for the players in the same scene, in the native answer bubble. As in
// GYK1, the player whose turn it is in the conversation answers them, above their own keeper: what they point
// at is highlighted for everyone, and their choice plays out in the conversation's game as the game's own press
// would, as it does for everyone. A building site's or an arena's choices stay with their own player.
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
    private static readonly AccessTools.FieldRef<UIMultiAnswer, Transform> Target =
        AccessTools.FieldRefAccess<UIMultiAnswer, Transform>("targetTransform");
    private static readonly AccessTools.FieldRef<UIMultiAnswer, GamepadNavigationController> Gamepad =
        AccessTools.FieldRefAccess<UIMultiAnswer, GamepadNavigationController>("gamepadController");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, UIMultiAnswer> MenuOf =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, UIMultiAnswer>("multiAnswer");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, AnswerVisualData> AnswerOf =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, AnswerVisualData>("visualData");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, bool> Over =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, bool>("isOver");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, bool> Available =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, bool>("available");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, bool> Ready =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, bool>("interactableByAnimation");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, Image> LockIcon =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, Image>("lockIconImage");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, TextStyleComponent> Style =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, TextStyleComponent>("textStyleComponent");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, TextStyle> Grey =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, TextStyle>("greyTextStyle");
    private static readonly Action<UIMultiAnswer> Disappear =
        AccessTools.MethodDelegate<Action<UIMultiAnswer>>(AccessTools.Method(typeof(UIMultiAnswer), "StartDisappearAnimation"));
    private static readonly Action<UIMultiAnswer> Reposition =
        AccessTools.MethodDelegate<Action<UIMultiAnswer>>(AccessTools.Method(typeof(UIMultiAnswer), "ApplyBubblePosition"));
    private static readonly Action<UIMultiAnswer> Focus =
        AccessTools.MethodDelegate<Action<UIMultiAnswer>>(AccessTools.Method(typeof(UIMultiAnswer), "HandleGamepadState"));
    // This game's own menus shared with the others, and the other players' menus shown here.
    private static readonly Dictionary<UIMultiAnswer, Menu> Shared = new Dictionary<UIMultiAnswer, Menu>();
    private static int menus;
    // The menu this game opens now: who answers it, and for another player's, which of its answers can be chosen there.
    private static int opening;
    private static Dictionary<string, bool> choosable;
    // This game applies the answer its turn's player chose.
    private static bool picking;

    private static int Local => CoopSession.Current?.LocalSlot ?? 0;

    // The player whose turn it is answers this game's own dialogue choices, above their keeper.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswer), nameof(UIMultiAnswer.ShowAnswers), typeof(List<AnswerVisualData>), typeof(Transform), typeof(WgoData),
        typeof(Action<string>), typeof(Action), typeof(UIBasicBubble.ForceCornerPosition), typeof(bool), typeof(bool))]
    private static void Showing(ref Transform targetTransform)
    {
        if (SharedPresentation.Applying)
            return;
        opening = 0;
        if (!CoopSession.SharesWorld || MainGame.PlayerController == null || targetTransform != MainGame.PlayerController.BubblePoint)
            return;
        opening = Local;
        if (!Conversation.Choosing)
            return;
        int turn = DialogueTurns.Turn;
        var point = turn == opening ? null : RemoteKeeper.BubblePoint(turn);
        if (point == null)
            return;
        opening = turn;
        targetTransform = point;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswer), nameof(UIMultiAnswer.ShowAnswers), typeof(List<AnswerVisualData>), typeof(Transform), typeof(WgoData),
        typeof(Action<string>), typeof(Action), typeof(UIBasicBubble.ForceCornerPosition), typeof(bool), typeof(bool))]
    private static void Shown(WgoData dialogParticipant, UIBasicBubble.ForceCornerPosition forceCornerPosition, bool isOverBlackout)
    {
        if (SharedPresentation.Applying)
            return;
        int answerer = opening;
        opening = 0;
        var list = Menus();
        // A menu the game found no answers to show never opens.
        if (answerer == 0 || list.Count == 0 || !list[list.Count - 1].gameObject.activeSelf)
            return;
        var view = list[list.Count - 1];
        var menu = new Menu { Owner = Local, Id = ++menus, Answerer = answerer, Talker = dialogParticipant, Corner = forceCornerPosition,
            OverBlackout = isOverBlackout, Conversation = Conversation.Choosing };
        Shared[view] = menu;
        if (menu.Conversation)
            DialogueTurns.Active();
        Share(view, menu);
        if (answerer != menu.Owner)
        {
            // Only the player whose turn it is points and chooses.
            Canvas(view).blocksRaycasts = false;
            return;
        }
        // A gamepad points at an answer as the menu opens.
        foreach (var option in Options(view))
        {
            if (Over(option))
                ShareHover(option, true);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(UIMultiAnswer), nameof(UIMultiAnswer.ShowAnswers), typeof(List<AnswerVisualData>), typeof(Transform), typeof(WgoData),
        typeof(Action<string>), typeof(Action), typeof(UIBasicBubble.ForceCornerPosition), typeof(bool), typeof(bool))]
    private static void Opened()
    {
        if (!SharedPresentation.Applying)
            opening = 0;
    }

    private static void Share(UIMultiAnswer view, Menu menu)
    {
        var answers = new List<(string id, bool hidden, bool available)>();
        foreach (var option in Options(view))
            answers.Add((AnswerOf(option).id, AnswerOf(option).hiddenByDefault, Available(option)));
        SharedPresentation.Send(SharedPresentation.Cue.Answers, writer =>
        {
            writer.Write(menu.Id);
            writer.Write((byte)answers.Count);
            foreach (var answer in answers)
            {
                writer.Write(answer.id);
                writer.Write(answer.hidden);
                writer.Write(answer.available);
            }
            SharedPresentation.WriteCharacter(writer, menu.Talker);
            writer.Write((byte)menu.Corner);
            writer.Write(menu.OverBlackout);
            writer.Write((byte)menu.Answerer);
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), nameof(UIMultiAnswerOption.OnItemOver))]
    private static void Pointed(UIMultiAnswerOption __instance) => ShareHover(__instance, true);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), nameof(UIMultiAnswerOption.OnItemOut))]
    private static void Left(UIMultiAnswerOption __instance) => ShareHover(__instance, false);

    // What the player who answers a menu points at, for everyone who sees it.
    private static void ShareHover(UIMultiAnswerOption option, bool over)
    {
        var view = MenuOf(option);
        if (view == null || !Shared.TryGetValue(view, out var menu) || menu.Answerer != Local)
            return;
        string answer = AnswerOf(option).id;
        if (menu.Owner == Local)
        {
            SharedPresentation.Send(SharedPresentation.Cue.AnswerHover, writer =>
            {
                writer.Write(menu.Id);
                writer.Write(answer);
                writer.Write(over);
            });
            return;
        }
        SharedPresentation.Send(SharedPresentation.Cue.AnswerPoint, writer =>
        {
            writer.Write(menu.Id);
            writer.Write((byte)menu.Owner);
            writer.Write(answer);
            writer.Write(over);
        });
    }

    // The player whose turn it is chooses from their own menu of another player's answers; the choice plays out in
    // that player's game, where the game takes what it costs and gives what it rewards, and then in everyone's.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), nameof(UIMultiAnswerOption.OnItemPress))]
    private static bool Press(UIMultiAnswerOption __instance)
    {
        var view = MenuOf(__instance);
        if (view == null || !Shared.TryGetValue(view, out var menu))
            return true;
        if (menu.Owner == Local)
            return menu.Answerer == Local || picking;
        if (menu.Answerer != Local || SharedPresentation.Applying || !Interactable(view) || !Ready(__instance) || !Available(__instance))
            return false;
        string answer = AnswerOf(__instance).id;
        SharedPresentation.Send(SharedPresentation.Cue.AnswerPick, writer =>
        {
            writer.Write(menu.Id);
            writer.Write((byte)menu.Owner);
            writer.Write(answer);
        });
        return false;
    }

    // Only the player whose turn it is answers; another player's menu here only plays out their choice.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswer), nameof(UIMultiAnswer.OnAnswerSelect))]
    private static bool Chosen(UIMultiAnswer __instance, string answerId)
    {
        if (!Shared.TryGetValue(__instance, out var menu))
            return true;
        if (menu.Owner != Local)
            return SharedPresentation.Applying;
        if (menu.Answerer != Local && !picking)
            return false;
        if (Interactable(__instance))
        {
            SharedPresentation.Send(SharedPresentation.Cue.AnswerChosen, writer =>
            {
                writer.Write(menu.Id);
                writer.Write(answerId);
            });
        }
        return true;
    }

    // A menu here takes gamepad input from its answering player alone. This game's own menu hands the answer back
    // to its player if the one whose turn it was has gone.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswer), "Update")]
    private static bool Navigate(UIMultiAnswer __instance)
    {
        if (!Shared.TryGetValue(__instance, out var menu))
            return true;
        if (menu.Owner == Local && menu.Answerer != Local && !Present(menu.Answerer))
            HandBack(__instance, menu);
        return menu.Answerer == Local;
    }

    // Nor does a gamepad point at an answer in a menu another player answers.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswer), "HandleGamepadState")]
    private static bool Pointing(UIMultiAnswer __instance)
    {
        int answerer = Shared.TryGetValue(__instance, out var menu) ? menu.Answerer : opening != 0 ? opening : Local;
        if (answerer == Local || Local == 0)
            return true;
        Gamepad(__instance)?.Disable();
        return false;
    }

    private static bool Present(int slot) => SharedPresentation.Watches(slot) && RemoteKeeper.BubblePoint(slot) != null;

    private static void HandBack(UIMultiAnswer view, Menu menu)
    {
        int gone = menu.Id;
        SharedPresentation.Send(SharedPresentation.Cue.AnswersClosed, writer => writer.Write(gone));
        menu.Id = ++menus;
        menu.Answerer = Local;
        Target(view) = MainGame.PlayerController.BubblePoint;
        Reposition(view);
        Canvas(view).blocksRaycasts = true;
        Focus(view);
        Share(view, menu);
    }

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

    // And each answer can be chosen as in their game, which takes what it costs from their keeper.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), nameof(UIMultiAnswerOption.ShowIcons))]
    private static void Styling(UIMultiAnswerOption __instance, out TextStyle __state) =>
        __state = choosable != null ? Style(__instance).CurrentTextStyle : null;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), nameof(UIMultiAnswerOption.ShowIcons))]
    private static void Styled(UIMultiAnswerOption __instance, TextStyle __state)
    {
        if (choosable == null || !choosable.TryGetValue(AnswerOf(__instance).id, out bool available) || Available(__instance) == available)
            return;
        Available(__instance) = available;
        LockIcon(__instance).sprite = LazySingletonSO<EasySpritesCollection>.Instance.GetSprite(available ? "ui_reply_lock_grn" : "ui_reply_lock_red");
        Style(__instance).SetTextStyle(available ? __state : Grey(__instance));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswer), "DisableBubble")]
    private static void Closed(UIMultiAnswer __instance)
    {
        if (!Shared.TryGetValue(__instance, out var menu))
            return;
        Shared.Remove(__instance);
        if (menu.Owner != Local)
            return;
        if (menu.Conversation)
            DialogueTurns.Active();
        SharedPresentation.Send(SharedPresentation.Cue.AnswersClosed, writer => writer.Write(menu.Id));
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
                Hover(Option(Mirror(slot, id), reader.ReadString()), reader.ReadBoolean());
                break;
            case SharedPresentation.Cue.AnswerPoint:
                int pointedIn = reader.ReadByte();
                var pointedMenu = pointedIn == Local ? Own(id) : Mirror(pointedIn, id);
                string pointed = reader.ReadString();
                bool over = reader.ReadBoolean();
                if (pointedMenu != null && Shared[pointedMenu].Answerer == slot)
                    Hover(Option(pointedMenu, pointed), over);
                break;
            case SharedPresentation.Cue.AnswerPick:
                Pick(slot, id, reader.ReadByte(), reader.ReadString());
                break;
            case SharedPresentation.Cue.AnswerChosen:
                string chosen = reader.ReadString();
                var mirror = Mirror(slot, id);
                var option = Option(mirror, chosen);
                if (option == null)
                    return;
                Shared[mirror].Leaving = true;
                option.OnItemOver();
                mirror.OnAnswerSelect(chosen);
                break;
            case SharedPresentation.Cue.AnswersClosed:
                Close(Mirror(slot, id));
                break;
        }
    }

    private static void Hover(UIMultiAnswerOption option, bool over)
    {
        if (option == null)
            return;
        if (over)
            option.OnItemOver();
        else
            option.OnItemOut();
    }

    // The answer chosen by the player whose turn it was, pressed in this game as its own player would press it.
    private static void Pick(int slot, int id, int owner, string answer)
    {
        var view = owner == Local ? Own(id) : null;
        if (view == null || Shared[view].Answerer != slot || !SharedPresentation.Watches(slot))
            return;
        var option = Option(view, answer);
        if (option == null)
            return;
        SharedPresentation.Unmirrored(() =>
        {
            DialogueTurns.Progressed(slot);
            picking = true;
            try
            {
                option.OnItemOver();
                option.OnItemPress();
            }
            finally
            {
                picking = false;
            }
        });
    }

    private static void Show(int slot, int id, BinaryReader reader)
    {
        int count = reader.ReadByte();
        var answers = new List<AnswerVisualData>();
        var available = new Dictionary<string, bool>();
        for (int i = 0; i < count; i++)
        {
            string answer = reader.ReadString();
            bool hidden = reader.ReadBoolean();
            available[answer] = reader.ReadBoolean();
            // A story answer shows the requirements its quest asks for.
            AnswerData data = GameBalance.Me.questDefByReqPhrase.TryGetValue(answer, out var quest) ? quest.finishCheck.GetAnswerDataByReqs() : null;
            answers.Add(new AnswerVisualData { id = answer, hiddenByDefault = hidden, answerData = data });
        }
        var talker = SharedPresentation.ReadCharacter(reader);
        var corner = (UIBasicBubble.ForceCornerPosition)reader.ReadByte();
        bool overBlackout = reader.ReadBoolean();
        int answerer = reader.ReadByte();
        if (!SharedPresentation.Watches(slot) || answers.Count == 0)
            return;
        // The player whose turn it is answers above their own keeper, as the game has it.
        bool mine = answerer == Local;
        var anchor = mine ? MainGame.PlayerController.BubblePoint : RemoteKeeper.BubblePoint(answerer);
        if (anchor == null)
            anchor = RemoteKeeper.BubblePoint(slot);
        if (anchor == null)
            return;
        opening = answerer;
        choosable = available;
        try
        {
            UIMultiAnswer.ShowAnswers(answers, anchor, talker, _ => { }, null, corner, isMainMultianswer: false, overBlackout);
        }
        finally
        {
            opening = 0;
            choosable = null;
        }
        var list = Menus();
        var view = list[list.Count - 1];
        Shared[view] = new Menu { Owner = slot, Id = id, Answerer = answerer, Mirrored = true };
        // The pointer here neither highlights nor chooses another player's answers.
        if (!mine)
            Canvas(view).blocksRaycasts = false;
    }

    // A menu of answers stands above this player's keeper, where their name tag would be, until its fade has ended;
    // the game keeps each menu in its list until then.
    internal static bool Answering(int slot)
    {
        var point = RemoteKeeper.BubblePoint(slot);
        if (point == null)
            return false;
        foreach (var menu in Menus())
        {
            if (menu != null && Target(menu) == point)
                return true;
        }
        return false;
    }

    private static UIMultiAnswer Own(int id) => Find(Local, id);

    private static UIMultiAnswer Mirror(int slot, int id) => slot == Local ? null : Find(slot, id);

    private static UIMultiAnswer Find(int owner, int id)
    {
        foreach (var shared in Shared)
        {
            if (shared.Key != null && shared.Value.Owner == owner && shared.Value.Id == id)
                return shared.Key;
        }
        return null;
    }

    private static UIMultiAnswerOption Option(UIMultiAnswer view, string answer)
    {
        if (view == null)
            return null;
        foreach (var option in Options(view))
        {
            if (AnswerOf(option).id == answer)
                return option;
        }
        return null;
    }

    // A menu closes with its player's, as it opens or after.
    private static void Close(UIMultiAnswer view)
    {
        if (view == null || !Shared.TryGetValue(view, out var menu))
            return;
        Shared.Remove(view);
        if (!menu.Leaving && Menus().Contains(view))
            Disappear(view);
    }

    // A player who leaves takes their menus shown here with them. This game's own menus stay with its story, also when the
    // session ends and no slot is this game's any more; the game takes them down as it leaves for the main menu.
    internal static void Forget(int slot)
    {
        var closing = new List<UIMultiAnswer>();
        foreach (var shared in Shared)
        {
            if (shared.Value.Mirrored && shared.Value.Owner == slot)
                closing.Add(shared.Key);
        }
        foreach (var view in closing)
            Close(view);
    }

    private sealed class Menu
    {
        // The player whose game shows the menu for its story, and its id there.
        internal int Owner;
        internal int Id;
        // The player who answers it: the one whose turn it is in a conversation.
        internal int Answerer;
        internal WgoData Talker;
        internal UIBasicBubble.ForceCornerPosition Corner;
        internal bool OverBlackout;
        internal bool Conversation;
        // Another player's menu, shown here.
        internal bool Mirrored;
        // Another player's menu playing out their choice.
        internal bool Leaving;
    }
}
