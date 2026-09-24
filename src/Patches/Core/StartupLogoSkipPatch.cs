using System.Collections;
using HarmonyLib;
using LazyBearTechnology.Preloader;
using UnityEngine;

namespace GYK2.TombManyKeepers.Patches.Core;

[HarmonyPatch]
internal static class StartupLogoSkipPatch
{
    private static bool skipLogo;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LazyPreloader), "DoFadeCoroutine")]
    private static void MakeLogoFadeSkippable(bool fadeIn, ref IEnumerator __result)
    {
        if (fadeIn)
            __result = ReadSkipDuringFade(__result);
    }

    private static IEnumerator ReadSkipDuringFade(IEnumerator fade)
    {
        skipLogo = false;
        while (fade.MoveNext())
        {
            // Remember presses during fade-in; the native fade still completes.
            skipLogo |= SkipPressed;
            yield return fade.Current;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LazyLogoData), nameof(LazyLogoData.DoProcess))]
    private static bool MakeLogoWaitSkippable(LazyLogoData __instance, ref IEnumerator __result)
    {
        if (__instance.showLengthMode != LazyLogoData.ShowLengthMode.TimeLimited)
            return true;

        __result = WaitForLogo(__instance.showingTime);
        return false;
    }

    private static IEnumerator WaitForLogo(float duration)
    {
        for (float elapsed = 0; elapsed < duration; elapsed += Time.deltaTime)
        {
            if (skipLogo || SkipPressed)
                break;
            yield return null;
        }
        skipLogo = false;
    }

    private static bool SkipPressed => Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0);
}
