using System;
using System.Collections.Generic;
using GYK2.TombManyKeepers.Multiplayer.Chat;
using GYK2.TombManyKeepers.Network.Session;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// /give: items to this player or another, and this player's own money and energy, as GYK1's. An item for another player
// goes through the host, which checks that cheats are on for the giver and hands it to them.
internal static partial class ChatCommands
{
    internal const int MaxAmount = 999;
    private const float MaxMoney = 1000000f;

    private static void Give(List<string> args, Action<string> respond)
    {
        var session = CoopSession.Current;
        if (args.Count < 2)
        {
            respond("Usage: /give <item> [amount] or /give <player> <item> [amount]");
            return;
        }
        int target = session.LocalSlot, itemAt = 1, amountAt = 2;
        // A word that means an item stays one, even where a player has that name.
        int named = ItemCatalog.Recognizes(args[1]) ? 0 : FindPlayer(args[1]);
        if (named != 0)
        {
            if (args.Count < 3)
            {
                respond($"Usage: /give {args[1]} <item> [amount]");
                return;
            }
            target = named;
            itemAt = 2;
            amountAt = 3;
        }
        string requested = args[itemAt];
        int amount = 1;
        if (args.Count > amountAt && !TryParseInt(args[amountAt], out amount))
        {
            respond($"Not an amount: {args[amountAt]}");
            return;
        }
        if (IsWord(requested, "money") || IsWord(requested, "energy"))
        {
            if (target != session.LocalSlot)
                respond($"{Capitalized(requested)} is each player's own; they give it to themselves.");
            else if (IsWord(requested, "money"))
                GiveMoney(amount, respond);
            else
                GiveEnergy(amount, respond);
            return;
        }
        if (!ItemCatalog.TryResolve(requested, out string id, out string name, out string error))
        {
            respond(error);
            return;
        }
        amount = Mathf.Clamp(amount, 1, MaxAmount);
        if (target == session.LocalSlot)
        {
            Receive(id, amount, out int dropped);
            respond($"Gave you {amount}x {name} [{id}]" + (dropped > 0 ? $"; {dropped} lie at your feet, as your bag is full." : "."));
            return;
        }
        CoopSession.GiveItem(target, id, amount, respond);
    }

    // The item into this player's bag, a stack at a time; what does not fit drops at their feet. How many dropped.
    internal static void Receive(string id, int amount, out int dropped)
    {
        dropped = 0;
        var player = MainGame.PlayerData;
        var definition = GameBalance.Me.GetData<ItemDef>(id);
        int stack = definition != null && definition.stackCount > 0 ? definition.stackCount : amount;
        for (int left = amount; left > 0; left -= stack)
        {
            var item = new Item(id, Math.Min(stack, left));
            if (player.Inventory.AddItemToInventory(item))
                continue;
            MainGame.Instance.dropSystem.DropItem(item, player.currentGameSceneId, MainGame.PlayerController.PlayerData.position.Value);
            dropped += item.Count;
        }
    }

    // Another player's /give for this one, handed over by the host.
    internal static void Received(string giver, string id, int amount)
    {
        Receive(id, amount, out int dropped);
        string name = ItemCatalog.NameOf(id);
        ChatLog.Notice($"Received {amount}x {name} [{id}] from {giver}'s /give" + (dropped > 0 ? $"; {dropped} lie at your feet." : "."));
    }

    private static void GiveMoney(int amount, Action<string> respond)
    {
        var money = PlayerMoneyGameResSystem.GetSystem();
        float value = Mathf.Clamp(amount, -MaxMoney, MaxMoney);
        money.Add(value);
        respond($"{(value < 0f ? "Took" : "Added")} {Mathf.Abs(value)} money; you have {MainGame.PlayerData.GetRes("money")}.");
    }

    private static void GiveEnergy(int amount, Action<string> respond)
    {
        var energy = PlayerEnergyGameResSystem.GetSystem();
        float before = MainGame.PlayerData.GetRes("energy");
        energy.Add(amount);
        float after = MainGame.PlayerData.GetRes("energy"), change = after - before;
        respond(Mathf.Approximately(change, 0f) ? $"Energy stays at {after:0.##}/{energy.Max:0.##}."
            : $"{(change < 0f ? "Took" : "Added")} {Mathf.Abs(change):0.##} energy; you have {after:0.##}/{energy.Max:0.##}.");
    }

    private static string Capitalized(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant();

    private static string[] CompleteGive(Typing typing)
    {
        if (typing.Index == 1)
            return Merge(ItemCatalog.Candidates(typing.Partial, personal: true), MatchPrefix(typing.Partial, PlayerNames()));
        if (typing.Index == 2 && !ItemCatalog.Recognizes(typing.Args[1]) && FindPlayer(typing.Args[1]) != 0)
            return ItemCatalog.Candidates(typing.Partial, personal: false);
        return null;
    }
}
