using System;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// A hit's flash, splash and blood on the ground, in every game: each player's hits land in their
// own game, and the others only see the damage without this.
[HarmonyPatch]
internal static class FightHits
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DamageEffectComponent), nameof(DamageEffectComponent.TryPlayEffect),
        typeof(SGuid), typeof(Vector3), typeof(Vector3), typeof(DamageEffectSettings))]
    private static void Played(SGuid guid, Vector3 position, Vector3 attackDirection)
    {
        if (!WorldSync.Sharing || SGuid.IsNullOrEmpty(guid) ||
            LazySingleton<FightingGameController>.Instance.CurrentFightState == FightState.Disabled)
            return;
        var id = guid.Guid;
        WorldSync.Queue(WorldSync.Change.FightHit, id, writer =>
        {
            WorldSync.WriteId(writer, id);
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
            writer.Write(attackDirection.x);
            writer.Write(attackDirection.z);
        });
    }

    internal static void Apply(BinaryReader reader)
    {
        var guid = new SGuid(WorldSync.ReadId(reader));
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var direction = new Vector3(reader.ReadSingle(), 0f, reader.ReadSingle());
        DamageEffectComponent.TryPlayEffect(guid, position, direction);
    }
}
