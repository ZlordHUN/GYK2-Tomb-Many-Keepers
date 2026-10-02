using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Multiplayer.Progression;

// Every keeper holds their own copy of what the campaign hands out, a quest's reward or a story item found in the
// world, so no copy strands a quest. The story still takes such an item once: when a keeper hands it in, paying it in
// a quest's answer as when giving the violin or installing a portal stone, when a quest takes it, as leaving the
// prison takes its key, or when a craft uses up a story item, as the tower's door uses the doctor's code, every other
// keeper's copy goes too, now or whenever they next play, and a keeper still to receive theirs ends up without it.
// The quest's stick of dynamite, which a keeper can also craft, goes from everyone the first time anyone uses one up.
// What a keeper brings of their own, such as beer, materials or a crafted prosthesis, is theirs alone to hand in.
[HarmonyPatch]
internal static class HandIns
{
    internal const string Mark = "tmk_grant:handin:";
    private static readonly Regex Removal = new Regex("^\\s*RemoveItem\\(\\s*\"([^\"]+)\"\\s*\\)\\s*$");
    private static readonly Regex Reward = new Regex("^\\s*DropItem\\(\\s*\"([^\"]+)\"\\s*,\\s*\"?(\\d+)");
    private static readonly AccessTools.FieldRef<UIMultiAnswerOption, AnswerVisualData> AnswerOf =
        AccessTools.FieldRefAccess<UIMultiAnswerOption, AnswerVisualData>("visualData");
    private static GameBalance balance;
    // What the quests hand out, each keeper their own, and how many quests hand out each.
    private static Dictionary<string, int> questRewards;
    // How many of each a quest hands out at most.
    private static Dictionary<string, int> rewardCounts;

    // An item every keeper receives their own copy of, and has no other way to come by: a story item, or a quest's
    // reward that belongs in the quest bag or that no craft makes.
    internal static bool Copied(string id)
    {
        var definition = string.IsNullOrEmpty(id) ? null : GameBalance.Me.GetDataOrNull<ItemDef>(id);
        return definition != null && (StoryItems.Is(definition) ||
            QuestRewards().ContainsKey(id) && (StoryItems.InQuestBag(definition) || !StoryItems.Made(id)));
    }

