using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Containers and workstations belong to the shared world, and one player uses each at a time.
// A joined player borrows a chest or station from the host while its window is open or their
// keeper works there: the host hands over its contents and craft, with the area's storage a
// station's crafts draw from, and takes back the player's changes once they are done. Only the
// game using a station runs its craft. Anyone else finds it in use.
[HarmonyPatch]
internal static class StationLeases
{
    private const float RequestTimeout = 3f;
    // A borrowed station opens its window or starts work within this long, or goes back.
    private const float OpenGrace = 1f;
    private static readonly AccessTools.FieldRef<WGOInteractionHandlerBase, Wgo> Assigned =
        AccessTools.FieldRefAccess<WGOInteractionHandlerBase, Wgo>("assignedWgo");
    private static readonly AccessTools.FieldRef<Inventory, Item> Contents =
        AccessTools.FieldRefAccess<Inventory, Item>("inventoryItem");
    private static readonly AccessTools.FieldRef<CraftComponent, Action<CraftComponentStatus>> StatusChanged =
        AccessTools.FieldRefAccess<CraftComponent, Action<CraftComponentStatus>>(nameof(CraftComponent.OnStatusChanged));
    // The craft state a save keeps.
    private static readonly FieldInfo[] CraftState = typeof(CraftComponent)
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(field => !field.IsNotSerialized && !typeof(Delegate).IsAssignableFrom(field.FieldType) &&
            (field.IsPublic || field.IsDefined(typeof(SerializeField), false)))
        .ToArray();

    // Host: stations lent to joined players, with the contents each was lent with.
    private static readonly Dictionary<Guid, Loan> lent = new Dictionary<Guid, Loan>();
    // Stations this game's keeper is using: the host's own, or those a joined player borrowed.
    private static readonly Dictionary<Guid, Use> uses = new Dictionary<Guid, Use>();
    // Joined player: requests on their way, with the interaction to finish once lent.
    private static readonly Dictionary<Guid, (float at, Action retry)> requested = new Dictionary<Guid, (float, Action)>();
    private static readonly List<Guid> finished = new List<Guid>();
    private static Use opening;
    private static bool watchingWindows;
    private static float craftCheck;

    private sealed class Loan
    {
        internal int Slot;
        internal readonly Dictionary<Guid, (byte[] inventory, byte[] craftInventory)> Lent =
            new Dictionary<Guid, (byte[], byte[])>();
    }

    private sealed class Use
    {
        internal WgoData Station;
        internal Guid[] Storage = Array.Empty<Guid>();
        internal readonly List<LazyWidgetBase> Windows = new List<LazyWidgetBase>();
        internal float Since;
        internal bool Seen;
    }

    // Host: lends a station unless someone else is using it. One the host's world has not loaded
    // yet is lent as the player's game has it.
    internal static void Lend(int slot, BinaryReader request, BinaryWriter answer)
    {
        var id = WorldSync.ReadId(request);
        var station = WorldSync.FindObject(id);
        WorldSync.WriteId(answer, id);
        int holder = lent.TryGetValue(id, out var loan) ? loan.Slot : uses.ContainsKey(id) ? 1 : slot;
        answer.Write(holder == slot);
        if (holder != slot)
        {
            answer.Write(CoopSession.Current.PlayerName(holder) ?? string.Empty);
            return;
        }
        loan = new Loan { Slot = slot };
        var storage = station == null ? new List<WgoData>() : Storage(station);
        answer.Write(station == null ? 0 : storage.Count + 1);
        if (station != null)
        {
            WriteEntry(answer, station, true, loan);
            foreach (var data in storage)
                WriteEntry(answer, data, false, loan);
        }
        lent[id] = loan;
    }

