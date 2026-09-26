using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Changes to an object that stays in place: visibility, interactivity, resources, pending
// interaction events, parts and their states, animations, health and tool hits. Each change
// replays through the same native setter; world resources travel the same way.
[HarmonyPatch(typeof(WgoData))]
internal static class ObjectState
{
    private static readonly AccessTools.FieldRef<WgoData, List<InteractionEvent>> Events =
        AccessTools.FieldRefAccess<WgoData, List<InteractionEvent>>("events");
    private static readonly Action<WgoData> NotifyEvents =
        AccessTools.MethodDelegate<Action<WgoData>>(AccessTools.Method(typeof(WgoData), "NotifyInteractionEventChanged"));

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.IsHidden), MethodType.Setter)]
    private static void Hidden(WgoData __instance, bool value) =>
        Share(WorldSync.Change.Hidden, __instance, writer => writer.Write(value));

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.IsInteractable), MethodType.Setter)]
    private static void Interactable(WgoData __instance, bool value) =>
        Share(WorldSync.Change.Interactable, __instance, writer => writer.Write(value));

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.AddInteractionEvent))]
    private static void EventAdded(WgoData __instance) => ShareEvents(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.RemoveInteractionEvent))]
    private static void EventRemoved(WgoData __instance) => ShareEvents(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.FireInteractionEvent))]
    private static void EventFired(WgoData __instance) => ShareEvents(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.AddWgoPart))]
    private static void PartAdded(WgoData __instance, string wgoPartId)
    {
        var part = __instance.AdditionalWgoPartsData.FirstOrDefault(item => item.id == wgoPartId);
        if (part == null)
            return;
        string variation = part.variationId ?? string.Empty;
        int rotation = part.rotationIndex;
        Share(WorldSync.Change.AddPart, __instance, writer =>
        {
            writer.Write(wgoPartId);
            writer.Write(variation);
            writer.Write(rotation);
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.RemoveWgoPart))]
    private static void PartRemoved(WgoData __instance, string wgoPartId) =>
        Share(WorldSync.Change.RemovePart, __instance, writer => writer.Write(wgoPartId));

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.RemoveAllWgoParts))]
    private static void PartsCleared(WgoData __instance) => Share(WorldSync.Change.ClearParts, __instance, _ => { });

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.SetTriggerToAnimator))]
    private static void AnimationTrigger(WgoData __instance, string triggerName)
    {
        if (!StoredTrigger.Replaying)
            Share(WorldSync.Change.AnimationTrigger, __instance, writer => writer.Write(triggerName ?? string.Empty));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.SetStateToAnimator))]
    private static void AnimatorState(WgoData __instance, AnimationState animationState) =>
        Share(WorldSync.Change.AnimationState, __instance, writer => writer.Write((int)animationState));

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.SetLayerWeightToAnimator))]
    private static void AnimationLayer(WgoData __instance, int layerIndex, float weight) =>
        Share(WorldSync.Change.AnimationLayer, __instance, writer =>
        {
            writer.Write(layerIndex);
            writer.Write(weight);
        });

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.SetCustomAnimationTrigger))]
    private static void CustomTrigger(WgoData __instance, string triggerName) =>
        Share(WorldSync.Change.CustomTrigger, __instance, writer => writer.Write(triggerName ?? string.Empty));

    // Health travels as a value; only the game that dealt the last hit runs the object's death.
    [HarmonyPostfix]
    [HarmonyPatch("HandleHpChanged")]
    private static void HealthChanged(WgoData __instance, HPComponent component)
    {
        int hp = component.Hp;
        bool damaged = component.wasDamagedAtLeastOnce;
        Share(WorldSync.Change.Hp, __instance, writer =>
        {
            writer.Write(hp);
            writer.Write(damaged);
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WgoData.NotifyApplyTool))]
    private static void ToolHit(WgoData __instance, bool isFirstHit) =>
        Share(WorldSync.Change.ToolTick, __instance, writer => writer.Write(isFirstHit));

    internal static void Apply(WorldSync.Change change, BinaryReader reader)
    {
        var data = WorldSync.FindObject(WorldSync.ReadId(reader));
        if (data == null)
            return;
        switch (change)
        {
            case WorldSync.Change.Hidden:
                data.IsHidden = reader.ReadBoolean();
                break;
            case WorldSync.Change.Interactable:
                data.IsInteractable = reader.ReadBoolean();
                break;
            case WorldSync.Change.ObjectRes:
                for (int count = reader.ReadInt32(); count > 0; count--)
                    data.SetGameRes(reader.ReadString(), reader.ReadSingle());
                break;
            case WorldSync.Change.Events:
                var events = Events(data);
                events.Clear();
                events.AddRange(WorldSync.ReadData<List<InteractionEvent>>(reader) ?? new List<InteractionEvent>());
                NotifyEvents(data);
                break;
            case WorldSync.Change.AddPart:
                string added = reader.ReadString();
                string variation = reader.ReadString();
                int rotation = reader.ReadInt32();
                if (data.AdditionalWgoPartsData.All(part => part.id != added))
                    data.AddWgoPart(added, variation, rotation);
                break;
            case WorldSync.Change.RemovePart:
                string removed = reader.ReadString();
                if (data.AdditionalWgoPartsData.Any(part => part.id == removed))
                    data.RemoveWgoPart(removed);
                break;
            case WorldSync.Change.ClearParts:
                data.RemoveAllWgoParts();
                break;
            case WorldSync.Change.PartState:
                string partId = reader.ReadString();
                string state = reader.ReadString();
                int stateRotation = reader.ReadInt32();
                var target = data.MainWgoPartData?.id == partId ? data.MainWgoPartData
                    : data.AdditionalWgoPartsData.FirstOrDefault(part => part.id == partId);
                target?.TryApplyState(data.UniqueId, state, stateRotation);
                break;
            case WorldSync.Change.AnimationTrigger:
                data.SetTriggerToAnimator(reader.ReadString());
                break;
            case WorldSync.Change.AnimationState:
                data.SetStateToAnimator((AnimationState)reader.ReadInt32());
                break;
            case WorldSync.Change.AnimationLayer:
                data.SetLayerWeightToAnimator(reader.ReadInt32(), reader.ReadSingle());
                break;
            case WorldSync.Change.CustomTrigger:
                data.SetCustomAnimationTrigger(reader.ReadString());
                break;
            case WorldSync.Change.Hp:
                data.HpComponent.SetCustomHpValue(reader.ReadInt32(), overrideMaxHpValue: false);
                data.HpComponent.wasDamagedAtLeastOnce = reader.ReadBoolean();
                break;
            case WorldSync.Change.ToolTick:
                data.NotifyApplyTool(reader.ReadBoolean());
                break;
        }
    }

    internal static void ApplyWorldRes(BinaryReader reader)
    {
        for (int count = reader.ReadInt32(); count > 0; count--)
            MainGame.WorldData.SetGameRes(reader.ReadString(), reader.ReadSingle());
    }

    private static void Share(WorldSync.Change change, WgoData data, Action<BinaryWriter> write)
    {
        if (!WorldSync.Tracks(data))
            return;
        var id = data.UniqueId.Guid;
        WorldSync.Queue(change, id, writer =>
        {
            WorldSync.WriteId(writer, id);
            write(writer);
        });
    }

    private static void ShareEvents(WgoData data)
    {
        if (!WorldSync.Tracks(data))
            return;
        var events = new List<InteractionEvent>(Events(data));
        Share(WorldSync.Change.Events, data, writer => WorldSync.WriteData(writer, events));
    }

    // The resources a change touched: one by id, or every resource of a set.
    private static string[] Touched(object changed) => changed is string id ? new[] { id }
        : changed is GameRes resources ? resources.List.Select(atom => atom.type).ToArray()
        : Array.Empty<string>();

    private static void ShareValues(WorldSync.Change change, Guid target, string[] ids, Func<string, float> value)
    {
        var values = ids.Distinct().Select(id => (id, value(id))).ToArray();
        if (values.Length == 0)
            return;
        WorldSync.Queue(change, target, writer =>
        {
            if (target != Guid.Empty)
                WorldSync.WriteId(writer, target);
            writer.Write(values.Length);
            foreach (var (id, amount) in values)
            {
                writer.Write(id);
                writer.Write(amount);
            }
        });
    }

    private static IEnumerable<MethodBase> ResourceSetters(Type type) => AccessTools.GetDeclaredMethods(type)
        .Where(method => method.Name is "SetGameRes" or "AddGameRes" or "SubGameRes" or "MultiplyGameRes");

    [HarmonyPatch]
    private static class ObjectResources
    {
        private static IEnumerable<MethodBase> TargetMethods() => ResourceSetters(typeof(WgoData));

        private static void Postfix(WgoData __instance, object __0)
        {
            if (WorldSync.Tracks(__instance))
                ShareValues(WorldSync.Change.ObjectRes, __instance.UniqueId.Guid, Touched(__0), __instance.GetGameRes);
        }
    }

    [HarmonyPatch]
    private static class WorldResources
    {
        private static IEnumerable<MethodBase> TargetMethods() => ResourceSetters(typeof(WorldData));

        private static void Postfix(WorldData __instance, object __0)
        {
            if (WorldSync.Sharing && __instance == MainGame.WorldData)
                ShareValues(WorldSync.Change.WorldRes, Guid.Empty, Touched(__0), __instance.GetGameRes);
        }
    }

    // Every view replays its object's stored trigger when it binds; that trigger travels as the
    // object's own state instead.
    [HarmonyPatch(typeof(WgoData), nameof(WgoData.TryFireSerializedTrigger))]
    private static class StoredTrigger
    {
        internal static bool Replaying { get; private set; }

        private static void Prefix() => Replaying = true;

        private static void Finalizer() => Replaying = false;
    }

    [HarmonyPatch(typeof(WgoPartData), nameof(WgoPartData.TryApplyState))]
    private static class PartStates
    {
        private static void Postfix(WgoPartData __instance, SGuid parent, bool __result)
        {
            if (!__result || parent == null)
                return;
            var owner = WorldSync.FindObject(parent.Guid);
            string partId = __instance.id;
            string variation = __instance.variationId ?? string.Empty;
            int rotation = __instance.rotationIndex;
            Share(WorldSync.Change.PartState, owner, writer =>
            {
                writer.Write(partId);
                writer.Write(variation);
                writer.Write(rotation);
            });
        }
    }
}
