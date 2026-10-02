using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GK2.FlowCanvasNodes;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Whether a line the game shows now is a conversation's or a cutscene's, and whether the answers it shows now are:
// the game's dialogue nodes show those. A keeper's remark on what they touch, the chat's bubbles, and the choices of a
// building site or an arena, which are the keeper's own business, are not.
[HarmonyPatch]
internal static class Conversation
{
    private static int saying;
    private static int choosing;

    internal static bool Saying => saying > 0;

    internal static bool Choosing => choosing > 0;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Flow_Talk), "DoTalk")]
    [HarmonyPatch(typeof(Flow_TalkWisp), "DoTalk")]
    [HarmonyPatch(typeof(Flow_MultiTalk), "DoTalkIteration")]
    private static void Say() => saying++;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Flow_Talk), "DoTalk")]
    [HarmonyPatch(typeof(Flow_TalkWisp), "DoTalk")]
    [HarmonyPatch(typeof(Flow_MultiTalk), "DoTalkIteration")]
    private static void Said() => saying--;

    // The Multi Answer node's In port, a lambda of the node: the one that calls Bubble.ShowMultiAnswer.
    [HarmonyPatch]
    private static class Choices
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var show = AccessTools.Method(typeof(Bubble), nameof(Bubble.ShowMultiAnswer));
            var token = BitConverter.GetBytes(show.MetadataToken);
            var types = new List<Type> { typeof(Flow_MultiAnswer) };
            types.AddRange(typeof(Flow_MultiAnswer).GetNestedTypes(AccessTools.all));
            var ports = types.SelectMany(AccessTools.GetDeclaredMethods)
                .Where(method => method.Module == show.Module && Calls(method.GetMethodBody()?.GetILAsByteArray(), token)).Cast<MethodBase>().ToList();
            if (ports.Count == 0)
                Debug.LogWarning("[Multiplayer] The game's dialogue answers were not found; each conversation's own player answers its choices");
            return ports;
        }

        // A call instruction, 0x28, with the method's token.
        private static bool Calls(byte[] il, byte[] token)
        {
            for (int i = 0; il != null && i + 4 < il.Length; i++)
            {
                if (il[i] == 0x28 && il[i + 1] == token[0] && il[i + 2] == token[1] && il[i + 3] == token[2] && il[i + 4] == token[3])
                    return true;
            }
            return false;
        }

        private static void Prefix() => choosing++;

        private static void Finalizer() => choosing--;
    }
}
