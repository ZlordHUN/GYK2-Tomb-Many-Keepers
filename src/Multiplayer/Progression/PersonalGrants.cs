using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Progression;

// Each character receives the personal rewards of the campaign's quests, technologies and town
// buildings once: items, personal values, talents, buffs, capacity, looks, achievements and their
// progress. The player whose action earned them receives them as it plays out, a town building's
// in the game whose craft finished it, which is often the host's even when another keeper brought
// the last materials; every other character receives their own in their own game, now or whenever
// they next play the campaign. A story item taken from the world is owed the same way, and a copy
// handed in by one keeper is taken from the others the same way. The campaign records each reward it
// has handed out and each character the ones it has received, so none is given twice, even once
// spent or lost, and no character receives a second copy of a story item. The values the campaign
// shares instead are not rewards.
[HarmonyPatch]
internal static class PersonalGrants
{
    private const string QuestMark = "tmk_grant:quest:";
    private const string TechMark = "tmk_grant:tech:";
    private const string TownMark = "tmk_grant:town:";
    private const string ItemMark = "tmk_grant:item:";
    // A town building's craft, which the game names after the building.
    private const string TownCraft = "town_building_craft:";
    private const float DeliveryInterval = 1f;
    // Rewards each keeper's game applies for itself.
    private static readonly string[] Personal =
    {
        "DropItem(", "AddPPar(", "SetPPar(", "AddInspiration(", "AddTalentValue(", "AddPerk(",
        "IncreasePlayerInventory(", "ReducePlayerInventory(", "IncreaseOverheadStackLimit(",
        "UnlockCustomizationPart(", "UnlockCustomizationColor(", "SuperUnlockBodyCustomization(", "AchUnlock(",
        "AchTriggerCountable(", "UnlockHudDaySprite("
    };
    private static readonly Regex DroppedItem = new Regex("^DropItem\\(\\s*\"([^\"]+)\"\\s*,\\s*\"?(\\d+)\"?\\s*\\)$");
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
    // The world whose earlier hand-ins the host has caught up with.
    private static WorldData caughtUp;
    // The town buildings' crafts' finishing expressions, and the building each finishes.
    private static GameBalance balance;
    private static Dictionary<List<LazyExpression>, string> townsByEnd;
    private static Dictionary<string, List<LazyExpression>> townEnds;

    // Hands this game's own character the campaign's rewards it has not received yet.
    internal static void Deliver()
    {
        if (UnityEngine.Time.unscaledTime < nextDelivery)
            return;
        nextDelivery = UnityEngine.Time.unscaledTime + DeliveryInterval;
        if (CoopSession.IsHosting && caughtUp != MainGame.WorldData)
        {
            caughtUp = MainGame.WorldData;
            HandIns.CatchUp();
        }
        var player = MainGame.PlayerData;
        owed.Clear();
        foreach (var atom in Ledger(MainGame.WorldData).List)
        {
            if (atom.value > 0f && atom.type.StartsWith("tmk_grant:", StringComparison.Ordinal) && player.GetRes(atom.type) <= 0f)
                owed.Add(atom.type);
        }
        foreach (string key in owed)
        {
            // An earlier reward of the same delivery may have been this story item.
            if (player.GetRes(key) > 0f)
                continue;
            // Received once, even if handing it over fails.
            player.SetResWithoutSystemsCheck(key, 1f);
            if (key.StartsWith(TechMark, StringComparison.Ordinal))
                GiveTechnology(key.Substring(TechMark.Length));
            else if (key.StartsWith(ItemMark, StringComparison.Ordinal))
            {
                string id = key.Substring(ItemMark.Length);
                if (StoryItems.Held(id) == 0)
                    Receive(id, 1);
            }
            // Another keeper handed in what this one holds a copy of.
            else if (key.StartsWith(HandIns.Mark, StringComparison.Ordinal))
                HandIns.Apply(key);
            else if (Expression(key) is { } expression)
                Give(expression);
        }
    }

    // A story item taken from the world: its taker keeps it, and every other character is owed one.
    internal static void RecordItem(string id) => Record(ItemMark + id);

