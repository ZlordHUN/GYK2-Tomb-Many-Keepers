using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Progression;

// Each character receives the personal rewards of the campaign's quests and technologies once:
// items, personal values, talents, buffs, capacity, looks and achievements. The player whose action
// earned them receives them as it plays out; every other character receives their own in their own
// game, now or whenever they next play the campaign. The campaign records each reward it has handed
// out and each character the ones it has received, so none is given twice, even once spent or lost.
[HarmonyPatch]
internal static class PersonalGrants
{
    private const string QuestMark = "tmk_grant:quest:";
    private const string TechMark = "tmk_grant:tech:";
    private const float DeliveryInterval = 1f;
    // Rewards each keeper's game applies for itself.
    private static readonly string[] Personal =
    {
        "DropItem(", "AddPPar(", "SetPPar(", "AddInspiration(", "AddTalentValue(", "AddPerk(",
        "IncreasePlayerInventory(", "ReducePlayerInventory(", "IncreaseOverheadStackLimit(",
        "UnlockCustomizationPart(", "UnlockCustomizationColor(", "SuperUnlockBodyCustomization(", "AchUnlock(",
        "UnlockHudDaySprite("
    };
    private static readonly Regex DroppedItem = new Regex("^DropItem\\(\\s*\"([^\"]+)\"\\s*,\\s*(\\d+)\\s*\\)$");
    private static readonly AccessTools.FieldRef<LazyExpressionBase, string> Source =
        AccessTools.FieldRefAccess<LazyExpressionBase, string>("expressionStringUnparsed");
    private static readonly AccessTools.FieldRef<WorldData, GameRes> Ledger =
        AccessTools.FieldRefAccess<WorldData, GameRes>("worldGameRes");
    private static readonly AccessTools.FieldRef<PlayerData, Action<List<Item>>> Collected =
        AccessTools.FieldRefAccess<PlayerData, Action<List<Item>>>(nameof(PlayerData.OnDropCollected));
    private static readonly List<string> owed = new List<string>();
    // Item rewards of the transition playing out, which go straight to the performing player.
    private static readonly HashSet<LazyExpression> performing = new HashSet<LazyExpression>();
    private static int transitions;
    private static float nextDelivery;

    // Hands this game's own character the campaign's rewards it has not received yet.
    internal static void Deliver()
    {
        if (UnityEngine.Time.unscaledTime < nextDelivery)
            return;
        nextDelivery = UnityEngine.Time.unscaledTime + DeliveryInterval;
        var player = MainGame.PlayerData;
        owed.Clear();
        foreach (var atom in Ledger(MainGame.WorldData).List)
        {
            if (atom.value > 0f && (atom.type.StartsWith(QuestMark, StringComparison.Ordinal) ||
                    atom.type.StartsWith(TechMark, StringComparison.Ordinal)) && player.GetRes(atom.type) <= 0f)
                owed.Add(atom.type);
        }
        foreach (string key in owed)
        {
            // Received once, even if handing it over fails.
            player.SetResWithoutSystemsCheck(key, 1f);
            if (key.StartsWith(TechMark, StringComparison.Ordinal))
                GiveTechnology(key.Substring(TechMark.Length));
            else if (QuestExpression(key) is { } expression)
                Give(expression);
        }
    }

