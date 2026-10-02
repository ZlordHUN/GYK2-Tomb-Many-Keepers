using System;
using System.Collections.Generic;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// /spawn corpse [amount]: bodies by this player's keeper, as GYK1's, of the tier the game gives them now, made as the
// game's own Generate Body node makes one. They drop into the world, which every player shares.
internal static partial class ChatCommands
{
    private const int MaxBodies = 20;

    private static void Spawn(List<string> args, Action<string> respond)
    {
        if (args.Count < 2 || !IsWord(args[1], "corpse"))
        {
            respond("Usage: /spawn corpse [amount]");
            return;
        }
        int amount = 1;
        if (args.Count > 2 && !TryParseInt(args[2], out amount))
        {
            respond($"Not an amount: {args[2]}");
            return;
        }
        if (amount < 1 || amount > MaxBodies)
        {
            respond($"The amount is 1 to {MaxBodies}.");
            return;
        }
        var player = MainGame.PlayerData;
        int tier = player.GetResInt("bodies_tier");
        var bodies = GameBalance.Me.bodyDefs.FindAll(body => body.tier == tier);
        if (bodies.Count == 0)
        {
            respond($"The game has no bodies of tier {tier}.");
            return;
        }
        var at = MainGame.PlayerController.PlayerData.position.Value;
        for (int i = 0; i < amount; i++)
            MainGame.Instance.dropSystem.DropItem(bodies[UnityEngine.Random.Range(0, bodies.Count)].GenerateItem(), player.currentGameSceneId, at);
        Debug.Log($"[Multiplayer] /spawn dropped {amount} bodies of tier {tier}");
        respond(amount == 1 ? "Spawned a body by you." : $"Spawned {amount} bodies by you.");
    }

    private static string[] CompleteSpawn(Typing typing) => typing.Index == 1 ? MatchPrefix(typing.Partial, new[] { "corpse" }) : null;
}
