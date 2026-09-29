using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// Enemies the host spawns become battle targets on the same line for joined players. The host's
// game moves them; a joined player's game never runs their AI, or it would fight twice.
[HarmonyPatch]
internal static class FightEnemies
{
    // An enemy's object can arrive a little before its view exists.
    private const float WaitForView = 5f;

    private static readonly List<(Guid id, int line, float until)> pending = new List<(Guid, int, float)>();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.RegisterTargetNonPersistent))]
    private static void Registered(FightingGameController __instance, ICombatEntity entity, int lineId)
    {
        if (!CoopSession.IsHosting || !WorldSync.Sharing || __instance.CurrentFightState != FightState.ActiveFight ||
            entity is not Wgo wgo || wgo.Data == null)
            return;
        var id = wgo.Data.UniqueId.Guid;
        WorldSync.Queue(WorldSync.Change.FightEnemy, id, writer =>
        {
            WorldSync.WriteId(writer, id);
            writer.Write(lineId);
        });
    }

    internal static void Apply(BinaryReader reader)
    {
        var id = WorldSync.ReadId(reader);
        int line = reader.ReadInt32();
        if (!TryRegister(id, line))
            pending.Add((id, line, Time.unscaledTime + WaitForView));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), "Update")]
    private static void RetryPending()
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var (id, line, until) = pending[i];
            if (TryRegister(id, line))
                pending.RemoveAt(i);
            else if (Time.unscaledTime > until)
            {
                Debug.LogWarning($"[Multiplayer] No view for the host's enemy {id} on line {line}");
                pending.RemoveAt(i);
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped() => pending.Clear();

    private static bool TryRegister(Guid id, int line)
    {
        var controller = LazySingleton<FightingGameController>.Instance;
        if (controller.CurrentFightState != FightState.ActiveFight)
            return true; // the battle is over, nothing to wait for
        var wgo = GameScene.GetWgoViewGlobal(new SGuid(id));
        if (wgo == null)
            return false;
        controller.RegisterTargetNonPersistent(wgo, line);
        wgo.IsActiveCombatant = GameBalance.Me.fighterWgoIdsCache.Contains(wgo.Id);
        return true;
    }
}