    internal static void Clear()
    {
        performing.Clear();
        transitions = 0;
        nextDelivery = 0f;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestData), nameof(QuestData.Start))]
    private static void BeforeStart(QuestData __instance, out QuestStatus __state)
    {
        __state = __instance.status;
        if (__state is not (QuestStatus.InProgress or QuestStatus.Completed or QuestStatus.Canceled))
            Perform(__instance.Definition.execExpressionsStart);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestData), nameof(QuestData.Complete))]
    private static void BeforeComplete(QuestData __instance, out QuestStatus __state)
    {
        __state = __instance.status;
        if (__state != QuestStatus.Completed)
            Perform(__instance.Definition.execExpressionsFinish);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestData), nameof(QuestData.Start))]
    private static void Started(QuestData __instance, QuestStatus __state)
    {
        if (__state != __instance.status)
            RecordQuest(__instance, 0, __instance.Definition.execExpressionsStart);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestData), nameof(QuestData.Complete))]
    private static void Completed(QuestData __instance, QuestStatus __state)
    {
        if (__state != __instance.status)
            RecordQuest(__instance, 1, __instance.Definition.execExpressionsFinish);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(QuestData), nameof(QuestData.Start))]
    private static void AfterStart() => EndTransition();

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(QuestData), nameof(QuestData.Complete))]
    private static void AfterComplete() => EndTransition();

    // A technology's unlocks are shared knowledge; what it gives the character is a reward.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TechDef), nameof(TechDef.Unlock))]
    private static void Unlocked(TechDef __instance)
    {
        if (!WorldSync.Applying && Rewards(__instance))
            Record(TechMark + __instance.id);
    }

    // During a session, the performing player's item rewards go straight into their inventory
    // instead of their feet, where another player could take them.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LazyExpression), "EvaluateStringInternal")]
    private static bool TakeItem(LazyExpression __instance, ref string __result)
    {
        if (transitions == 0 || !performing.Remove(__instance) || !Receive(DroppedItem.Match(Source(__instance))))
            return true;
        __result = string.Empty;
        return false;
    }

    private static void Perform(List<LazyExpression> expressions)
    {
        transitions++;
        if (CoopSession.Current == null || WorldSync.Applying || expressions == null)
            return;
        foreach (var expression in expressions)
        {
            if (DroppedItem.IsMatch(Source(expression) ?? string.Empty))
                performing.Add(expression);
        }
    }

    private static void EndTransition()
    {
        if (--transitions <= 0)
        {
            transitions = 0;
            performing.Clear();
        }
    }

    private static void RecordQuest(QuestData quest, int list, List<LazyExpression> expressions)
    {
        // Another player's transition arrives as state; its rewards are delivered separately. A
        // save being prepared for loading is not the campaign being played yet.
        if (WorldSync.Applying || expressions == null || !Current(quest))
            return;
        for (int index = 0; index < expressions.Count; index++)
        {
            if (IsPersonal(Source(expressions[index])))
                Record($"{QuestMark}{quest.id}:{list}:{index}");
        }
    }

    private static void Record(string key)
    {
        if (MainGame.PlayerData == null || MainGame.WorldData == null)
            return;
        MainGame.PlayerData.SetResWithoutSystemsCheck(key, 1f);
        MainGame.WorldData.SetGameRes(key, 1f);
    }

    private static bool Current(QuestData quest) =>
        MainGame.Instance?.GameSave?.questSystemData?.questCollection.questsCache.TryGetValue(quest.id, out var current) == true &&
        current == quest;

    private static bool IsPersonal(string source)
    {
        if (string.IsNullOrEmpty(source))
            return false;
        foreach (string function in Personal)
        {
            if (source.StartsWith(function, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static LazyExpression QuestExpression(string key)
    {
        int end = key.LastIndexOf(':');
        int middle = end > 0 ? key.LastIndexOf(':', end - 1) : -1;
        if (middle <= QuestMark.Length || !int.TryParse(key.Substring(middle + 1, end - middle - 1), out int list) ||
            !int.TryParse(key.Substring(end + 1), out int index))
            return null;
        var definition = GameBalance.Me.GetDataOrNull<QuestDef>(key.Substring(QuestMark.Length, middle - QuestMark.Length));
        var expressions = list == 0 ? definition?.execExpressionsStart : definition?.execExpressionsFinish;
        return expressions != null && index < expressions.Count ? expressions[index] : null;
    }

    // An item goes straight into the character's inventory; other rewards run as the quest's own
    // expression, for this character.
    private static void Give(LazyExpression expression)
    {
        if (!Receive(DroppedItem.Match(Source(expression))))
            expression.Evaluate();
    }

    private static bool Receive(Match item)
    {
        if (!item.Success)
            return false;
        var player = MainGame.PlayerData;
        var reward = new Item(item.Groups[1].Value, int.Parse(item.Groups[2].Value));
        if (!player.inventory.AddItemToInventory(reward, out var added))
            return false;
        // The game's own pickup notification.
        Collected(player)?.Invoke(added);
        // What no longer fits falls at the player's feet.
        if (reward.Count > 0)
            MainGame.Instance.dropSystem.DropItem(reward, player.currentGameSceneId, player.position.Value);
        return true;
    }

    private static bool Rewards(TechDef technology)
    {
        if (technology.perksAfterUnlock.Count > 0 || !technology.addGameResAfterUnlock.IsEmpty() ||
            !technology.setGameResAfterUnlock.IsEmpty())
            return true;
        foreach (var expression in technology.expressionsAfterUnlock)
        {
            if (IsPersonal(Source(expression)))
                return true;
        }
        return false;
    }

    private static void GiveTechnology(string id)
    {
        var technology = GameBalance.Me.GetDataOrNull<TechDef>(id);
        if (technology == null)
            return;
        var save = MainGame.Instance.GameSave;
        foreach (string perk in technology.perksAfterUnlock)
            save.perkSystemData.AddPerk(perk);
        if (!technology.addGameResAfterUnlock.IsEmpty())
            MainGame.PlayerData.AddRes(technology.addGameResAfterUnlock);
        if (!technology.setGameResAfterUnlock.IsEmpty())
            MainGame.PlayerData.SetRes(technology.setGameResAfterUnlock);
        foreach (var expression in technology.expressionsAfterUnlock)
        {
            if (IsPersonal(Source(expression)))
                Give(expression);
        }
    }
}
