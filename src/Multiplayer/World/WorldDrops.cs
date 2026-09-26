using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Items lying in the world. Anyone's drops, pickups and partial pickups are shared. Drops fall
// and merge by physics in every game, so the host decides where they settle and which merge.
[HarmonyPatch]
internal static class WorldDrops
{
    private const float SettleInterval = 0.5f;
    private const float SettleDistance = 0.05f;
    private static readonly AccessTools.FieldRef<DropView, DropData> ViewData =
        AccessTools.FieldRefAccess<DropView, DropData>("data");
    private static readonly Action<DropView, Vector3> PlaceView =
        AccessTools.MethodDelegate<Action<DropView, Vector3>>(AccessTools.Method(typeof(DropView), "ApplySyncedWorldPosition"));
    // Where the host last saw and last shared each drop in its loaded scenes.
    private static readonly Dictionary<Guid, (Vector3 seen, Vector3 shared)> settling = new Dictionary<Guid, (Vector3, Vector3)>();
    private static readonly List<Guid> gone = new List<Guid>();
    private static float nextSettle;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSceneData), nameof(GameSceneData.AddDrop), typeof(DropData))]
    private static void Added(GameSceneData __instance, DropData drop)
    {
        if (!WorldSync.Sharing || drop?.Item == null)
            return;
        string scene = __instance.id;
        var id = drop.UniqueId.Guid;
        settling[id] = (drop.Position, drop.Position);
        WorldSync.QueueAdded(WorldSync.Change.AddDrop, id, writer =>
        {
            writer.Write(scene);
            WorldSync.WriteData(writer, drop);
        });
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameSceneData), nameof(GameSceneData.RemoveDrop))]
    private static void Removing(GameSceneData __instance, DropData drop, out bool __state) =>
        __state = WorldSync.Sharing && drop?.Item != null && Find(__instance, drop.UniqueId.Guid) == drop;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSceneData), nameof(GameSceneData.RemoveDrop))]
    private static void Removed(GameSceneData __instance, DropData drop, bool __state)
    {
        if (!__state)
            return;
        string scene = __instance.id;
        var id = drop.UniqueId.Guid;
        settling.Remove(id);
        WorldSync.QueueRemoved(WorldSync.Change.RemoveDrop, id, writer =>
        {
            writer.Write(scene);
            WorldSync.WriteId(writer, id);
        });
    }

    // Merges and partial pickups change how many items a drop holds.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DropData), nameof(DropData.NotifyCountChanged))]
    private static void Counted(DropData __instance)
    {
        if (!WorldSync.Sharing || __instance.Item == null || __instance.IsRemoving || WorldSync.IsNew(__instance.UniqueId.Guid))
            return;
        string scene = __instance.WorldId;
        var id = __instance.UniqueId.Guid;
        int count = __instance.Count;
        WorldSync.Queue(WorldSync.Change.DropCount, id, writer =>
        {
            writer.Write(scene);
            WorldSync.WriteId(writer, id);
            writer.Write(count);
        });
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DropView), "Merge")]
    private static bool MergeOnHost() => !CoopSession.IsGuest;

    // The host shares where its drops came to rest.
    internal static void Settle()
    {
        if (!CoopSession.Current.IsHost || Time.unscaledTime < nextSettle)
            return;
        nextSettle = Time.unscaledTime + SettleInterval;
        gone.Clear();
        gone.AddRange(settling.Keys);
        foreach (string sceneId in MainGame.WorldData.LoadedScenes)
        {
            var scene = MainGame.WorldData.GetGameSceneDataById(sceneId);
            if (scene == null)
                continue;
            foreach (var drop in scene.droppedItems)
            {
                if (drop.Item == null || drop.IsRemoving)
                    continue;
                var id = drop.UniqueId.Guid;
                gone.Remove(id);
                var position = drop.Position;
                if (!settling.TryGetValue(id, out var state))
                {
                    settling[id] = (position, position);
                    continue;
                }
                bool resting = (position - state.seen).sqrMagnitude < 0.0001f;
                if (resting && (position - state.shared).sqrMagnitude > SettleDistance * SettleDistance && !WorldSync.IsNew(id))
                {
                    string owner = scene.id;
                    WorldSync.Queue(WorldSync.Change.DropPosition, id, writer =>
                    {
                        writer.Write(owner);
                        WorldSync.WriteId(writer, id);
                        writer.Write(position.x);
                        writer.Write(position.y);
                        writer.Write(position.z);
                    });
                    state.shared = position;
                }
                state.seen = position;
                settling[id] = state;
            }
        }
        foreach (var id in gone)
            settling.Remove(id);
    }

    internal static void Clear() => settling.Clear();

    internal static void ApplyAdd(BinaryReader reader)
    {
        var scene = MainGame.WorldData.GetGameSceneDataById(reader.ReadString());
        var drop = WorldSync.ReadData<DropData>(reader);
        if (scene == null || drop?.Item == null || Find(scene, drop.UniqueId.Guid) != null)
            return;
        // A drop still waiting for its scene here is the same drop.
        scene.queuedDrops.RemoveAll(queued => queued.Item != null && queued.UniqueId == drop.UniqueId);
        drop.WorldId = scene.id;
        scene.AddDrop(drop);
    }

    internal static void ApplyRemove(BinaryReader reader)
    {
        var scene = MainGame.WorldData.GetGameSceneDataById(reader.ReadString());
        var drop = scene == null ? null : Find(scene, WorldSync.ReadId(reader));
        if (drop != null)
            scene.RemoveDrop(drop);
    }

    internal static void ApplyCount(BinaryReader reader)
    {
        var scene = MainGame.WorldData.GetGameSceneDataById(reader.ReadString());
        var drop = scene == null ? null : Find(scene, WorldSync.ReadId(reader));
        int count = reader.ReadInt32();
        if (drop == null || drop.Count == count)
            return;
        drop.Item.Count = count;
        drop.NotifyCountChanged();
    }

    internal static void ApplyPosition(BinaryReader reader)
    {
        var scene = MainGame.WorldData.GetGameSceneDataById(reader.ReadString());
        var drop = scene == null ? null : Find(scene, WorldSync.ReadId(reader));
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (drop == null)
            return;
        var view = View(drop);
        if (view != null)
            PlaceView(view, position);
        else
            drop.Position = position;
    }

    // A claimed drop as the host left it.
    internal static void SetCount(string sceneId, Guid id, int count)
    {
        var scene = MainGame.WorldData.GetGameSceneDataById(sceneId);
        var drop = scene == null ? null : Find(scene, id);
        if (drop == null || drop.Count == count)
            return;
        if (count == 0)
            scene.RemoveDrop(drop);
        else
        {
            drop.Item.Count = count;
            drop.NotifyCountChanged();
        }
    }

    internal static DropData Find(GameSceneData scene, Guid id)
    {
        foreach (var drop in scene.droppedItems)
        {
            if (drop.Item != null && drop.UniqueId.Guid == id)
                return drop;
        }
        foreach (var drop in scene.queuedDrops)
        {
            if (drop.Item != null && drop.UniqueId.Guid == id)
                return drop;
        }
        return null;
    }

    private static DropView View(DropData drop)
    {
        foreach (var scene in LazySingleton<GameSceneManager>.Instance.LoadedGameScenes)
        {
            var view = scene.GetDropView(drop.Item);
            if (view != null && ViewData(view) == drop)
                return view;
        }
        return null;
    }
}
