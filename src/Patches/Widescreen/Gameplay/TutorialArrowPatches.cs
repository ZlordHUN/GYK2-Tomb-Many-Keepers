using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Patches.Widescreen.Gameplay;

[HarmonyPatch(typeof(UITutorialArrow), "LateUpdate")]
internal static class TutorialArrowPatches
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> NormalizeScreenDimensions(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var width = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.width));
        var height = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.height));
        var normalize = AccessTools.Method(typeof(TutorialArrowPatches), nameof(NormalizeCanvasScale));
        var widthReads = 0;
        var heightReads = 0;
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].Calls(width))
                widthReads++;
            else if (code[i].Calls(height))
                heightReads++;
            else
                continue;

            if (i + 1 >= code.Count || code[i + 1].opcode != OpCodes.Conv_R4)
                throw new InvalidOperationException("UITutorialArrow screen dimensions no longer convert directly to float.");
            code.Insert(i + 2, new CodeInstruction(OpCodes.Call, normalize));
            i += 2;
        }
        if (widthReads != 1 || heightReads != 1)
            throw new InvalidOperationException($"UITutorialArrow expected one width and height read; found {widthReads} and {heightReads}.");
        return code;
    }

    // Native insets were authored for a canvas scale of two.
    private static float NormalizeCanvasScale(float dimension) => dimension * (2f / LazyUI.ScaleFactor);
}
