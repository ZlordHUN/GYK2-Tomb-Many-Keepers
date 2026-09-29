using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// Any keeper commands the party with its flag (design doc: the latest order wins). A joined player's
// take or placement of a flag is replayed in the host's game by the game's own handler, and the host
// then keeps the flag on that player's keeper, so the host's allies follow them.
[HarmonyPatch]
internal static class FightFlags
{
    private static readonly AccessTools.FieldRef<WGOInteractionHandlerBase, Wgo> Assigned =
        AccessTools.FieldRefAccess<WGOInteractionHandlerBase, Wgo>("assignedWgo");

    // Host: flags that joined players carry, by their slot.
    private static readonly Dictionary<int, Wgo> carried = new Dictionary<int, Wgo>();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FlagInteractionHandler), nameof(FlagInteractionHandler.Interact))]
    private static void Took(WGOInteractionHandlerBase __instance, PlayerController interactor, bool __result) =>
        Share(__instance, interactor, __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FlagStandInteractionHandler), nameof(FlagStandInteractionHandler.Interact))]
    private static void UsedStand(WGOInteractionHandlerBase __instance, PlayerController interactor, bool __result) =>
        Share(__instance, interactor, __result);

    private static void Share(WGOInteractionHandlerBase handler, PlayerController interactor, bool done)
    {
        var session = CoopSession.Current;
        var target = Assigned(handler);
        if (!done || !CoopSession.IsGuest || !WorldSync.Sharing || interactor != MainGame.PlayerController ||
            target == null || target.Data == null)
            return;
        int slot = session.LocalSlot;
        var id = target.Data.UniqueId.Guid;
        WorldSync.Queue(WorldSync.Change.FightFlag, id, writer =>
        {
            writer.Write((byte)slot);
            WorldSync.WriteId(writer, id);
        });
    }

    internal static void Apply(BinaryReader reader)
    {
        int slot = reader.ReadByte();
        var target = GameScene.GetWgoViewGlobal(new SGuid(WorldSync.ReadId(reader)));
        if (!CoopSession.IsHosting || target == null || target.InteractionHandler == null)
            return;
        // The host's keeper stands in for the joined one for this one interaction, then gets its own flag back.
        var host = MainGame.PlayerController;
        var own = host.attachedWgo;
        carried.TryGetValue(slot, out var theirs);
        host.attachedWgo = theirs;
        try
        {
            target.InteractionHandler.Interact(host);
        }
        finally
        {
            var now = host.attachedWgo;
            host.attachedWgo = own;
            host.View.Banner.Show(own != null, own != null ? own.Data.MainWgoPartData.variationId : "");
            if (now != null)
                carried[slot] = now;
            else
                carried.Remove(slot);
            Debug.Log($"[Multiplayer] Keeper {slot} {(now != null ? "carries" : "set down")} a flag");
        }
    }

    // As the game keeps a carried flag on its own keeper every frame.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), "Update")]
    private static void Follow()
    {
        if (carried.Count == 0 || !CoopSession.IsHosting)
            return;
        foreach (var pair in carried)
        {
            var body = RemoteKeeper.Talking(pair.Key)?.GetComponentInParent<PlayerPhysicalBody>();
            if (body != null && pair.Value != null && pair.Value.Data != null)
                pair.Value.Data.Position = body.transform.position;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped() => carried.Clear();
}
