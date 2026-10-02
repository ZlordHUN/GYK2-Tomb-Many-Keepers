using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// What a station crafts and the green bars of its progress show alike in every game. Only the game using a station
// runs its craft (StationLeases): the host for its world's crafting, its own keeper's work and its zombies', or a joined
// player at a station they borrowed, so the other games showed the craft as they last had it, or not at all. The game
// running a craft shares it whole as its queue, status or items change, and its progress as it fills; the others take
// it on their copy of the station, which runs nothing there, and tell only what draws the station of it, so the game's
// own craft widget draws its bars as the running game does. A zombie crafter's reaction to its station's craft, which
// places orders and starts crafts, stays with the game running it. A station a game begins running, a borrowed one or
// one given back, goes whole at once, with the changes its last game made as it handed it on.
internal static class CraftProgress
{
    // How far a craft is into its next tick moves every frame: it goes at most this often, in twentieths.
    private const float Interval = 0.2f;
    private const int TickSteps = 20;
    private static readonly AccessTools.FieldRef<CraftComponent, Action<CraftComponentStatus>> StatusChanged =
        AccessTools.FieldRefAccess<CraftComponent, Action<CraftComponentStatus>>(nameof(CraftComponent.OnStatusChanged));
    private static readonly AccessTools.FieldRef<CraftComponent, int> QueueIndex =
        AccessTools.FieldRefAccess<CraftComponent, int>("curCraftQueueIdx");
    private static readonly AccessTools.FieldRef<CraftComponent, float> TickTime =
        AccessTools.FieldRefAccess<CraftComponent, float>("currentAutoCraftTickTime");
    private static readonly AccessTools.FieldRef<CraftElementBase, int> Ticks =
        AccessTools.FieldRefAccess<CraftElementBase, int>("currentProgressTicks");
    private static readonly AccessTools.FieldRef<CraftElementBase, int> Succeeded =
        AccessTools.FieldRefAccess<CraftElementBase, int>("succeededProgressTicks");
    private static readonly AccessTools.FieldRef<CraftElementBase, int> Failed =
        AccessTools.FieldRefAccess<CraftElementBase, int>("failedProgressTicks");
    private static readonly AccessTools.FieldRef<CraftElementBase, int> Total =
        AccessTools.FieldRefAccess<CraftElementBase, int>("totalProgressTicks");
    // What each station this game runs last showed the other games.
    private static readonly Dictionary<Guid, Shown> shown = new Dictionary<Guid, Shown>();
    private static readonly HashSet<Guid> seen = new HashSet<Guid>();
    private static readonly List<Guid> ended = new List<Guid>();

    private sealed class Shown
    {
        internal int Shape;
        internal (int ticks, int succeeded, int failed, int total, int zombie) Progress;
        internal int Step;
        internal float At;
    }

    // Each frame, with the frame's other changes: the crafts this game runs, and the end of those it showed.
    internal static void Share()
    {
        var save = MainGame.Instance?.GameSave;
        if (save == null || MainGame.Instance.gameState != MainGame.GameState.InGame)
            return;
        float now = Time.unscaledTime;
        seen.Clear();
        var active = save.craftSystemData?.activeCrafts;
        if (active != null)
        {
            foreach (var craft in active)
                Watch(craft?.CraftableObject as WgoData, now);
        }
        if (save.craftSystemData?.zombieCraftActivities is { } workers)
        {
            foreach (var activity in workers)
                Watch(MainGame.WorldData.GetWgoData(activity.WgoUniqueId), now);
        }
        if (save.conveyorSystemData?.zombieCraftActivities is { } conveyors)
        {
            foreach (var activity in conveyors)
                Watch(MainGame.WorldData.GetWgoData(activity.WgoUniqueId), now);
        }
        foreach (var station in StationLeases.Used)
            Watch(station, now);
        ended.Clear();
        foreach (var id in shown.Keys)
        {
            if (!seen.Contains(id))
                ended.Add(id);
        }
        foreach (var id in ended)
        {
            var station = WorldSync.FindObject(id);
            if (station == null)
                shown.Remove(id);
            else
                Watch(station, now);
        }
    }

    private static void Watch(WgoData station, float now)
    {
        if (station == null || !seen.Add(station.UniqueId.Guid))
            return;
        var id = station.UniqueId.Guid;
        var craft = station.CraftComponent;
        if (craft == null || !StationLeases.Runs(craft) || !WorldSync.Tracks(station))
        {
            // Another game runs it now, and shares it.
            shown.Remove(id);
            return;
        }
        bool known = shown.TryGetValue(id, out var last);
        if (!known && !craft.HasCraftsInQueue)
            return;
        int shape = ShapeOf(craft);
        var progress = ProgressOf(craft);
        int step = StepOf(craft);
        if (!known || shape != last.Shape)
        {
            ShareWhole(station);
            shown[id] = last = new Shown { Shape = shape, Progress = progress, Step = step, At = now };
        }
        else if (!progress.Equals(last.Progress) || step != last.Step && now - last.At >= Interval)
        {
            ShareProgress(station);
            last.Progress = progress;
            last.Step = step;
            last.At = now;
        }
        // An idle station has shown its end and has nothing more to show until it crafts again.
        if (!craft.HasCraftsInQueue)
            shown.Remove(id);
    }