    // Joined player: the host's answer to a request.
    internal static void Receive(BinaryReader answer)
    {
        var id = WorldSync.ReadId(answer);
        bool granted = answer.ReadBoolean();
        requested.TryGetValue(id, out var request);
        requested.Remove(id);
        if (!granted)
        {
            string holder = answer.ReadString();
            Notify(holder.Length == 0 ? "Someone is using this." : $"{holder} is using this.");
            return;
        }
        var station = WorldSync.FindObject(id);
        var storage = InstallContents(answer);
        if (station == null)
        {
            GiveBack(id, null, storage);
            return;
        }
        Begin(station).Storage = storage;
        request.retry?.Invoke();
    }

    // Host: takes back a station and the player's changes to it and the storage lent with it.
    internal static void TakeBack(int slot, BinaryReader returned)
    {
        var id = WorldSync.ReadId(returned);
        if (!lent.TryGetValue(id, out var loan) || loan.Slot != slot)
            return;
        lent.Remove(id);
        for (int count = returned.ReadInt32(); count > 0; count--)
        {
            var data = WorldSync.FindObject(WorldSync.ReadId(returned));
            byte[] inventory = ReadBlock(returned);
            bool isStation = returned.ReadBoolean();
            byte[] craft = isStation ? ReadBlock(returned) : null;
            byte[] craftInventory = isStation ? ReadBlock(returned) : null;
            if (data == null || !loan.Lent.TryGetValue(data.UniqueId.Guid, out var before))
                continue;
            Merge(data.Inventory, before.inventory, inventory, data);
            if (!isStation)
                continue;
            Merge(data.CraftableObjectCraftInventory, before.craftInventory, craftInventory, data);
            InstallCraft(data, craft);
        }
    }

    // Host: a player who left gives nothing back; their stations are free again.
    internal static void Forget(int slot)
    {
        foreach (var pair in lent.Where(pair => pair.Value.Slot == slot).ToList())
            lent.Remove(pair.Key);
    }

    // Each frame: stations no longer in use go back, and borrowed crafts run here.
    internal static void Update()
    {
        foreach (var pair in requested.Where(pair => Time.unscaledTime - pair.Value.at > RequestTimeout).ToList())
            requested.Remove(pair.Key);
        finished.Clear();
        foreach (var pair in uses)
        {
            if (!InUse(pair.Value))
                finished.Add(pair.Key);
        }
        foreach (var id in finished)
        {
            var use = uses[id];
            uses.Remove(id);
            if (CoopSession.IsGuest)
                GiveBack(id, use.Station, use.Storage);
        }
        if (CoopSession.IsGuest)
            RunBorrowedCrafts(Time.deltaTime);
    }

    // Joined player: everything borrowed goes back as the player leaves.
    internal static void ReturnAll()
    {
        foreach (var pair in uses)
            GiveBack(pair.Key, pair.Value.Station, pair.Value.Storage);
        Clear();
    }

