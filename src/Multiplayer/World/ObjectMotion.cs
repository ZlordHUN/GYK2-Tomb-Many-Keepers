using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Objects that move, such as walking townspeople. The game moving an object shares where it is,
// which way it faces and whether it walks; everyone else follows smoothly with the native walk
// and idle animations.
[HarmonyPatch(typeof(WgoData))]
internal static class ObjectMotion
{
    private const float ShareInterval = 0.1f;
    private const float SnapDistance = 3f;
    // An unreliable stop can be lost; a walker without updates for this long stands still.
    private const float StopAfter = 0.6f;
    private static readonly AccessTools.FieldRef<MovementComponent, float> Speed =
        AccessTools.FieldRefAccess<MovementComponent, float>("speed");
    private static readonly AccessTools.FieldRef<MovementComponent, Action<float>> WalkStarted =
        AccessTools.FieldRefAccess<MovementComponent, Action<float>>("onStartAction");
    private static readonly AccessTools.FieldRef<MovementComponent, Action> WalkFinished =
        AccessTools.FieldRefAccess<MovementComponent, Action>("onFinishAction");
    private static readonly AccessTools.FieldRef<MovementComponent, Action<Vector2>> Turned =
        AccessTools.FieldRefAccess<MovementComponent, Action<Vector2>>("onDirectionChange");
    private static readonly HashSet<WgoData> moved = new HashSet<WgoData>();
    // Objects last shared as walking, so their stop is shared too.
    private static readonly HashSet<WgoData> walking = new HashSet<WgoData>();
    private static readonly List<WgoData> sending = new List<WgoData>();
    private static readonly Dictionary<Guid, Follower> followers = new Dictionary<Guid, Follower>();
    private static readonly List<Guid> arrived = new List<Guid>();
    private static readonly MemoryStream packet = new MemoryStream();
    private static readonly BinaryWriter writer = new BinaryWriter(packet);
    private static float nextShare;

    private sealed class Follower
    {
        internal Vector3 Target;
        internal Vector2 Direction;
        internal bool Walking;
        internal float Speed;
        internal float UpdatedAt;
        internal bool Animating;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.Position), MethodType.Setter)]
    private static void Moved(WgoData __instance) => Track(__instance);

    [HarmonyPostfix]
    [HarmonyPatch("HandleDirectionChanged")]
    private static void Faced(WgoData __instance) => Track(__instance);

    internal static void Share()
    {
        if (Time.unscaledTime < nextShare)
            return;
        nextShare = Time.unscaledTime + ShareInterval;
        sending.Clear();
        foreach (var data in moved)
        {
            if (WorldSync.Tracks(data))
                sending.Add(data);
        }
        foreach (var data in walking)
        {
            if (!moved.Contains(data) && WorldSync.Tracks(data) && !data.MovementComponent.IsMoving)
                sending.Add(data);
        }
        moved.Clear();
        if (sending.Count == 0)
            return;
        packet.SetLength(0);
        writer.Write(sending.Count);
        foreach (var data in sending)
        {
            var movement = data.MovementComponent;
            bool isWalking = movement.IsMoving;
            WorldSync.WriteId(writer, data.UniqueId.Guid);
            var position = data.Position;
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
            var direction = data.direction.Value;
            writer.Write(direction.x);
            writer.Write(direction.y);
            writer.Write(isWalking);
            writer.Write(Speed(movement));
            if (isWalking)
                walking.Add(data);
            else
                walking.Remove(data);
        }
        CoopSession.ShareMotion(packet.GetBuffer(), (int)packet.Length);
    }

    internal static void Apply(BinaryReader reader)
    {
        for (int count = reader.ReadInt32(); count > 0; count--)
        {
            var id = WorldSync.ReadId(reader);
            var target = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            bool isWalking = reader.ReadBoolean();
            float speed = reader.ReadSingle();
            if (WorldSync.FindObject(id) == null)
                continue;
            if (!followers.TryGetValue(id, out var follower))
                followers[id] = follower = new Follower();
            follower.Target = target;
            follower.Direction = direction;
            follower.Walking = isWalking;
            follower.Speed = speed;
            follower.UpdatedAt = Time.unscaledTime;
        }
    }

    // Moves every followed object a step toward where its game last saw it.
    internal static void Follow()
    {
        if (followers.Count == 0)
            return;
        arrived.Clear();
        float blend = 1f - Mathf.Exp(-12f * Time.deltaTime);
        foreach (var pair in followers)
        {
            var data = WorldSync.FindObject(pair.Key);
            if (data == null)
            {
                arrived.Add(pair.Key);
                continue;
            }
            var follower = pair.Value;
            bool isWalking = follower.Walking && Time.unscaledTime - follower.UpdatedAt < StopAfter;
            var current = data.Position;
            var next = (follower.Target - current).sqrMagnitude > SnapDistance * SnapDistance ? follower.Target
                : Vector3.Lerp(current, follower.Target, blend);
            WorldSync.Apply(() =>
            {
                data.Position = next;
                if (data.direction.Value != follower.Direction && follower.Direction != Vector2.zero)
                {
                    data.direction.Value = follower.Direction;
                    Turned(data.MovementComponent)?.Invoke(follower.Direction);
                }
            });
            if (isWalking != follower.Animating)
            {
                follower.Animating = isWalking;
                if (isWalking)
                    WalkStarted(data.MovementComponent)?.Invoke(follower.Speed);
                else
                    WalkFinished(data.MovementComponent)?.Invoke();
            }
            if (!isWalking && (follower.Target - next).sqrMagnitude < 0.0001f)
                arrived.Add(pair.Key);
        }
        foreach (var id in arrived)
            followers.Remove(id);
    }

    internal static void Clear()
    {
        moved.Clear();
        walking.Clear();
        followers.Clear();
    }

    private static void Track(WgoData data)
    {
        // Each player's wisp is their own, though its data is the host's.
        if (!WorldSync.Tracks(data) || IsWisp(data))
            return;
        moved.Add(data);
    }

    private static bool IsWisp(WgoData data)
    {
        foreach (var wisp in MainGame.WorldData.wispDataList)
        {
            if (wisp.linkedWgoId == data.UniqueId)
                return true;
        }
        return false;
    }
}
