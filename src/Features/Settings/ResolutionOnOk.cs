using System;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.Features.Settings;

// A resolution chosen in the game's Settings applies on OK, as the owner asked: the game applied each one as its arrows
// stepped through them, and the window resizing under the pointer made choosing one a chore. The row now only shows
// the resolution chosen; OK applies it and keeps Settings open, and OK again leaves as the game's own OK does. Leaving
// Settings any other way drops the choice.
[HarmonyPatch(typeof(UIGameSettingsWindow))]
internal static class ResolutionOnOk
{
    private static readonly AccessTools.FieldRef<UIGameSettingsWindow, UISwitchButton> ResolutionRow =
        AccessTools.FieldRefAccess<UIGameSettingsWindow, UISwitchButton>("resolutionSwitch");
    private static readonly AccessTools.FieldRef<UIGameSettingsWindow, UIDialogWindowButton> OkButton =
        AccessTools.FieldRefAccess<UIGameSettingsWindow, UIDialogWindowButton>("lazyButton");
    private static readonly AccessTools.FieldRef<UISwitchButton, Action<int>> Changed =
        AccessTools.FieldRefAccess<UISwitchButton, Action<int>>("onChangedCallback");
    // The resolution chosen and not yet applied.
    private static ResolutionConfig chosen;

    internal static bool Chosen => chosen != null;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGameSettingsWindow.Init))]
    private static void Built(UIGameSettingsWindow __instance) => Changed(ResolutionRow(__instance)) = index =>
    {
        var picked = ResolutionConfig.GetResolutionConfigByIndex(index);
        // Stepping back to the resolution in use chooses nothing.
        chosen = ResolutionConfig.FindResolutionConfigIndex(GameSettings.Instance.resolutionConfig) == index ? null : picked;
    };

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGameSettingsWindow.Open))]
    private static void Opened(UIGameSettingsWindow __instance)
    {
        chosen = null;
        var window = __instance;
        OkButton(window).Draw(new UIDialogWindowData.ButtonData(() => Ok(window), LLBase.L("btn_ok"), null, replaceForGamepad: true,
            GameKey.Select));
    }

    private static void Ok(UIGameSettingsWindow window)
    {
        if (chosen == null)
        {
            window.Close();
            return;
        }
        var settings = GameSettings.Instance;
        settings.resolutionConfig = chosen;
        chosen = null;
        settings.ApplyGraphicSettings();
        ((RectTransform)window.transform).RefreshContentFitter();
    }

    // The row shows the resolution chosen until it applies; the game's own refresh would show the one in use.
    [HarmonyPrefix]
    [HarmonyPatch("RefreshResolutionSwitch")]
    private static bool Refreshing() => chosen == null;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGameSettingsWindow.Close))]
    private static void Closed() => chosen = null;
}