    internal static void Clear()
    {
        lent.Clear();
        uses.Clear();
        requested.Clear();
        opening = null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WGOInteractionHandlerBase), nameof(WGOInteractionHandlerBase.Interact))]
    private static bool Interact(WGOInteractionHandlerBase __instance, PlayerController interactor, ref bool __result) =>
        Interacting(__instance, interactor, () => __instance.Interact(interactor), ref __result);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WGOInteractionHandlerBase), nameof(WGOInteractionHandlerBase.Interact2))]
    private static bool Interact2(WGOInteractionHandlerBase __instance, PlayerController interactor, ref bool __result) =>
        Interacting(__instance, interactor, () => __instance.Interact2(interactor), ref __result);

    // Burning a corpse and tending a garden begin before the shared interaction.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CrematoriumInteractionHandler), nameof(CrematoriumInteractionHandler.Interact))]
    private static bool Cremate(CrematoriumInteractionHandler __instance, PlayerController interactor, ref bool __result) =>
        Interacting(__instance, interactor, () => __instance.Interact(interactor), ref __result);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GardenInteractionHandler), nameof(GardenInteractionHandler.Interact))]
    private static bool Garden(GardenInteractionHandler __instance, PlayerController interactor, ref bool __result) =>
        Interacting(__instance, interactor, () => __instance.Interact(interactor), ref __result);

    // Working a station's craft with a tool needs the station too.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ToolComponent), nameof(ToolComponent.TryStartInteraction))]
    private static bool StartWork(IWorkActivity toolActor, ref bool __result)
    {
        if (toolActor is not PlayerCraftActivity craft || Available(craft.WgoData, null))
            return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.Update))]
    private static bool AutoCraft(CraftComponent __instance) => Runs(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.UpdateManual))]
    private static bool ManualCraft(CraftComponent __instance) => Runs(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.PreFinishUpdate))]
    private static bool FinishCraft(CraftComponent __instance) => Runs(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.UpdateQueueElementsCraftStatus))]
    private static bool QueueStatus(CraftComponent __instance) => Runs(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.UpdateCanContinueManualCraftState))]
    private static bool ManualState(CraftComponent __instance) => Runs(__instance);

    private static bool Interacting(WGOInteractionHandlerBase handler, PlayerController interactor, Action retry, ref bool result)
    {
        if (interactor != MainGame.PlayerController || Available(Assigned(handler)?.Data, retry))
            return true;
        // The interaction is taken; it finishes once the station is lent.
        result = true;
        return false;
    }

    // Whether this game's keeper can use the station now; a joined player asks the host for it.
    private static bool Available(WgoData station, Action retry)
    {
        if (!Shared(station))
            return true;
        var id = station.UniqueId.Guid;
        if (uses.TryGetValue(id, out var use))
        {
            Watch(use);
            return true;
        }
        if (CoopSession.IsHosting)
        {
            if (lent.TryGetValue(id, out var loan))
            {
                Notify($"{CoopSession.Current.PlayerName(loan.Slot)} is using this.");
                return false;
            }
            Begin(station);
            return true;
        }
        if (!requested.ContainsKey(id))
        {
            requested[id] = (Time.unscaledTime, retry);
            CoopSession.RequestStation(writer => WorldSync.WriteId(writer, id));
        }
        return false;
    }

    // Stations and containers of the running shared world; each player's own things are theirs.
    private static bool Shared(WgoData data) =>
        data != null && !data.isTempObject && CoopSession.SharesWorld && WorldSync.FindObject(data.UniqueId.Guid) == data &&
        (data.Definition.inventorySize != 0 || Crafts(data));

    private static bool Crafts(WgoData data) =>
        data.CraftComponent?.CraftableObject != null && data.CraftComponent.HasCraftsByBalance;

    // Only the game using a station runs its craft: the host's, unless a joined player has it.
    private static bool Runs(CraftComponent craft)
    {
        if (craft.CraftableObject is not WgoData data || !CoopSession.SharesWorld)
            return true;
        var id = data.UniqueId.Guid;
        return CoopSession.IsHosting ? !lent.ContainsKey(id) : uses.ContainsKey(id);
    }

    private static Use Begin(WgoData station)
    {
        var use = new Use { Station = station, Since = Time.unscaledTime };
        uses[station.UniqueId.Guid] = use;
        Watch(use);
        return use;
    }

    // Windows opened while an interaction starts belong to it.
    private static void Watch(Use use)
    {
        opening = use;
        use.Since = Time.unscaledTime;
        if (watchingWindows)
            return;
        watchingWindows = true;
        LazyWindowsStackController.OnWindowOpened += window =>
        {
            if (opening != null && Time.unscaledTime - opening.Since <= OpenGrace && !opening.Windows.Contains(window))
                opening.Windows.Add(window);
        };
    }

    private static bool InUse(Use use)
    {
        var player = MainGame.PlayerController;
        var work = player.PlayerWorkComponent.Wgo;
        bool inUse = use.Station.Worker is PlayerController worker && worker == player ||
            work != null && work.Data == use.Station ||
            use.Windows.Any(window => window != null && LazyWindowsStackController.IsWindowOpened(window));
        use.Seen |= inUse;
        return inUse || !use.Seen && Time.unscaledTime - use.Since <= OpenGrace;
    }

    private static void GiveBack(Guid id, WgoData station, Guid[] storageIds)
    {
        CoopSession.ReturnStation(writer =>
        {
            WorldSync.WriteId(writer, id);
            var storage = station == null ? new List<WgoData>()
                : storageIds.Select(WorldSync.FindObject).Where(data => data != null).ToList();
            writer.Write(station == null ? 0 : storage.Count + 1);
            if (station == null)
                return;
            WriteEntry(writer, station, true, null);
            foreach (var data in storage)
                WriteEntry(writer, data, false, null);
        });
    }

    // The area storage a station's crafts can draw from.
    private static List<WgoData> Storage(WgoData station)
    {
        var storage = new List<WgoData>();
        var zone = station.WorldZoneData;
        if (zone == null || !Crafts(station))
            return storage;
        foreach (var id in zone.wgoDataList)
        {
            var data = MainGame.WorldData.GetWgoData(id);
            if (data != null && data != station && data.Definition.inventorySize != 0 && data.Definition.OpenInMultiInventory)
                storage.Add(data);
        }
        return storage;
    }

    private static void WriteEntry(BinaryWriter writer, WgoData data, bool isStation, Loan loan)
    {
        WorldSync.WriteId(writer, data.UniqueId.Guid);
        byte[] inventory = Serialize(data.Inventory?.Data);
        WriteBlock(writer, inventory);
        writer.Write(isStation);
        byte[] craftInventory = null;
        if (isStation)
        {
            WriteBlock(writer, data.CraftComponent == null ? Array.Empty<byte>()
                : Sirenix.Serialization.SerializationUtility.SerializeValue(data.CraftComponent, Sirenix.Serialization.DataFormat.Binary));
            craftInventory = Serialize(data.CraftableObjectCraftInventory?.Data);
            WriteBlock(writer, craftInventory);
        }
        if (loan != null)
            loan.Lent[data.UniqueId.Guid] = (inventory, craftInventory);
    }

    // Joined player: the host's current contents replace this game's copies.
    private static Guid[] InstallContents(BinaryReader reader)
    {
        var storage = new List<Guid>();
        for (int count = reader.ReadInt32(); count > 0; count--)
        {
            var id = WorldSync.ReadId(reader);
            var data = WorldSync.FindObject(id);
            byte[] inventory = ReadBlock(reader);
            bool isStation = reader.ReadBoolean();
            byte[] craft = isStation ? ReadBlock(reader) : null;
            byte[] craftInventory = isStation ? ReadBlock(reader) : null;
            if (!isStation)
                storage.Add(id);
            if (data == null)
                continue;
            Replace(data.Inventory, inventory);
            if (!isStation)
                continue;
            Replace(data.CraftableObjectCraftInventory, craftInventory);
            InstallCraft(data, craft);
        }
        return storage.ToArray();
    }

    private static void Replace(Inventory inventory, byte[] contents)
    {
        var item = Deserialize(contents);
        if (inventory != null && item != null)
            Contents(inventory) = item;
    }

    // A station's craft takes on the state of the game that last ran it.
    private static void InstallCraft(WgoData data, byte[] state)
    {
        var live = data.CraftComponent;
        if (live == null || state == null || state.Length == 0)
            return;
        var source = Sirenix.Serialization.SerializationUtility.DeserializeValue<CraftComponent>(state, Sirenix.Serialization.DataFormat.Binary);
        if (source == null)
            return;
        foreach (var field in CraftState)
            field.SetValue(live, field.GetValue(source));
        live.Init(data);
        if (live.ShouldRegisterInCraftSystem())
            MainGame.Instance.craftSystem.AddCraftObject(live);
        else
            MainGame.Instance.craftSystem.RemoveCraftObject(live);
        StatusChanged(live)?.Invoke(live.Status);
    }

    // Host: applies what a player took and added since the contents were lent, so changes the
    // host's world made meanwhile stay.
    private static void Merge(Inventory inventory, byte[] lentContents, byte[] returnedContents, WgoData owner)
    {
        var before = Stacks(Deserialize(lentContents));
        var after = Stacks(Deserialize(returnedContents));
        if (inventory == null)
            return;
        foreach (var pair in before)
        {
            int left = after.TryGetValue(pair.Key, out var now) ? now.count : 0;
            int taken = pair.Value.count - left;
            if (taken > 0 && !inventory.RemoveItemFromInventoryByUID(pair.Value.item, taken))
                inventory.RemoveItemById(pair.Value.item.id, taken);
        }
        foreach (var pair in after)
        {
            int had = before.TryGetValue(pair.Key, out var was) ? was.count : 0;
            if (pair.Value.count <= had)
                continue;
            var added = Item.Copy(pair.Value.item);
            added.Count = pair.Value.count - had;
            inventory.AddItemToInventory(added);
            // What no longer fits falls beside its container.
            if (added.Count > 0)
                MainGame.Instance.dropSystem.DropItem(added, owner.WorldId, owner.GetDropPos(added));
        }
    }

    private static Dictionary<Guid, (Item item, int count)> Stacks(Item container)
    {
        var stacks = new Dictionary<Guid, (Item, int)>();
        if (container == null)
            return stacks;
        foreach (var item in container.Inventory)
        {
            if (item == null || item.IsEmpty)
                continue;
            var id = item.UniqueId.Guid;
            stacks[id] = stacks.TryGetValue(id, out var stack) ? (stack.Item1, stack.Item2 + item.Count) : (item, item.Count);
        }
        return stacks;
    }

    // Joined player: a borrowed station's craft runs here the way the host's crafting runs it.
    private static void RunBorrowedCrafts(float deltaTime)
    {
        craftCheck += deltaTime;
        bool check = craftCheck >= 1f;
        if (check)
            craftCheck = 0f;
        foreach (var use in uses.Values)
        {
            var craft = use.Station.CraftComponent;
            if (craft == null || !craft.ShouldRegisterInCraftSystem())
                continue;
            if (check && craft.Status != CraftComponentStatus.ReadyToFinishAutoCraft)
            {
                if (craft.IsQueueDelayed || craft.Status == CraftComponentStatus.ReadyToStartCraft ||
                    craft.Status == CraftComponentStatus.FinishDelayed)
                    craft.UpdateQueueElementsCraftStatus();
                if (!craft.IsAutoCraftable)
                    craft.UpdateCanContinueManualCraftState(deltaTime);
            }
            if (craft.IsAutoCraftable && !craft.HasPreFinishUpdate && !craft.IsDestroyingCraftActive)
                craft.Update(deltaTime);
            if (craft.HasPreFinishUpdate)
                craft.PreFinishUpdate(deltaTime);
        }
    }

    private static void WriteBlock(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static byte[] ReadBlock(BinaryReader reader) => reader.ReadBytes(reader.ReadInt32());

    private static byte[] Serialize(Item item) => item == null ? Array.Empty<byte>()
        : Sirenix.Serialization.SerializationUtility.SerializeValue(item, Sirenix.Serialization.DataFormat.Binary);

    private static Item Deserialize(byte[] bytes) => bytes == null || bytes.Length == 0 ? null
        : Sirenix.Serialization.SerializationUtility.DeserializeValue<Item>(bytes, Sirenix.Serialization.DataFormat.Binary);

    private static void Notify(string text)
    {
        if (MainGame.Instance.gameState == MainGame.GameState.InGame)
            LazySingleton<UINotificator>.Instance.ShowSimpleTextNotification(text);
    }
}