    internal static List<GameResAtom> LedgerAtoms => Ledger(MainGame.WorldData).List;

    internal static void Clear()
    {
        performing.Clear();
        transitions = 0;
        nextDelivery = 0f;
        caughtUp = null;
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

    // A town building finished, by whichever game ran its craft.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WgoData), nameof(WgoData.EvaluateOnCraftEndExpressions))]
    private static void BeforeCraftEnd(List<LazyExpression> onCraftEndExpressions, out bool __state)
    {
        __state = TownOf(onCraftEndExpressions) != null;
        if (__state)
            Perform(onCraftEndExpressions);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WgoData), nameof(WgoData.EvaluateOnCraftEndExpressions))]
    private static void CraftEnded(List<LazyExpression> onCraftEndExpressions, bool __state)
    {
        if (!__state || WorldSync.Applying)
            return;
        string town = TownOf(onCraftEndExpressions);
        for (int index = 0; index < onCraftEndExpressions.Count; index++)
        {
            if (IsPersonal(onCraftEndExpressions[index]))
                Record($"{TownMark}{town}:{index}");
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(WgoData), nameof(WgoData.EvaluateOnCraftEndExpressions))]
    private static void AfterCraftEnd(bool __state)
    {
        if (__state)
            EndTransition();
    }

    // A technology's unlocks are shared knowledge; what it gives the character is a reward.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TechDef), nameof(TechDef.Unlock))]
    private static void Unlocked(TechDef __instance)
    {
        if (!WorldSync.Applying && Rewards(__instance))
            Record(TechMark + __instance.id);
    }

    // During a session, the performing player's item rewards go straight into their inventory
    // instead of their feet, where another player could take them. The native drop answers true
    // however it is evaluated.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LazyExpression), "EvaluateInScope")]
    private static bool TakeItem(LazyExpression __instance, ref object __result)
    {
        if (transitions == 0 || !performing.Remove(__instance) || !Receive(DroppedItem.Match(Source(__instance) ?? string.Empty)))
            return true;
        __result = true;
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
            if (IsPersonal(expressions[index]))
                Record($"{QuestMark}{quest.id}:{list}:{index}");
            else
                HandIns.QuestTakes(quest, list, index, expressions[index]);
        }
    }

    // A reward or hand-in handed out: in the campaign's ledger, and as received by this game's own character.
    internal static void Record(string key)
    {
        if (MainGame.PlayerData == null || MainGame.WorldData == null)
            return;
        MainGame.PlayerData.SetResWithoutSystemsCheck(key, 1f);
        MainGame.WorldData.SetGameRes(key, 1f);
    }

    private static bool Current(QuestData quest) =>
        MainGame.Instance?.GameSave?.questSystemData?.questCollection.questsCache.TryGetValue(quest.id, out var current) == true &&
        current == quest;

    // A personal reward, as the game parses it, which also reads shorthand such as $value = 1.
    private static bool IsPersonal(LazyExpression expression)
    {
        string parsed = expression?.GetRawExpressionString();
        if (string.IsNullOrEmpty(parsed) || CampaignValues.Changes(parsed))
            return false;
        parsed = parsed.TrimStart();
        foreach (string function in Personal)
        {
            if (parsed.StartsWith(function, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // A recorded quest or town building reward: its definition, list and index.
    private static LazyExpression Expression(string key)
    {
        bool quest = key.StartsWith(QuestMark, StringComparison.Ordinal);
        if (!quest && !key.StartsWith(TownMark, StringComparison.Ordinal))
            return null;
        string rest = key.Substring(quest ? QuestMark.Length : TownMark.Length);
        int end = rest.LastIndexOf(':');
        if (end <= 0 || !int.TryParse(rest.Substring(end + 1), out int index))
            return null;
        List<LazyExpression> expressions;
        if (quest)
        {
            int middle = rest.LastIndexOf(':', end - 1);
            if (middle <= 0 || !int.TryParse(rest.Substring(middle + 1, end - middle - 1), out int list))
                return null;
            var definition = GameBalance.Me.GetDataOrNull<QuestDef>(rest.Substring(0, middle));
            expressions = list == 0 ? definition?.execExpressionsStart : definition?.execExpressionsFinish;
        }
        else
        {
            Towns();
            townEnds.TryGetValue(rest.Substring(0, end), out expressions);
        }
        return expressions != null && index >= 0 && index < expressions.Count ? expressions[index] : null;
    }

    // The town building whose craft finishes with these expressions.
    private static string TownOf(List<LazyExpression> expressions)
    {
        Towns();
        return expressions != null && townsByEnd.TryGetValue(expressions, out string town) ? town : null;
    }

    private static void Towns()
    {
        if (townsByEnd != null && balance == GameBalance.Me)
            return;
        balance = GameBalance.Me;
        townsByEnd = new Dictionary<List<LazyExpression>, string>();
        townEnds = new Dictionary<string, List<LazyExpression>>(StringComparer.Ordinal);
        foreach (var craft in balance.craftDefs)
        {
            if (craft?.onCraftEndExpressions == null || craft.id == null || !craft.id.StartsWith(TownCraft, StringComparison.Ordinal))
                continue;
            string town = craft.id.Substring(TownCraft.Length);
            townsByEnd[craft.onCraftEndExpressions] = town;
            townEnds[town] = craft.onCraftEndExpressions;
        }
    }

    // An item goes straight into the character's inventory; other rewards run as their own
    // expression, for this character. A value the campaign now shares reaches everyone as it
    // changes, though an older campaign may still have recorded it as owed.
    private static void Give(LazyExpression expression)
    {
        if (CampaignValues.Changes(expression.GetRawExpressionString()))
            return;
        if (!Receive(DroppedItem.Match(Source(expression) ?? string.Empty)))
            expression.Evaluate();
    }

    private static bool Receive(Match item) => item.Success && Receive(item.Groups[1].Value, int.Parse(item.Groups[2].Value));

    private static bool Receive(string id, int count)
    {
        var player = MainGame.PlayerData;
        var reward = new Item(id, count);
        if (!player.inventory.AddItemToInventory(reward, out var added))
            return false;
        // However a story item comes, it is this character's copy of it.
        if (StoryItems.Is(reward.Definition))
            player.SetResWithoutSystemsCheck(ItemMark + id, 1f);
        // The game's own pickup notification.
        Collected(player)?.Invoke(added);
        // What no longer fits falls at the player's feet.
        if (reward.Count > 0)
            MainGame.Instance.dropSystem.DropItem(reward, player.currentGameSceneId, player.position.Value);
        return true;
    }

    private static bool Rewards(TechDef technology)
    {
        if (technology.perksAfterUnlock.Count > 0 || HasPersonal(technology.addGameResAfterUnlock) ||
            HasPersonal(technology.setGameResAfterUnlock))
            return true;
        foreach (var expression in technology.expressionsAfterUnlock)
        {
            if (IsPersonal(expression))
                return true;
        }
        return false;
    }

    private static bool HasPersonal(GameRes values)
    {
        if (values == null)
            return false;
        foreach (var atom in values.List)
        {
            if (!CampaignValues.Shared(atom.type))
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
        var player = MainGame.PlayerData;
        foreach (string perk in technology.perksAfterUnlock)
            save.perkSystemData.AddPerk(perk);
        if (technology.addGameResAfterUnlock != null)
        {
            foreach (var atom in technology.addGameResAfterUnlock.List)
            {
                if (!CampaignValues.Shared(atom.type))
                    player.AddRes(atom.type, atom.value);
            }
        }
        if (technology.setGameResAfterUnlock != null)
        {
            foreach (var atom in technology.setGameResAfterUnlock.List)
            {
                if (!CampaignValues.Shared(atom.type))
                    player.SetRes(atom.type, atom.value);
            }
        }
        foreach (var expression in technology.expressionsAfterUnlock)
        {
            if (IsPersonal(expression))
                Give(expression);
        }
    }
}
