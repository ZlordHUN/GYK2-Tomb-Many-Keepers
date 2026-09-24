using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Patches.Widescreen.Display;

[HarmonyPatch]
internal static class FullscreenResolutionPatches
{
    private static int widthStep;
    private static int heightStep;
    private static int maximumScale;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameSettings), nameof(GameSettings.ApplyResolutionSettings))]
    private static void ReadDisplay(GameSettings __instance)
    {
        widthStep = 0;
        if (__instance.screenMode != ScreenMode.FullScreen)
            return;

        var display = Screen.mainWindowDisplayInfo;
        if (display.width <= 0 || display.height <= 0)
            return;

        // Exact aspect multiples avoid even rounding-induced presentation bars.
        var divisor = display.width;
        var remainder = display.height;
        while (remainder != 0)
        {
            var next = divisor % remainder;
            divisor = remainder;
            remainder = next;
        }
        widthStep = display.width / divisor;
        heightStep = display.height / divisor;
        maximumScale = Mathf.Min(SystemInfo.maxTextureSize / widthStep, SystemInfo.maxTextureSize / heightStep);
        if (maximumScale < 1)
            widthStep = 0;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ResolutionConfig), nameof(ResolutionConfig.AppliedHeight), MethodType.Getter)]
    private static void FitHeight(int ___pixelSize, ref int __result)
    {
        if (widthStep > 0)
        {
            // Both dimensions must also contain whole native pixel blocks.
            var pixelSize = Mathf.Clamp(___pixelSize, 1, maximumScale);
            var step = heightStep * pixelSize;
            __result = Mathf.Clamp(Mathf.RoundToInt((float)__result / step), 1, maximumScale / pixelSize) * step;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ResolutionConfig), nameof(ResolutionConfig.AppliedWidth), MethodType.Getter)]
    private static void FitWidth(ResolutionConfig __instance, ref int __result)
    {
        if (widthStep > 0)
            __result = __instance.AppliedHeight / heightStep * widthStep;
    }
}
