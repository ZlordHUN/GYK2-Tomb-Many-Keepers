using System;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// The host runs the battle; joined players enter and leave it with the host and only show it.
[HarmonyPatch]
internal static class FightSync
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Play), new Type[0])]
    private static void Started(FightingGameController __instance)
    {
        if (!CoopSession.IsHosting || !WorldSync.Sharing || __instance.CurrentFightState != FightState.ActiveFight)
            return;
        string id = __instance.CurrentLevelId;
        WorldSync.Queue(WorldSync.Change.FightStart, Guid.Empty, writer => writer.Write(id));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped()
    {
        if (CoopSession.IsHosting && WorldSync.Sharing)
            WorldSync.Queue(WorldSync.Change.FightStop, Guid.Empty, _ => { });
    }

    // A joined player's game never runs the phases, so it spawns nothing of its own.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingLevelPresetProcessor), nameof(FightingLevelPresetProcessor.StartPreset))]
    private static bool HostRunsPhases() => !CoopSession.IsGuest;

    // The host's allies arrive as world objects; spawning them again would double them.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingLevel), nameof(FightingLevel.SpawnAllies))]
    private static bool HostSpawnsAllies() => !CoopSession.IsGuest;

    internal static void ApplyStart(BinaryReader reader)
    {
        string id = reader.ReadString();
        var controller = LazySingleton<FightingGameController>.Instance;
        Debug.Log($"[Multiplayer] The host started the battle {id}");
        controller.Play(id);
    }

    internal static void ApplyStop(BinaryReader reader)
    {
        var controller = LazySingleton<FightingGameController>.Instance;
        if (controller.CurrentFightState == FightState.Disabled)
            return;
        Debug.Log("[Multiplayer] The host ended the battle");
        controller.Stop();
    }
}
