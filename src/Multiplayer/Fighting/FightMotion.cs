using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// The host's enemy positions and battle clock, a few times a second; joined players glide their
// enemies there and move the timeline with it.
[HarmonyPatch]
internal static class FightMotion
{
    private const float Interval = 0.1f;
    private const float SnapDistance = 4f;
    private const float Moving = 0.02f;

    private static readonly AccessTools.FieldRef<FightingGameController, FightingLevelPresetProcessor> Processor =
        AccessTools.FieldRefAccess<FightingGameController, FightingLevelPresetProcessor>("presetProcessor");
    private static readonly MethodInfo SetProgress =
        AccessTools.PropertySetter(typeof(FightingLevelPresetProcessor), nameof(FightingLevelPresetProcessor.CurrentProgress));
    private static readonly FieldInfo ProgressChanged =
        AccessTools.Field(typeof(FightingLevelPresetProcessor), nameof(FightingLevelPresetProcessor.OnProgressChanged));

    private static readonly Dictionary<Guid, Vector3> targets = new Dictionary<Guid, Vector3>();
    private static readonly List<(Guid id, Vector3 position)> snapshot = new List<(Guid, Vector3)>();
    private static float nextSend;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), "Update")]
    private static void Update(FightingGameController __instance)
    {
        if (__instance.CurrentFightState != FightState.ActiveFight)
            return;
        if (CoopSession.IsHosting)
            Send(__instance);
        else if (CoopSession.IsGuest)
            Glide();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped() => targets.Clear();

    private static void Send(FightingGameController controller)
    {
        if (!WorldSync.Sharing || Time.unscaledTime < nextSend)
            return;
        nextSend = Time.unscaledTime + Interval;
        snapshot.Clear();
        foreach (var entity in controller.TargetsDatabase.GetTargetsByTeam(LazyConsts.Fighting.TeamType.WildZombie))
            if (entity is Wgo wgo && wgo.Data != null)
                snapshot.Add((wgo.Data.UniqueId.Guid, wgo.Data.Position));
        var processor = Processor(controller);
        float progress = processor.CurrentProgress, normalized = processor.ProgressNormalized;
        var enemies = snapshot.ToArray();
        WorldSync.Queue(WorldSync.Change.FightMotion, Guid.Empty, writer =>
        {
            writer.Write(progress);
            writer.Write(normalized);
            writer.Write(enemies.Length);
            foreach (var (id, position) in enemies)
            {
                WorldSync.WriteId(writer, id);
                writer.Write(position.x);
                writer.Write(position.y);
                writer.Write(position.z);
            }
        });
    }

    internal static void Apply(BinaryReader reader)
    {
        float progress = reader.ReadSingle(), normalized = reader.ReadSingle();
        var controller = LazySingleton<FightingGameController>.Instance;
        if (controller.CurrentFightState != FightState.ActiveFight)
            return;
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            targets[WorldSync.ReadId(reader)] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        // The timer reads the progress; the bar waits for the event.
        var processor = Processor(controller);
        SetProgress?.Invoke(processor, new object[] { progress });
        (ProgressChanged?.GetValue(processor) as Action<float>)?.Invoke(normalized);
    }

    private static void Glide()
    {
        float blend = 1f - Mathf.Exp(-12f * Time.deltaTime);
        foreach (var pair in targets)
        {
            var wgo = GameScene.GetWgoViewGlobal(new SGuid(pair.Key));
            if (wgo == null || wgo.Data == null || !FightingTargetsDatabase.IsCombatEntityAlive(wgo))
                continue;
            var current = wgo.Data.Position;
            var step = pair.Value - current;
            var next = step.sqrMagnitude > SnapDistance * SnapDistance ? pair.Value : Vector3.Lerp(current, pair.Value, blend);
            WorldSync.Apply(() => wgo.Data.Position = next);
            wgo.transform.position = next;
            var animation = wgo.MainWgoPart?.AnimationComponent;
            if (animation == null)
                continue;
            if (step.sqrMagnitude > Moving * Moving)
            {
                animation.SetState(AnimationState.Walk);
                animation.SetDirection(new Vector2(step.x, step.z));
            }
            else
                animation.SetState(AnimationState.Idle);
        }
    }
}