    // Host, once a campaign is loaded: a hand-in made before the campaign recorded them, or alone before it was
    // hosted, left the other keepers' copies behind. A quest that took a copied item being done, its item is nobody's
    // to hand in any more, when only one quest gives it and so gave it before. The campaign's ledger then takes the
    // hand-in for every keeper, whoever made it, and a keeper without a copy loses nothing.
    internal static void CatchUp()
    {
        var quests = MainGame.Instance?.GameSave?.questSystemData?.questCollection?.questsCache;
        if (quests == null || MainGame.WorldData == null)
            return;
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var atom in PersonalGrants.LedgerAtoms)
        {
            if (atom.type.StartsWith(Mark, StringComparison.Ordinal) && atom.type.Split('|') is { Length: >= 3 } parts)
                recorded.Add(parts[0].Substring(Mark.Length) + "|" + parts[2]);
        }
        foreach (var quest in quests.Values)
        {
            if (quest?.status != QuestStatus.Completed || quest.Definition == null)
                continue;
            var definition = quest.Definition;
            if (definition.finishCheck?.phraseReqs != null)
            {
                foreach (var requirement in definition.finishCheck.phraseReqs)
                {
                    if (requirement.entity == QuestPhraseRequirement.Entity.Item &&
                        requirement.requirement == QuestPhraseRequirement.Requirement.Price && requirement.itemCount != null)
                        Owed(requirement.itemCount.itemId, requirement.itemCount.count, "answer." + definition.id, recorded);
                }
            }
            for (int list = 0; list < 2; list++)
            {
                var expressions = list == 0 ? definition.execExpressionsStart : definition.execExpressionsFinish;
                for (int index = 0; expressions != null && index < expressions.Count; index++)
                {
                    var match = Removal.Match(expressions[index]?.GetRawExpressionString() ?? string.Empty);
                    if (match.Success)
                        Owed(match.Groups[1].Value, 1, $"quest.{quest.id}.{list}.{index}", recorded);
                }
            }
        }
    }

    private static void Owed(string id, int count, string source, HashSet<string> recorded)
    {
        if (count <= 0 || recorded.Contains(id + "|" + source) || !Copied(id) || QuestRewards().TryGetValue(id, out int givers) && givers > 1)
            return;
        MainGame.WorldData.SetGameRes($"{Mark}{id}|{count}|{source}", 1f);
    }

    // Another keeper's hand-in, taken from this keeper's own bags as far as they hold it.
    internal static void Apply(string key)
    {
        string[] parts = key.Substring(Mark.Length).Split('|');
        if (parts.Length < 3 || parts[0].Length == 0 || !int.TryParse(parts[1], out int count) || count <= 0)
            return;
        var inventory = MainGame.PlayerData.inventory;
        int held = inventory.Data.GetTotalCountInInventory(parts[0]);
        if (held > 0)
            inventory.RemoveItemById(parts[0], Math.Min(held, count));
    }

    // A quest's own taking, as it plays out for the keeper performing it, whose quest checked they had it.
    internal static void QuestTakes(QuestData quest, int list, int index, LazyExpression expression)
    {
        var match = Removal.Match(expression?.GetRawExpressionString() ?? string.Empty);
        if (match.Success && Copied(match.Groups[1].Value))
            Record(match.Groups[1].Value, 1, $"quest.{quest.id}.{list}.{index}");
    }

    // The price of a quest's answer, paid by the keeper of the game whose conversation it is.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), "HandlePrice")]
    private static void Paying(UIMultiAnswerOption __instance, SmartRes priceSmartRes, out List<(string id, int held)> __state)
    {
        __state = null;
        if (priceSmartRes?.items == null || Quest(__instance) == null)
            return;
        foreach (var cost in priceSmartRes.items)
        {
            if (cost != null && Copied(cost.itemId))
                (__state ??= new List<(string, int)>()).Add((cost.itemId, StoryItems.Held(cost.itemId)));
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIMultiAnswerOption), "HandlePrice")]
    private static void Paid(UIMultiAnswerOption __instance, List<(string id, int held)> __state)
    {
        if (__state == null || WorldSync.Applying)
            return;
        string quest = Quest(__instance).id;
        foreach (var (id, held) in __state)
        {
            int paid = held - StoryItems.Held(id);
            if (paid > 0)
                Record(id, paid, "answer." + quest);
        }
    }

    // A craft using up a copied item, other than one it hands back: a story item each time, and one a keeper can also
    // craft only the first time, up to what its quest gave each keeper, since it may be the keeper's own.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftElementBase), nameof(CraftElementBase.RemoveCraftRequirements))]
    private static void Crafted(CraftElementBase __instance)
    {
        var craft = __instance.Def;
        if (craft == null || WorldSync.Applying)
            return;
        foreach (var item in __instance.CraftInput)
        {
            if (item?.Definition == null || item.Count <= 0 || Makes(craft, item.id) || !Copied(item.id))
                continue;
            if (StoryItems.Is(item.Definition))
                Record(item.id, item.Count, "craft." + craft.id);
            else if (GivenEach(item.id) is var given and > 0)
                Record(item.id, Math.Min(item.Count, given), "craft");
        }
    }

    private static QuestDef Quest(UIMultiAnswerOption option)
    {
        string answer = AnswerOf(option)?.id;
        return !string.IsNullOrEmpty(answer) && GameBalance.Me.questDefByReqPhrase.TryGetValue(answer, out var quest) ? quest : null;
    }

    private static bool Makes(CraftDefBase craft, string id)
    {
        foreach (var output in craft.outputItems.chanceOutputItems)
        {
            if (output.id == id)
                return true;
        }
        foreach (var group in craft.outputItems.groupChanceOutputItems)
        {
            foreach (var output in group.chanceItems)
            {
                if (output.id == id)
                    return true;
            }
        }
        return false;
    }

    // The key carries the item first, as item ids may hold colons, then how many and where it went.
    private static void Record(string id, int count, string source) => PersonalGrants.Record($"{Mark}{id}|{count}|{source}");

    private static int GivenEach(string id) => QuestRewards() != null && rewardCounts.TryGetValue(id, out int given) ? given : 0;

    private static Dictionary<string, int> QuestRewards()
    {
        if (questRewards != null && balance == GameBalance.Me)
            return questRewards;
        balance = GameBalance.Me;
        questRewards = new Dictionary<string, int>(StringComparer.Ordinal);
        rewardCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var quest in balance.questDefs)
        {
            Add(quest.execExpressionsStart);
            Add(quest.execExpressionsFinish);
        }
        return questRewards;
    }

    private static void Add(List<LazyExpression> expressions)
    {
        if (expressions == null)
            return;
        foreach (var expression in expressions)
        {
            var match = Reward.Match(expression?.GetRawExpressionString() ?? string.Empty);
            if (!match.Success)
                continue;
            string id = match.Groups[1].Value;
            questRewards[id] = questRewards.TryGetValue(id, out int givers) ? givers + 1 : 1;
            int count = int.Parse(match.Groups[2].Value);
            rewardCounts[id] = rewardCounts.TryGetValue(id, out int most) ? Math.Max(most, count) : count;
        }
    }
}
