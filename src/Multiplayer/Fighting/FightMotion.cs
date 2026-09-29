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

    private static readonly AccessTools.FieldRef<FightingGameController, FightingLevelPresetProcessor> Processor =
        AccessTools.FieldRefAccess<FightingGameController, FightingLevelPresetProcessor>("presetProcessor");
    private static readonly MethodInfo SetProgress =
        AccessTools.PropertySetter(typeof(FightingLevelPresetProcessor), nameof(FightingLevelPresetProcessor.CurrentProgress));
    private static readonly FieldInfo ProgressChanged =
        AccessTools.Field(typeof(FightingLevelPresetProcessor), nameof(FightingLevelPresetProcessor.OnProgressChanged));

    // Where an enemy was when the host's last position came, where it is going, and how it looks.
    private sealed class Track
    {
        internal Vector3 from, to;
        internal float since, direction;
        internal int state;
    }

    private static readonly Dictionary<Guid, Track> tracks = new Dictionary<Guid, Track>();
    private static readonly List<(Guid id, Vector3 position, float direction, int state)> snapshot =
        new List<(Guid, Vector3, float, int)>();
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
    private static void Stopped() => tracks.Clear();

    private static void Send(FightingGameController controller)
    {
        if (!WorldSync.Sharing || Time.unscaledTime < nextSend)
            return;
        nextSend = Time.unscaledTime + Interval;
        snapshot.Clear();
        foreach (var entity in controller.TargetsDatabase.GetTargetsByTeam(LazyConsts.Fighting.TeamType.WildZombie))
        {
            if (entity is not Wgo wgo || wgo.Data == null)
                continue;
            var animation = wgo.MainWgoPart?.AnimationComponent;
            var animator = animation?.Animator;
            float direction = animator != null ? animator.GetFloat(AnimationComponentBase.idDirectionAnimator) : 0f;
            int state = animation != null ? (int)animation.GetState() : (int)AnimationState.Idle;
            snapshot.Add((wgo.Data.UniqueId.Guid, wgo.Data.Position, direction, state));
        }
        var processor = Processor(controller);
        float progress = processor.CurrentProgress, normalized = processor.ProgressNormalized;
        var enemies = snapshot.ToArray();
        WorldSync.Queue(WorldSync.Change.FightMotion, Guid.Empty, writer =>
        {
            writer.Write(progress);
            writer.Write(normalized);
            writer.Write(enemies.Length);
            foreach (var (id, position, direction, state) in enemies)
            {
                WorldSync.WriteId(writer, id);
                writer.Write(position.x);
                writer.Write(position.y);
                writer.Write(position.z);
                writer.Write(direction);
                writer.Write((short)state);
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
        {
            var id = WorldSync.ReadId(reader);
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float direction = reader.ReadSingle();
            int state = reader.ReadInt16();
            if (!tracks.TryGetValue(id, out var track))
                tracks[id] = track = new Track { to = position };
            // The next leg starts where the enemy is shown now, so it never jumps back.
            var wgo = GameScene.GetWgoViewGlobal(new SGuid(id));
            track.from = wgo != null && wgo.Data != null ? wgo.Data.Position : track.to;
            track.to = position;
            track.since = Time.unscaledTime;
            track.direction = direction;
            track.state = state;
        }
        // The timer reads the progress; the bar waits for the event.
        var processor = Processor(controller);
        SetProgress?.Invoke(processor, new object[] { progress });
        (ProgressChanged?.GetValue(processor) as Action<float>)?.Invoke(normalized);
    }

    private static void Glide()
    {
        foreach (var pair in tracks)
        {
            var wgo = GameScene.GetWgoViewGlobal(new SGuid(pair.Key));
            if (wgo == null || wgo.Data == null || !FightingTargetsDatabase.IsCombatEntityAlive(wgo))
                continue;
            var track = pair.Value;
            // An even walk over one send interval; a long way off is a teleport, not a walk.
            float t = Mathf.Clamp01((Time.unscaledTime - track.since) / Interval);
            var next = (track.to - track.from).sqrMagnitude > SnapDistance * SnapDistance
                ? track.to
                : Vector3.Lerp(track.from, track.to, t);
            WorldSync.Apply(() => wgo.Data.Position = next);
            wgo.transform.position = next;
            var animation = wgo.MainWgoPart?.AnimationComponent;
            if (animation == null)
                continue;
            if ((int)animation.GetState() != track.state)
                animation.SetState((AnimationState)track.state);
            animation.Animator?.SetFloat(AnimationComponentBase.idDirectionAnimator, track.direction);
        }
    }
}
