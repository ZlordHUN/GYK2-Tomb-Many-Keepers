using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// Swings, shots and spits a fighter starts with a trigger or its attack layer, which the states
// sent with its position miss.
[HarmonyPatch]
internal static class FightAnimations
{
    private enum Kind : byte
    {
        Trigger,
        Layer
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimationComponentBase), nameof(AnimationComponentBase.SetTrigger), typeof(string))]
    private static void Trigger(AnimationComponentBase __instance, string trigger) =>
        Share(__instance, Kind.Trigger, Animator.StringToHash(trigger), 0f);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimationComponentBase), nameof(AnimationComponentBase.SetTrigger), typeof(int))]
    private static void TriggerHash(AnimationComponentBase __instance, int trigger) =>
        Share(__instance, Kind.Trigger, trigger, 0f);

    // Some code sets a layer every frame; only a change is worth sending.
    private static readonly Dictionary<(AnimationComponentBase, int), float> layers =
        new Dictionary<(AnimationComponentBase, int), float>();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimationComponentBase), nameof(AnimationComponentBase.SetLayerWeight))]
    private static void Layer(AnimationComponentBase __instance, int layerIndex, float weight)
    {
        var key = (__instance, layerIndex);
        if (layers.TryGetValue(key, out float last) && Mathf.Approximately(last, weight))
            return;
        layers[key] = weight;
        Share(__instance, Kind.Layer, layerIndex, weight);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped() => layers.Clear();

    private static void Share(AnimationComponentBase animation, Kind kind, int value, float weight)
    {
        if (!CoopSession.IsHosting || !WorldSync.Sharing ||
            LazySingleton<FightingGameController>.Instance.CurrentFightState == FightState.Disabled)
            return;
        var agent = animation.GetComponentInParent<FightingAgent>();
        var data = agent != null && agent.Wgo != null ? agent.Wgo.Data : null;
        if (data == null)
            return;
        var id = data.UniqueId.Guid;
        WorldSync.Queue(WorldSync.Change.FightAnimation, id, writer =>
        {
            WorldSync.WriteId(writer, id);
            writer.Write((byte)kind);
            writer.Write(value);
            writer.Write(weight);
        });
    }

    internal static void Apply(BinaryReader reader)
    {
        var wgo = GameScene.GetWgoViewGlobal(new SGuid(WorldSync.ReadId(reader)));
        var kind = (Kind)reader.ReadByte();
        int value = reader.ReadInt32();
        float weight = reader.ReadSingle();
        var animation = wgo != null ? wgo.MainWgoPart?.AnimationComponent : null;
        if (animation == null)
            return;
        if (kind == Kind.Trigger)
            animation.SetTrigger(value);
        else
            animation.SetLayerWeight(value, weight);
    }
}
