using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// When players reach for the same item, the host decides who gets it. A joined player's pickup
// claims the drop from the host, which hands over what is still there; the player's game then
// collects it with the native pickup effects. The host picks up its own drops directly.
[HarmonyPatch(typeof(PlayerData))]
internal static class DropClaims
{
    private const string ResourcePrefix = "game_res_";
    // An unanswered claim lapses after this long, so the drop can be claimed again.
    private const float ClaimTimeout = 3f;
    private static readonly AccessTools.FieldRef<PlayerData, Action<List<Item>>> Collected =
        AccessTools.FieldRefAccess<PlayerData, Action<List<Item>>>(nameof(PlayerData.OnDropCollected));
    private static readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerData.CollectDrop))]
    private static bool Collect(PlayerData __instance, DropView dropView)
    {
        var drop = dropView?.Data;
        if (!Claims(__instance, drop))
            return true;
        int count = drop.IsResDrop ? drop.Count
            : Mathf.Min(drop.Count, __instance.inventory.Data.CanAddItemCountToInventory(drop.Item));
        // A full inventory pushes the item away as usual.
        if (count <= 0)
            return true;
        Claim(drop, count);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerData.CollectResDrop))]
    private static bool CollectResources(PlayerData __instance, DropData drop)
    {
        if (!Claims(__instance, drop) || !drop.IsResDrop)
            return true;
        Claim(drop, drop.Count);
        return false;
    }

    // Host: hands a joined player what is left of the drop they claimed, up to the count asked.
    internal static void Grant(BinaryReader claim, BinaryWriter answer)
    {
        string sceneId = claim.ReadString();
        var id = WorldSync.ReadId(claim);
        int asked = claim.ReadInt32();
        var scene = MainGame.WorldData.GetGameSceneDataById(sceneId);
        var drop = scene == null ? null : WorldDrops.Find(scene, id);
        int given = drop == null || drop.IsRemoving ? 0 : Mathf.Clamp(asked, 0, drop.Count);
        answer.Write(sceneId);
        WorldSync.WriteId(answer, id);
        answer.Write(given);
        if (given == 0)
        {
            answer.Write(drop == null || drop.IsRemoving ? 0 : drop.Count);
            return;
        }
        var item = Item.Copy(drop.Item);
        item.Count = given;
        answer.Write(drop.Count - given);
        WorldSync.WriteData(answer, item);
        // Everyone sees the drop shrink or go, the claiming player first.
        if (given == drop.Count)
            scene.RemoveDrop(drop);
        else
        {
            drop.Item.Count -= given;
            drop.NotifyCountChanged();
        }
    }

    // Joined player: the host's answer to a claim.
    internal static void Receive(BinaryReader answer)
    {
        string sceneId = answer.ReadString();
        var id = WorldSync.ReadId(answer);
        int given = answer.ReadInt32();
        int left = answer.ReadInt32();
        pending.Remove(id);
        // The drop as the host now has it; the host's own change follows.
        WorldSync.Apply(() => WorldDrops.SetCount(sceneId, id, left));
        if (given > 0)
            Take(WorldSync.ReadData<Item>(answer));
    }

    internal static void Clear() => pending.Clear();

    // A joined player's own pickups in the running game are claimed.
    private static bool Claims(PlayerData player, DropData drop) =>
        drop?.Item != null && CoopSession.IsGuest && WorldSync.Sharing && player == MainGame.PlayerData;

    private static void Claim(DropData drop, int count)
    {
        var id = drop.UniqueId.Guid;
        if (pending.TryGetValue(id, out float since) && Time.unscaledTime - since < ClaimTimeout)
            return;
        pending[id] = Time.unscaledTime;
        string scene = drop.WorldId;
        CoopSession.RequestClaim(writer =>
        {
            writer.Write(scene);
            WorldSync.WriteId(writer, id);
            writer.Write(count);
        });
    }

    // Collects a granted item the way the native pickup does.
    private static void Take(Item item)
    {
        var player = MainGame.PlayerData;
        if (item.id.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        {
            string resource = item.id.Substring(ResourcePrefix.Length);
            if (TechDef.FlyingReses.Contains(resource))
            {
                var position = MainGame.PlayerController.transform.position;
                for (int i = 0; i < item.Count; i++)
                    FlyingTechPoint.Drop(position, resource);
                LazyBearTechnology.LazyAudio.Play("tech_point_collect");
            }
            else
                player.AddRes(resource, item.Count);
            foreach (var expression in item.Definition.onDropCollected)
                expression.Evaluate(item);
            return;
        }
        if (player.inventory.AddItemToInventory(item, out var added) && added.Count > 0)
        {
            foreach (var expression in item.Definition.onDropCollected)
                expression.Evaluate(added[0]);
            Collected(player)?.Invoke(added);
        }
        // What no longer fits falls at the player's feet.
        if (item.Count > 0)
            MainGame.Instance.dropSystem.DropItem(item, player.currentGameSceneId, player.position.Value);
    }
}
