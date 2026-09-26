using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GYK2.TombManyKeepers.Multiplayer.Progression;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using Sirenix.Serialization;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Every player's changes to the shared world and the campaign's story travel as one ordered
// stream. The game's changes are queued as it makes them and sent once per frame; the host applies
// each player's changes and passes them on. Applying a change runs the same native code, and what
// that causes is not shared again.
internal static class WorldSync
{
    internal enum Change : byte
    {
        AddObject,
        RemoveObject,
        AddStatic,
        RemoveStatic,
        AddDrop,
        RemoveDrop,
        DropCount,
        DropPosition,
        Hidden,
        Interactable,
        ObjectRes,
        WorldRes,
        Events,
        AddPart,
        RemovePart,
        ClearParts,
        PartState,
        AnimationTrigger,
        AnimationState,
        AnimationLayer,
        CustomTrigger,
        Hp,
        ToolTick,
        Quest,
        Knowledge,
        Reputation
    }

    // Reliable messages hold at most 512 KiB.
    private const int MaxBatch = 256 * 1024;
    private static readonly List<(Change change, Guid target, Action<BinaryWriter> write)> queue =
        new List<(Change, Guid, Action<BinaryWriter>)>();
    // Things added since the last send travel with their state at send time.
    private static readonly HashSet<Guid> added = new HashSet<Guid>();
    private static readonly MemoryStream batch = new MemoryStream();
    private static readonly BinaryWriter batchWriter = new BinaryWriter(batch);
    private static readonly MemoryStream entry = new MemoryStream();
    private static readonly BinaryWriter entryWriter = new BinaryWriter(entry);
    private static int applying;
    private static int loadingContent;

    // What this game changes through play. Applying another player's change, or loading scene
    // content every game loads alike, gives nothing to share.
    internal static bool Sharing => applying == 0 && loadingContent == 0 && CoopSession.SharesWorld;

    internal static bool Applying => applying > 0;

    // A world object whose changes are shared; one added since the last send needs none.
    internal static bool Tracks(WgoData data) => Sharing && data != null && !data.isTempObject &&
        FindObject(data.UniqueId.Guid) == data && !added.Contains(data.UniqueId.Guid);

    internal static bool IsNew(Guid target) => added.Contains(target);

    internal static WgoData FindObject(Guid id) =>
        MainGame.WorldData.Cache.wgoDataByUidCache.TryGetValue(id, out var data) ? data : null;

    internal static void Queue(Change change, Guid target, Action<BinaryWriter> write) => queue.Add((change, target, write));

    internal static void QueueAdded(Change change, Guid target, Action<BinaryWriter> write)
    {
        added.Add(target);
        queue.Add((change, target, write));
    }

    // Something added and removed before the next send was never seen by anyone else.
    internal static void QueueRemoved(Change change, Guid target, Action<BinaryWriter> write)
    {
        if (added.Remove(target))
        {
            queue.RemoveAll(item => item.target == target);
            return;
        }
        queue.Add((change, target, write));
    }

    internal static void Clear()
    {
        queue.Clear();
        added.Clear();
    }

    // Sends the queued changes; the host also sends them before capturing its game for a new player.
    internal static void Flush()
    {
        if (queue.Count == 0)
            return;
        if (!CoopSession.SharesWorld)
        {
            queue.Clear();
            added.Clear();
            return;
        }
        batch.SetLength(0);
        foreach (var (change, _, write) in queue)
        {
            entry.SetLength(0);
            try
            {
                write(entryWriter);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Multiplayer] Could not share {change}: {exception}");
                continue;
            }
            if (batch.Length > 0 && batch.Length + entry.Length > MaxBatch)
                Send();
            batchWriter.Write((byte)change);
            batchWriter.Write((int)entry.Length);
            batchWriter.Write(entry.GetBuffer(), 0, (int)entry.Length);
        }
        Send();
        queue.Clear();
        added.Clear();
    }

    // Another player's changes, in the order they were made.
    internal static void Apply(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        applying++;
        try
        {
            while (stream.Length - stream.Position >= 5)
            {
                var change = (Change)reader.ReadByte();
                byte[] payload = reader.ReadBytes(reader.ReadInt32());
                try
                {
                    Apply(change, new BinaryReader(new MemoryStream(payload)));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[Multiplayer] Could not apply {change}: {exception}");
                }
            }
        }
        finally
        {
            applying--;
        }
    }

    // Runs another player's change outside the stream, such as a movement step.
    internal static void Apply(Action change)
    {
        applying++;
        try
        {
            change();
        }
        finally
        {
            applying--;
        }
    }

    internal static void WriteId(BinaryWriter writer, Guid id) => writer.Write(id.ToByteArray());

    internal static Guid ReadId(BinaryReader reader) => new Guid(reader.ReadBytes(16));

    // Objects travel in the save's own format.
    internal static void WriteData<T>(BinaryWriter writer, T value)
    {
        byte[] bytes = SerializationUtility.SerializeValue(value, DataFormat.Binary);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static T ReadData<T>(BinaryReader reader) =>
        SerializationUtility.DeserializeValue<T>(reader.ReadBytes(reader.ReadInt32()), DataFormat.Binary);

    private static void Send()
    {
        if (batch.Length > 0)
            CoopSession.ShareChanges(batch.GetBuffer(), (int)batch.Length);
        batch.SetLength(0);
    }

    private static void Apply(Change change, BinaryReader reader)
    {
        switch (change)
        {
            case Change.AddObject:
                WorldObjects.ApplyAdd(reader);
                break;
            case Change.RemoveObject:
                WorldObjects.ApplyRemove(reader);
                break;
            case Change.AddStatic:
                WorldObjects.ApplyAddStatic(reader);
                break;
            case Change.RemoveStatic:
                WorldObjects.ApplyRemoveStatic(reader);
                break;
            case Change.AddDrop:
                WorldDrops.ApplyAdd(reader);
                break;
            case Change.RemoveDrop:
                WorldDrops.ApplyRemove(reader);
                break;
            case Change.DropCount:
                WorldDrops.ApplyCount(reader);
                break;
            case Change.DropPosition:
                WorldDrops.ApplyPosition(reader);
                break;
            case Change.WorldRes:
                ObjectState.ApplyWorldRes(reader);
                break;
            case Change.Quest:
                QuestSync.ApplyState(reader);
                break;
            case Change.Knowledge:
                SharedKnowledge.Apply(reader);
                break;
            case Change.Reputation:
                SharedKnowledge.ApplyReputation(reader);
                break;
            default:
                ObjectState.Apply(change, reader);
                break;
        }
    }

    // Scene content, and drops waiting for their scene, load the same way in every game.
    [HarmonyPatch]
    private static class LocalLoading
    {
        private static IEnumerable<MethodBase> TargetMethods() => new MethodBase[]
        {
            AccessTools.Method(typeof(WorldData), "AddDataToSceneFromContentData"),
            AccessTools.Method(typeof(WorldData), "DeInitDataFromConfig"),
            AccessTools.Method(typeof(GameSceneData), nameof(GameSceneData.ProcessQueuedDrops))
        };

        private static void Prefix() => loadingContent++;

        private static void Finalizer() => loadingContent--;
    }
}
