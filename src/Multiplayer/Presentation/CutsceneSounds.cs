using System;
using System.IO;
using System.Reflection;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// The sounds and world effects of a player's cutscene play for the players watching it.
[HarmonyPatch]
internal static class CutsceneSounds
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldFX), nameof(WorldFX.Spawn), typeof(Vector3), typeof(string), typeof(Action), typeof(Vector3))]
    private static void Spawned(Vector3 worldPos, string name, Vector3 size)
    {
        if (!WatchedCutscene.InOwnCutscene || !CoopSession.SharesWorld)
            return;
        SharedPresentation.Send(SharedPresentation.Cue.Effect, writer =>
        {
            writer.Write(name ?? string.Empty);
            Write(writer, worldPos);
            Write(writer, size);
        });
    }

    internal static void Apply(int slot, SharedPresentation.Cue cue, BinaryReader reader)
    {
        if (cue == SharedPresentation.Cue.Sound)
        {
            string id = reader.ReadString();
            bool stop = reader.ReadBoolean();
            if (!SharedPresentation.Watches(slot) || id.Length == 0)
                return;
            if (stop)
                LazyAudio.Stop(id);
            else
                LazyAudio.Play(id);
            return;
        }
        string name = reader.ReadString();
        var position = Read(reader);
        var size = Read(reader);
        if (SharedPresentation.Watches(slot) && name.Length > 0)
            WorldFX.Spawn(position, name, null, size);
    }

    private static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    private static Vector3 Read(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    // A cutscene's Play Sound node; its port holds the sound it plays or stops.
    [HarmonyPatch]
    private static class PlaySound
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("GK2.FlowCanvasNodes.Flow_PlaySound"), "PlaySound");

        private static void Prefix(object __instance)
        {
            if (!WatchedCutscene.InOwnCutscene || !CoopSession.SharesWorld)
                return;
            var node = Traverse.Create(__instance);
            string id = node.Field("soundId").Property("value").GetValue<string>();
            bool stop = node.Field("stop").GetValue<bool>();
            SharedPresentation.Send(SharedPresentation.Cue.Sound, writer =>
            {
                writer.Write(id ?? string.Empty);
                writer.Write(stop);
            });
        }
    }
}
