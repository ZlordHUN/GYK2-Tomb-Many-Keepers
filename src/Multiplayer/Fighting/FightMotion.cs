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
    private static readonly FieldInfo FirstDamage = AccessTools.Field(typeof(HPComponent), nameof(HPComponent.OnFirstDamageDealt));

    // Where an enemy was when the host's last position came, where it is going, and how it looks.
    private sealed class Track
    {
        internal Vector3 from, to;
        internal float since, direction;
        internal int state, shown = -1;
    }

    private static readonly Dictionary<Guid, Track> tracks = new Dictionary<Guid, Track>();
    private static readonly List<(Guid id, Vector3 position, float direction, int state, int hp)> snapshot =
        new List<(Guid, Vector3, float, int, int)>();
    private static float nextSend;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), "Update")]
    private static void Update(FightingGameController __instance)
    {
        if (__instance.CurrentFightState == FightState.Disabled)
            return;
        if (CoopSession.IsHosting)
            Send(__instance);
        else if (CoopSession.IsGuest)
            Glide();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped() => tracks.Clear();

    // From preparing to the end, a joined player's fighters, allies too, only follow the host.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FightingAgent), nameof(FightingAgent.CustomUpdate))]
    private static bool HostRunsFighters() =>
        !CoopSession.IsGuest || LazySingleton<FightingGameController>.Instance.CurrentFightState == FightState.Disabled;

    private static void Send(FightingGameController controller)
    {
        if (!WorldSync.Sharing || Time.unscaledTime < nextSend)
            return;
        nextSend = Time.unscaledTime + Interval;
        snapshot.Clear();
        foreach (var agent in UnityEngine.Object.FindObjectsByType<FightingAgent>(FindObjectsSortMode.None))
        {
            var wgo = agent.Wgo;
            if (wgo == null || wgo.Data == null)
                continue;
            var animation = wgo.MainWgoPart?.AnimationComponent;
            var animator = animation?.Animator;
            float direction = animator != null ? animator.GetFloat(AnimationComponentBase.idDirectionAnimator) : 0f;
            int state = animation != null ? (int)animation.GetState() : (int)AnimationState.Idle;
            int hp = wgo.Data.HpComponent != null ? wgo.Data.HpComponent.Hp : -1;
            snapshot.Add((wgo.Data.UniqueId.Guid, wgo.Data.Position, direction, state, hp));
        }
        var processor = Processor(controller);
        float progress = processor.CurrentProgress, normalized = processor.ProgressNormalized;
        var enemies = snapshot.ToArray();
        WorldSync.Queue(WorldSync.Change.FightMotion, Guid.Empty, writer =>
        {
            writer.Write(progress);
            writer.Write(normalized);
            writer.Write(enemies.Length);
            foreach (var (id, position, direction, state, hp) in enemies)
            {
                WorldSync.WriteId(writer, id);
                writer.Write(position.x);
                writer.Write(position.y);
                writer.Write(position.z);
                writer.Write(direction);
                writer.Write((short)state);
                writer.Write(hp);
            }
        });
    }

    internal static void Apply(BinaryReader reader)
    {
        float progress = reader.ReadSingle(), normalized = reader.ReadSingle();
        var controller = LazySingleton<FightingGameController>.Instance;
        if (controller.CurrentFightState == FightState.Disabled)
            return;
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var id = WorldSync.ReadId(reader);
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float direction = reader.ReadSingle();
            int state = reader.ReadInt16();
            int hp = reader.ReadInt32();
            if (!tracks.TryGetValue(id, out var track))
                tracks[id] = track = new Track { to = position };
            // The next leg starts where the enemy is shown now, so it never jumps back.
            var wgo = GameScene.GetWgoViewGlobal(new SGuid(id));
            if (wgo != null && wgo.Data != null)
                ShowHp(wgo.Data.HpComponent, hp);
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

    // Health as the host has it; the first loss shows the bar. Death is the host's to send.
    private static void ShowHp(HPComponent component, int hp)
    {
        if (component == null || hp <= 0 || component.Hp == hp)
            return;
        component.SetCustomHpValue(hp, overrideMaxHpValue: false);
        if (component.wasDamagedAtLeastOnce || hp >= component.MaxHpValue)
            return;
        component.wasDamagedAtLeastOnce = true;
        (FirstDamage?.GetValue(component) as Action)?.Invoke();
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
            // Only when the host's changes: an attack that ends here goes back to idle by itself.
            if (track.shown != track.state)
            {
                animation.SetState((AnimationState)track.state);
                track.shown = track.state;
            }
            animation.Animator?.SetFloat(AnimationComponentBase.idDirectionAnimator, track.direction);
        }
    }
}
