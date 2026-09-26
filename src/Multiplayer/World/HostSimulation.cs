using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Only the host runs the shared world on its own: crafting and conveyors, delayed events and
// spawns, expiring and drifting drops, delayed quests, scheduled logic, zombies and townspeople.
// Joined players see its results; their own keeper's perks, and what their own play sets moving,
// still run.
[HarmonyPatch]
internal static class HostSimulation
{
    private static readonly Type[] Systems =
    {
        typeof(CraftSystem), typeof(DropSystem), typeof(ConveyorSystem), typeof(WgoDelayedEventsSystem),
        typeof(RiverDropSystem), typeof(QuestSystem), typeof(WgoDelayedSpawnSystem), typeof(GameLogicsSystem),
        typeof(ZombieSystem), typeof(WgoCustomDeathSystem), typeof(NPCLifeSimulator), typeof(ZombiePorterSystem)
    };

    private static IEnumerable<MethodBase> TargetMethods() =>
        Systems.Select(system => (MethodBase)AccessTools.Method(system, "CustomUpdate"));

    private static bool Prefix() => !CoopSession.IsGuest;
}