    // What the station crafts, in what order and how far along each item's start and finish: a change goes whole.
    private static int ShapeOf(CraftComponent craft)
    {
        var shape = new HashCode();
        shape.Add(craft.Status);
        shape.Add(QueueIndex(craft));
        shape.Add(craft.HasPreFinishUpdate);
        foreach (var element in craft.CraftElementsQueue)
        {
            shape.Add(element?.CraftId);
            shape.Add(element?.Count ?? 0);
            shape.Add(element?.IsStarted ?? false);
            shape.Add(element?.CraftStatus ?? default);
        }
        return shape.ToHashCode();
    }

    // A garden's growth and a hidden craft show no craft widget, so their next tick's approach shows nowhere.
    private static int StepOf(CraftComponent craft)
    {
        var element = craft.CurrentCraftElement;
        if (element == null || element.Def.isHidden || element.ParamsData.craftParamsType == CraftParamsData.CraftParamsType.GardenGrowing)
            return 0;
        return Mathf.FloorToInt(craft.AutoCraftTickProgressNormalized * TickSteps);
    }

    private static (int, int, int, int, int) ProgressOf(CraftComponent craft)
    {
        var element = craft.CurrentCraftElement;
        return element == null ? (0, 0, 0, 0, craft.ZombieSubTicks)
            : (Ticks(element), Succeeded(element), Failed(element), Total(element), craft.ZombieSubTicks);
    }

    private static void ShareWhole(WgoData station)
    {
        var id = station.UniqueId.Guid;
        WorldSync.Queue(WorldSync.Change.Craft, id, writer =>
        {
            WorldSync.WriteId(writer, id);
            WorldSync.WriteData(writer, station.CraftComponent);
        });
    }

    private static void ShareProgress(WgoData station)
    {
        var id = station.UniqueId.Guid;
        WorldSync.Queue(WorldSync.Change.CraftProgress, id, writer =>
        {
            var craft = station.CraftComponent;
            var element = craft.CurrentCraftElement;
            var (ticks, succeeded, failed, total, zombie) = ProgressOf(craft);
            WorldSync.WriteId(writer, id);
            writer.Write(element?.CraftId ?? string.Empty);
            writer.Write(QueueIndex(craft));
            writer.Write(ticks);
            writer.Write(succeeded);
            writer.Write(failed);
            writer.Write(total);
            writer.Write(zombie);
            writer.Write(TickTime(craft));
        });
    }

    // Another game's craft on this game's copy of its station.
    internal static void ApplyWhole(BinaryReader reader)
    {
        var station = WorldSync.FindObject(WorldSync.ReadId(reader));
        byte[] state = reader.ReadBytes(reader.ReadInt32());
        if (station?.CraftComponent == null || StationLeases.Runs(station.CraftComponent) || !StationLeases.CopyCraft(station, state))
            return;
        Drawn(station.CraftComponent);
        GameScene.GetWgoViewGlobal(station.UniqueId)?.DrawWidgets();
    }

    internal static void ApplyProgress(BinaryReader reader)
    {
        var station = WorldSync.FindObject(WorldSync.ReadId(reader));
        string craftId = reader.ReadString();
        int index = reader.ReadInt32(), ticks = reader.ReadInt32(), succeeded = reader.ReadInt32(), failed = reader.ReadInt32(),
            total = reader.ReadInt32(), zombie = reader.ReadInt32();
        float tickTime = reader.ReadSingle();
        var craft = station?.CraftComponent;
        if (craft == null || StationLeases.Runs(craft))
            return;
        // Progress belongs to the item the station shows; a whole craft brings any other.
        var element = craft.CurrentCraftElement;
        if (QueueIndex(craft) != index || (element?.CraftId ?? string.Empty) != craftId)
            return;
        if (element != null)
        {
            Ticks(element) = ticks;
            Succeeded(element) = succeeded;
            Failed(element) = failed;
            Total(element) = total;
        }
        TickTime(craft) = tickTime;
        // The widget's slider follows sub-ticks as the native ones move it.
        if (craft.ZombieSubTicks != zombie)
            craft.ZombieSubTicks = zombie;
    }

    // The station's view, what it draws by its craft and its craft widget learn the new state; nothing else does.
    private static void Drawn(CraftComponent craft)
    {
        var handlers = StatusChanged(craft);
        if (handlers == null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            if (handler.Target is Wgo || handler.Target is ConditionalDrawer || handler.Target is UICraftHintWidget)
                ((Action<CraftComponentStatus>)handler)(craft.Status);
        }
    }

    internal static void Clear()
    {
        shown.Clear();
        seen.Clear();
    }
}
