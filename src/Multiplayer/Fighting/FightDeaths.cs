using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// A death in the host's battle plays for joined players too: effects, blood and the fall. What the
// death does to the world, like loot, stays with the host, which shares its outcome anyway.
[HarmonyPatch]
internal static class FightDeaths
{
    // Set when the agent's AI starts, which never happens in a joined player's game.
    private static readonly AccessTools.FieldRef<FightingAgent, Wgo> AgentWgo =
        AccessTools.FieldRefAccess<FightingAgent, Wgo>("wgo");

    private static readonly HashSet<Guid> sent = new HashSet<Guid>();
    private static bool showing;

    // Before the death runs: it removes the enemy, and that removal must reach others second.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingAgent), nameof(FightingAgent.PlayDying))]
    private static void Died(FightingAgent __instance)
    {
        var data = __instance.Wgo != null ? __instance.Wgo.Data : null;
        if (!CoopSession.IsHosting || !WorldSync.Sharing || data == null || !sent.Add(data.UniqueId.Guid))
            return;
        var id = data.UniqueId.Guid;
        WorldSync.Queue(WorldSync.Change.FightDeath, id, writer => WorldSync.WriteId(writer, id));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped() => sent.Clear();

    // Only the look of a death while a joined player shows the host's.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WgoData), nameof(WgoData.HandleDeath))]
    private static bool HandleDeath() => !showing;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WgoData), nameof(WgoData.TriggerCustomDeathMoment))]
    private static bool DeathMoment() => !showing;

    internal static void Apply(BinaryReader reader)
    {
        var id = WorldSync.ReadId(reader);
        var wgo = GameScene.GetWgoViewGlobal(new SGuid(id));
        var part = wgo != null ? wgo.MainWgoPart : null;
        if (part == null || !(part.TryGetComponent<FightingAgent>(out var agent) ||
                              (agent = wgo.GetComponentInChildren<FightingAgent>(includeInactive: true)) != null))
        {
            Debug.LogWarning($"[Multiplayer] Could not show the death of {id}: {(wgo == null ? "no view" : "no fighting agent")}");
            return;
        }
        if (AgentWgo(agent) == null)
            AgentWgo(agent) = wgo;
        showing = true;
        try
        {
            agent.PlayDying();
        }
        finally
        {
            showing = false;
        }
        wgo.MainWgoPart.AnimationComponent?.SetState(AnimationState.Death);
    }
}
