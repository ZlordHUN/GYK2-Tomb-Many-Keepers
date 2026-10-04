using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Conveyors run only in the host's game (HostSimulation), so joined players saw belts stand still and
// empty. On each of its ticks the host shares what enters and leaves every cell; the others put the same
// on their cells and play the same step of the belt.
[HarmonyPatch]
internal static class ConveyorTicks
{
    private static readonly List<(Guid id, ConveyorMovableItemData into, ConveyorMovableItemData outOf, (string, int)[] lying)> cells =
        new List<(Guid, ConveyorMovableItemData, ConveyorMovableItemData, (string, int)[])>();

    // The game starts a belt step right after it has moved the items of a tick.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConveyorSystemAnimationOrchestrator), nameof(ConveyorSystemAnimationOrchestrator.SetState))]
    private static void Stepped(string name)
    {
        if (name != "Out" || !CoopSession.IsHosting || !WorldSync.Sharing)
            return;
        cells.Clear();
        foreach (var cell in MainGame.Instance.GameSave.conveyorSystemData.conveyorComponents)
        {
            var data = cell?.WgoData;
            if (data == null)
                continue;
            var items = data.Inventory.Data.Inventory;
            var lying = new (string, int)[items.Count];
            for (int i = 0; i < items.Count; i++)
                lying[i] = (items[i].id, items[i].Count);
            cells.Add((data.UniqueId.Guid, cell.InItem, cell.OutItem, lying));
        }
        var step = cells.ToArray();
        WorldSync.Queue(WorldSync.Change.ConveyorTick, Guid.Empty, writer =>
        {
            writer.Write(step.Length);
            foreach (var (id, into, outOf, lying) in step)
            {
                WorldSync.WriteId(writer, id);
                Write(writer, into);
                Write(writer, outOf);
                writer.Write(lying.Length);
                foreach (var (item, count) in lying)
                {
                    writer.Write(item ?? string.Empty);
                    writer.Write(count);
                }
            }
        });
    }

    private static readonly AccessTools.FieldRef<ConveyorSystem, Action> Updated =
        AccessTools.FieldRefAccess<ConveyorSystem, Action>(nameof(ConveyorSystem.OnUpdated));

    // Cells this game last put items on, to clear before the next step.
    private static readonly List<ConveyorComponent> filled = new List<ConveyorComponent>();


    internal static void Apply(BinaryReader reader)
    {
        foreach (var cell in filled)
            cell.ClearInAndOutItemDatas();
        filled.Clear();
        for (int count = reader.ReadInt32(); count > 0; count--)
        {
            var id = WorldSync.ReadId(reader);
            var into = Read(reader);
            var outOf = Read(reader);
            var lying = new (string, int)[reader.ReadInt32()];
            for (int i = 0; i < lying.Length; i++)
                lying[i] = (reader.ReadString(), reader.ReadInt32());
            if (!(WorldSync.FindObject(id) is ConveyorWgoData data) || data.ConveyorComponent == null)
                continue;
            data.ConveyorComponent.InItem = into;
            data.ConveyorComponent.OutItem = outOf;
            filled.Add(data.ConveyorComponent);
            Lay(data, lying);
        }
        // Cells read their item's way in and out on this event, as the host's step raises it.
        Updated(MainGame.Instance.conveyorSystem)?.Invoke();
        LazySingleton<ConveyorSystemAnimationOrchestrator>.Instance.SetState("Out");
        LazySingleton<ConveyorSoundSystem>.Instance.PauseSounds();
        LazySingleton<ConveyorSoundSystem>.Instance.PlaySounds();
    }

    // What lies on a cell between steps is its inventory, which the host's step changes; the belt's own
    // step redraws it at the right moment.
    private static void Lay(ConveyorWgoData data, (string id, int count)[] lying)
    {
        var items = data.Inventory.Data.Inventory;
        bool same = items.Count == lying.Length;
        for (int i = 0; same && i < lying.Length; i++)
            same = items[i].id == lying[i].id && items[i].Count == lying[i].count;
        if (same)
            return;
        data.Inventory.Clear();
        foreach (var (id, count) in lying)
            data.Inventory.AddItemToInventory(new Item(id, count));
    }

    private static void Write(BinaryWriter writer, ConveyorMovableItemData item)
    {
        writer.Write(item != null);
        if (item == null)
            return;
        writer.Write(item.itemId ?? string.Empty);
        writer.Write((int)item.direction);
        writer.Write(item.isCommon);
    }

    private static ConveyorMovableItemData Read(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        string itemId = reader.ReadString();
        var direction = (Direction)reader.ReadInt32();
        return new ConveyorMovableItemData(itemId, direction, reader.ReadBoolean());
    }
}
