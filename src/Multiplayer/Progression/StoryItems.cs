using System;
using System.Collections.Generic;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Multiplayer.Progression;

// The story leaves some things for its keeper to find instead of handing them over: the medallion,
// signet and key behind the prison's secret wall, the inquisitor's note on the prison table, the
// notes in the temple's chests, and the first shovel and hammer in the graveyard chest, which no
// recipe makes. Only one keeper could take each. Whoever takes one now keeps it, and every other
// character receives their own once, now or whenever they next play the campaign. A story item is
// a one-of-a-kind quest item, or a tool, that no craft makes; a quest item a keeper crafts, such as
// the prosthesis or the dynamite, or one that comes in numbers, such as the looters' keys, stays
// with whoever has it, as do a chest's other contents.
[HarmonyPatch]
internal static class StoryItems
{
    private const string QuestBag = "quest_bag";
    private static GameBalance balance;
    private static HashSet<string> crafted;

    internal static bool Is(ItemDef definition) => definition != null &&
        (definition.isTool || definition.stackCount == 1 && definition.itemGroupIds.Contains(QuestBag)) &&
        !Crafted().Contains(definition.id);

    internal static bool InQuestBag(ItemDef definition) => definition?.itemGroupIds.Contains(QuestBag) == true;

    // Whether some craft makes the item, so a keeper may hold their own beside any copy.
    internal static bool Made(string id) => Crafted().Contains(id);

    // How many of the item the keeper carries, in their bags and on their tool belt.
    internal static int Held(string id)
    {
        var player = MainGame.PlayerData;
        return (player?.inventory?.Data?.GetTotalCountInInventory(id) ?? 0) +
               (player?.toolBeltInventory?.Data?.GetTotalCountInInventory(id) ?? 0);
    }

    // A joined player's pickup, which the host hands over instead of the native pickup.
    internal static void Claimed(Item item)
    {
        if (Is(item?.Definition))
            Taken(item.id);
    }

    // The native pickup: the host's own, and any outside a session.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.CollectDrop))]
    private static void PickingUp(PlayerData __instance, DropView dropView, out (string id, int count) __state)
    {
        var item = dropView?.Data?.Item;
        __state = __instance == MainGame.PlayerData && Is(item?.Definition) ? (item.id, item.Count) : (null, 0);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.CollectDrop))]
    private static void PickedUp(DropView dropView, (string id, int count) __state)
    {
        if (__state.id != null && __state.count > (dropView?.Data?.Item?.Count ?? 0))
            Taken(__state.id);
    }

    // Taken from a chest's window into the keeper's own bags; moving between their own bags changes
    // nothing they carry.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoryUIItemMoveOpHandler), "TryMoveItem")]
    private static void Moving(UIItemCell itemCell, out (string id, int held) __state)
    {
        var item = itemCell?.DisplayingItem;
        __state = Is(item?.Definition) ? (item.id, Held(item.id)) : (null, 0);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryUIItemMoveOpHandler), "TryMoveItem")]
    private static void Moved(bool __result, (string id, int held) __state)
    {
        if (__result && __state.id != null && Held(__state.id) > __state.held)
            Taken(__state.id);
    }

    // As with a quest's rewards, a story item found before the campaign is hosted is owed once it is.
    private static void Taken(string id)
    {
        if (!WorldSync.Applying)
            PersonalGrants.RecordItem(id);
    }

    // What some craft makes; a craft that hands back what it was given, as the tower door returns
    // the medallion, makes nothing new.
    private static HashSet<string> Crafted()
    {
        if (crafted != null && balance == GameBalance.Me)
            return crafted;
        balance = GameBalance.Me;
        crafted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var craft in balance.craftDefs)
        {
            var given = new HashSet<string>(StringComparer.Ordinal);
            foreach (var need in craft.needItems)
                given.Add(need.id);
            foreach (var output in craft.outputItems.chanceOutputItems)
                AddMade(output, given);
            foreach (var group in craft.outputItems.groupChanceOutputItems)
            {
                foreach (var output in group.chanceItems)
                    AddMade(output, given);
            }
        }
        return crafted;
    }

    private static void AddMade(ChanceOutputItem output, HashSet<string> given)
    {
        if (output.isStarGroup && balance.starGroupItemsCache.TryGetValue(output.id, out var stars))
        {
            foreach (var star in stars)
                AddMade(star.id, given);
        }
        else
            AddMade(output.id, given);
    }

    private static void AddMade(string id, HashSet<string> given)
    {
        if (!string.IsNullOrEmpty(id) && !given.Contains(id))
            crafted.Add(id);
    }
}
