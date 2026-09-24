using GYK2.TombManyKeepers.Features.Widescreen.MainMenu;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Patches.Widescreen.MainMenu;

[HarmonyPatch(typeof(MainGame), "ApplyMainMenuViewScaleByCurrentResolution")]
internal static class MainMenuWidescreenPatches
{
    [HarmonyPostfix]
    private static void FitBackground(GameObject ___mainMenuViewObject, Vector3 ___mainMenuViewScaleX1)
    {
        var resolution = ResolutionConfig.currentResolution;
        if (___mainMenuViewObject == null || resolution == null || !resolution.IsValid)
            return;

        var aspect = Mathf.Max((float)resolution.AppliedWidth / resolution.AppliedHeight,
            (float)ResolutionConfig.Width / ResolutionConfig.Height);
        var wide = aspect > 16f / 9f + 0.001f;
        var background = ___mainMenuViewObject.GetComponent<MainMenuBackground>();
        if (wide && background == null)
        {
            background = ___mainMenuViewObject.AddComponent<MainMenuBackground>();
            background.Initialize();
        }
        if (background != null)
            background.SetWide(wide);

        // The native Y scale compensates for the camera angle; fit the artwork uniformly.
        var height = 2f * CameraSystem.CalculateOrthographicSize(resolution.AppliedHeight, ResolutionConfig.PixelSize);
        var artworkAspect = wide ? MainMenuBackground.Aspect : 16f / 9f;
        var scale = height / MainMenuBackground.Height * Mathf.Max(1f, aspect / artworkAspect);
        ___mainMenuViewObject.transform.localScale = ___mainMenuViewScaleX1 * scale;
    }
}
