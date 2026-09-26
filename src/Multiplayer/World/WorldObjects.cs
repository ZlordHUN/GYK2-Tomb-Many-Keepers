using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Objects and static objects appearing in or leaving any scene. An object travels whole when it
// appears and by its id when it goes; replacing or moving an object does both.
[HarmonyPatch(typeof(GameSceneData))]
internal static class WorldObjects
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSceneData.AddWgoData), typeof(WgoData), typeof(bool))]
    private static void Added(GameSceneData __instance, WgoData wgoData) => Share(__instance, wgoData);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSceneData.AddWgoData), typeof(string), typeof(Vector3), typeof(string), typeof(bool))]
    private static void Created(GameSceneData __instance, WgoData __result) => Share(__instance, __result);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameSceneData.RemoveWgoData))]
    private static void Removing(WgoData wgoData, out bool __state) =>
        __state = WorldSync.Sharing && !wgoData.isTempObject && WorldSync.FindObject(wgoData.UniqueId.Guid) == wgoData;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSceneData.RemoveWgoData))]
    private static void Removed(WgoData wgoData, bool __state)
    {
        if (!__state)
            return;
        var id = wgoData.UniqueId.Guid;
        WorldSync.QueueRemoved(WorldSync.Change.RemoveObject, id, writer => WorldSync.WriteId(writer, id));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSceneData.AddWsoData), typeof(WsoData))]
    private static void AddedStatic(GameSceneData __instance, WsoData wsoData) => ShareStatic(__instance, wsoData);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSceneData.AddWsoData), typeof(string), typeof(Vector3))]
    private static void CreatedStatic(GameSceneData __instance, WsoData __result) => ShareStatic(__instance, __result);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameSceneData.RemoveWsoData))]
    private static void RemovingStatic(WsoData wsoData, out bool __state) =>
        __state = WorldSync.Sharing && FindStatic(wsoData.UniqueId.Guid) == wsoData;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameSceneData.RemoveWsoData))]
    private static void RemovedStatic(WsoData wsoData, bool __state)
    {
        if (!__state)
            return;
        var id = wsoData.UniqueId.Guid;
        WorldSync.QueueRemoved(WorldSync.Change.RemoveStatic, id, writer => WorldSync.WriteId(writer, id));
    }

    internal static void ApplyAdd(BinaryReader reader)
    {
        var scene = MainGame.WorldData.GetGameSceneDataById(reader.ReadString());
        var data = WorldSync.ReadData<WgoData>(reader);
        if (scene == null || data == null)
            return;
        // The same object can arrive again, such as one captured with a new player's game.
        var existing = WorldSync.FindObject(data.UniqueId.Guid);
        if (existing != null)
            MainGame.WorldData.GetGameSceneDataById(existing.WorldId)?.RemoveWgoData(existing, clearCraftComponent: false);
        data.PrepareForGame();
        scene.AddWgoData(data, recheckVisibilityOnSpawn: true);
    }

    internal static void ApplyRemove(BinaryReader reader)
    {
        var data = WorldSync.FindObject(WorldSync.ReadId(reader));
        if (data != null)
            MainGame.WorldData.GetGameSceneDataById(data.WorldId)?.RemoveWgoData(data);
    }

    internal static void ApplyAddStatic(BinaryReader reader)
    {
        var scene = MainGame.WorldData.GetGameSceneDataById(reader.ReadString());
        var data = WorldSync.ReadData<WsoData>(reader);
        if (scene == null || data == null || FindStatic(data.UniqueId.Guid) != null)
            return;
        data.PrepareForGame();
        scene.AddWsoData(data);
    }

    internal static void ApplyRemoveStatic(BinaryReader reader)
    {
        var data = FindStatic(WorldSync.ReadId(reader));
        if (data != null)
            MainGame.WorldData.GetGameSceneDataById(data.WorldId)?.RemoveWsoData(data);
    }

    private static void Share(GameSceneData scene, WgoData data)
    {
        if (!WorldSync.Sharing || data == null || data.isTempObject)
            return;
        string sceneId = scene.id;
        WorldSync.QueueAdded(WorldSync.Change.AddObject, data.UniqueId.Guid, writer =>
        {
            writer.Write(sceneId);
            WorldSync.WriteData(writer, data);
        });
    }

    private static void ShareStatic(GameSceneData scene, WsoData data)
    {
        if (!WorldSync.Sharing || data == null)
            return;
        string sceneId = scene.id;
        WorldSync.QueueAdded(WorldSync.Change.AddStatic, data.UniqueId.Guid, writer =>
        {
            writer.Write(sceneId);
            WorldSync.WriteData(writer, data);
        });
    }

    private static WsoData FindStatic(Guid id) =>
        MainGame.WorldData.Cache.wsoDataByUidCache.TryGetValue(id, out var data) ? data : null;
}
