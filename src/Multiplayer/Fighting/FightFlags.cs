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

// Any keeper commands the party with its flag (design doc: the latest order wins). Each player's take
// or placement of a flag is replayed in the others' games by the game's own handler, and they keep the
// flag on that player's keeper; in the host's game the allies then follow it.
[HarmonyPatch]
internal static class FightFlags
{
    private static readonly AccessTools.FieldRef<WGOInteractionHandlerBase, Wgo> Assigned =
        AccessTools.FieldRefAccess<WGOInteractionHandlerBase, Wgo>("assignedWgo");

    // Flags that other players carry, by their slot.
    private static readonly Dictionary<int, Wgo> carried = new Dictionary<int, Wgo>();
    private static readonly Dictionary<int, string> looks = new Dictionary<int, string>();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FlagInteractionHandler), nameof(FlagInteractionHandler.Interact))]
    private static void Took(WGOInteractionHandlerBase __instance, PlayerController interactor, bool __result) =>
        Share(__instance, interactor, __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FlagStandInteractionHandler), nameof(FlagStandInteractionHandler.Interact))]
    private static void UsedStand(WGOInteractionHandlerBase __instance, PlayerController interactor, bool __result) =>
        Share(__instance, interactor, __result);

    // A barricade holds a flag too.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BarricadeInteractionHandler), nameof(BarricadeInteractionHandler.Interact))]
    private static void UsedBarricade(WGOInteractionHandlerBase __instance, PlayerController interactor, bool __result) =>
        Share(__instance, interactor, __result);

    private static void Share(WGOInteractionHandlerBase handler, PlayerController interactor, bool done)
    {
        var session = CoopSession.Current;
        var target = Assigned(handler);
        if (!done || session == null || !WorldSync.Sharing || interactor != MainGame.PlayerController ||
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
        var session = CoopSession.Current;
        if (session == null || slot == session.LocalSlot || target == null || target.InteractionHandler == null)
            return;
        // This game's keeper stands in for the other one for this one interaction, then gets its own flag back.
        // A carried flag's look lives only in the banner, and setting it down reads it from there.
        var me = MainGame.PlayerController;
        var banner = me.View.Banner;
        var own = me.attachedWgo;
        string ownLook = Look(banner);
        carried.TryGetValue(slot, out var theirs);
        looks.TryGetValue(slot, out var theirLook);
        me.attachedWgo = theirs;
        banner.Show(theirs != null, theirLook ?? "");
        try
        {
            target.InteractionHandler.Interact(me);
        }
        finally
        {
            var now = me.attachedWgo;
            string nowLook = now != null ? Look(banner) : "";
            me.attachedWgo = own;
            banner.Show(own != null, ownLook);
            if (now != null)
            {
                carried[slot] = now;
                looks[slot] = nowLook;
            }
            else
            {
                carried.Remove(slot);
                looks.Remove(slot);
            }
            RemoteKeeper.ShowBanner(slot, now != null, nowLook);
            Debug.Log($"[Multiplayer] Keeper {slot} {(now != null ? "carries" : "set down")} a flag");
        }
    }

    // Allies pushing the carrier's body must not drag the flag, or they twitch at its rim.
    private const float FlagDeadZone = 0.5f;

    // As the game keeps a carried flag on its own keeper every frame, from where that player says they are.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), "Update")]
    private static void Follow()
    {
        if (carried.Count == 0 || CoopSession.Current == null)
            return;
        foreach (var pair in carried)
        {
            var at = RemoteKeeper.PositionOf(pair.Key);
            var data = pair.Value != null ? pair.Value.Data : null;
            if (at == null || data == null)
                continue;
            if ((at.Value - data.Position).sqrMagnitude > FlagDeadZone * FlagDeadZone)
                data.Position = at.Value;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped()
    {
        foreach (var slot in carried.Keys)
            RemoteKeeper.ShowBanner(slot, false, "");
        carried.Clear();
        looks.Clear();
    }

    // The game's own lookup throws when no look is active.
    private static string Look(BannerView banner)
    {
        if (!banner.IsVisible)
            return "";
        try { return banner.GetCurVariationId(); }
        catch (NullReferenceException) { return ""; }
    }
}
