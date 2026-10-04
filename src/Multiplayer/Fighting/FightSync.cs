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
    private enum Request : byte
    {
        Prepare,
        Start
    }

    // Set while a joined player's game follows the host into a battle.
    private static int fromHost;
    // A joined player's wish to prepare or start, which the host carries out on its next frame.
    private static (Request kind, string id)? requested;

    // A joined player's own game never begins a battle, say by taking the flag: it asks the host.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.StartPreFight))]
    private static bool AskToPrepare(string levelId) => HostBegins(Request.Prepare, levelId);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Play), new Type[0])]
    private static bool AskToStart(FightingGameController __instance) =>
        HostBegins(Request.Start, __instance.CurrentLevel != null ? __instance.CurrentLevelId : string.Empty);

    private static bool HostBegins(Request kind, string id)
    {
        if (!CoopSession.IsGuest || fromHost > 0)
            return true;
        if (WorldSync.Sharing)
        {
            Debug.Log($"[Multiplayer] Asking the host to {kind.ToString().ToLowerInvariant()} the battle {id}");
            WorldSync.Queue(WorldSync.Change.FightRequest, Guid.Empty, writer =>
            {
                writer.Write((byte)kind);
                writer.Write(id ?? string.Empty);
            });
        }
        return false;
    }

    internal static void ApplyRequest(BinaryReader reader)
    {
        var kind = (Request)reader.ReadByte();
        string id = reader.ReadString();
        if (CoopSession.IsHosting)
            requested = (kind, id);
    }

    // Outside the applied stream, so what the host begins is shared as its own.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), "Update")]
    private static void CarryOutRequest(FightingGameController __instance)
    {
        if (requested == null || !CoopSession.IsHosting)
            return;
        var (kind, id) = requested.Value;
        requested = null;
        Debug.Log($"[Multiplayer] A joined player asked to {kind.ToString().ToLowerInvariant()} the battle {id}");
        if (kind == Request.Prepare && __instance.CurrentFightState == FightState.Disabled && !string.IsNullOrEmpty(id))
            __instance.StartPreFight(id);
        else if (kind == Request.Start && __instance.CurrentFightState == FightState.InPreFight)
            __instance.Play();
    }

    // Preparing: lines and the countdown show before the first wave.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.StartPreFight))]
    private static void Preparing(FightingGameController __instance, string levelId)
    {
        if (!CoopSession.IsHosting || !WorldSync.Sharing || __instance.CurrentFightState != FightState.InPreFight)
            return;
        WorldSync.Queue(WorldSync.Change.FightPrepare, Guid.Empty, writer => writer.Write(levelId));
    }

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
    private static void Stopped(bool hasCustomAfterFightPos, bool stopAsWon)
    {
        // How the host ends it decides where everyone goes next, such as into the scene that follows.
        if (CoopSession.IsHosting && WorldSync.Sharing)
            WorldSync.Queue(WorldSync.Change.FightStop, Guid.Empty, writer =>
            {
                writer.Write(hasCustomAfterFightPos);
                writer.Write(stopAsWon);
            });
    }

    // A joined player's game never runs the phases, so it spawns nothing of its own.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingLevelPresetProcessor), nameof(FightingLevelPresetProcessor.StartPreset))]
    private static bool HostRunsPhases() => !CoopSession.IsGuest;

    // A breach hands the enemies to the line's group, and the group orders them about: the host's
    // to do. A joined player's enemies have no AI to take the orders.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingLine), "HandleLineBreachedByEnemies")]
    private static bool HostHandlesBreach() => !CoopSession.IsGuest;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AgentsGroupBehaviourController), nameof(AgentsGroupBehaviourController.CustomUpdate))]
    private static bool HostOrdersGroups() =>
        !CoopSession.IsGuest || LazySingleton<FightingGameController>.Instance.CurrentFightState == FightState.Disabled;

    // The host's allies arrive as world objects; spawning them again would double them.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingLevel), nameof(FightingLevel.SpawnAllies))]
    private static bool HostSpawnsAllies() => !CoopSession.IsGuest;

    internal static void ApplyPrepare(BinaryReader reader)
    {
        string id = reader.ReadString();
        Debug.Log($"[Multiplayer] The host is preparing the battle {id}");
        fromHost++;
        try
        {
            LazySingleton<FightingGameController>.Instance.StartPreFight(id);
        }
        finally
        {
            fromHost--;
        }
    }

    internal static void ApplyStart(BinaryReader reader)
    {
        string id = reader.ReadString();
        var controller = LazySingleton<FightingGameController>.Instance;
        Debug.Log($"[Multiplayer] The host started the battle {id}");
        fromHost++;
        try
        {
            controller.Play(id);
        }
        finally
        {
            fromHost--;
        }
    }

    internal static void ApplyStop(BinaryReader reader)
    {
        var controller = LazySingleton<FightingGameController>.Instance;
        if (controller.CurrentFightState == FightState.Disabled)
            return;
        bool customPosition = reader.ReadBoolean(), won = reader.ReadBoolean();
        Debug.Log("[Multiplayer] The host ended the battle");
        controller.Stop(customPosition, won);
    }
}
