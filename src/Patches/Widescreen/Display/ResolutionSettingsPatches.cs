using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Patches.Widescreen.Display;

[HarmonyPatch]
internal static class ResolutionSettingsPatches
{
    // Supplemental rendering sizes; the game's presets and driver modes are also included.
    private static readonly (int Width, int Height)[] AdditionalResolutions =
    {
        // Legacy and low-resolution modes.
        (320, 200), (320, 240), (400, 300), (512, 384), (640, 350), (640, 400), (640, 480),
        (720, 400), (720, 480), (720, 576), (800, 480), (800, 600), (848, 480), (854, 480), (1024, 600),
        // 4:3 and 5:4.
        (1024, 768), (1152, 864), (1400, 1050), (2048, 1536), (2560, 1920), (2560, 2048),
        (3200, 2400), (3840, 2880),
        // 16:9, including 5K, 6K and 8K.
        (640, 360), (960, 540), (1024, 576), (1152, 648), (1536, 864), (2048, 1152), (2304, 1296),
        (2880, 1620), (3072, 1728), (3200, 1800), (4096, 2304), (5120, 2880), (5760, 3240),
        (6016, 3384), (6144, 3456), (7680, 4320),
        // 16:10 and 3:2.
        (800, 500), (960, 600), (1024, 640), (1152, 720), (1600, 1000), (2048, 1280), (2304, 1440),
        (3072, 1920), (3200, 2000), (3840, 2400), (5120, 3200), (7680, 4800),
        (1440, 960), (1920, 1280), (2160, 1440), (2880, 1920), (3000, 2000),
        // Cinema and ultrawide.
        (2048, 1080), (4096, 2160), (8192, 4320), (1280, 540), (1920, 800), (1920, 810),
        (2560, 1080), (3840, 1600), (3840, 1620), (5120, 2160), (6880, 2880), (7680, 3200),
        // 32:9, 32:10 and multi-monitor layouts.
        (1920, 540), (2560, 720), (3200, 900), (3840, 1080), (5760, 1620), (6400, 1800), (7680, 2160),
        (2560, 800), (2880, 900), (3200, 1000), (3840, 1200), (5120, 1600), (5760, 1800), (7680, 2400),
        (5760, 1080), (7680, 1440)
    };

    // The game's own "(x3)" presets give 2560x1440 and 1920x1440 a closer gameplay view: the world renders at 1080
    // lines and is scaled up to the screen's 1440. These 1440-line ultrawide sizes get the same view beside their
    // native presets.
    private static readonly int[] ZoomedUltrawideWidths = { 3440, 5120 };
    private const int ZoomedHeight = 1440, ZoomedRenderHeight = 1080;
    private const string ZoomSuffix = "(x3)";

    private static readonly AccessTools.FieldRef<List<ResolutionConfig>> AvailableResolutions =
        AccessTools.StaticFieldRefAccess<List<ResolutionConfig>>(AccessTools.Field(typeof(ResolutionConfig), "availableResolutions"));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ResolutionConfig), nameof(ResolutionConfig.InitAvailableResolutions))]
    private static void BeforeInitialize(bool ___isInitialized, out bool __state) => __state = !___isInitialized;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ResolutionConfig), nameof(ResolutionConfig.InitAvailableResolutions))]
    private static void IncludeDisplayModes(bool __state, List<ResolutionConfig> ___availableResolutions,
        List<ResolutionConfig> ___hardcodedResolutions)
    {
        if (!__state)
            return;

        // Native presets are useful even when the display driver does not advertise their sizes.
        foreach (var preset in ___hardcodedResolutions)
            AddPreset(preset, ___availableResolutions);
        foreach (var width in ZoomedUltrawideWidths)
            AddPreset(new ResolutionConfig(width, ZoomedHeight, 2, UIWindowSizeType.Big, ZoomSuffix, useMainMenuScaleX2: true,
                isFakeResolution: true, width * ZoomedRenderHeight / ZoomedHeight, ZoomedRenderHeight), ___availableResolutions);

        foreach (var (width, height) in AdditionalResolutions)
            AddResolution(width, height, ___availableResolutions);

        foreach (var resolution in Screen.resolutions)
            AddResolution(resolution.width, resolution.height, ___availableResolutions);

        var desktop = Screen.currentResolution;
        AddResolution(desktop.width, desktop.height, ___availableResolutions);
        AddResolution(Screen.width, Screen.height, ___availableResolutions);
        // A display's own size, which drivers do not always list: the main display's, and that of the display the
        // window is on.
        var main = UnityEngine.Display.main;
        if (main != null)
            AddResolution(main.systemWidth, main.systemHeight, ___availableResolutions);
        var display = Screen.mainWindowDisplayInfo;
        AddResolution(display.width, display.height, ___availableResolutions);
        SortResolutions(___availableResolutions);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIGameSettingsWindow), nameof(UIGameSettingsWindow.Init))]
    private static void IncludeCurrentMode()
    {
        // Saved custom window sizes can be applied after the display list is initialized.
        var current = GameSettings.Instance.resolutionConfig;
        if (current != null && current.IsValid && ResolutionConfig.FindResolutionConfigIndex(current) < 0 &&
            ResolutionConfig.TryAddAvailableResolution(current.Copy()))
            SortResolutions(AvailableResolutions());
    }

    private static void SortResolutions(List<ResolutionConfig> available)
    {
        // Keep each native scale variant in order while inserting restored modes by size.
        var ordered = available.OrderBy(r => r.ListedWidth).ThenBy(r => r.ListedHeight).ToArray();
        available.Clear();
        available.AddRange(ordered);
    }

    private static void AddPreset(ResolutionConfig preset, List<ResolutionConfig> available)
    {
        var name = preset.GetResolutionName();
        if (!available.Exists(r => r.WindowSizeType == preset.WindowSizeType && r.GetResolutionName() == name))
            ResolutionConfig.TryAddAvailableResolution(preset);
    }

    private static void AddResolution(int width, int height, List<ResolutionConfig> available)
    {
        if (width <= 0 || height <= 0 || available.Exists(r => r.ListedWidth == width && r.ListedHeight == height))
            return;

        ResolutionConfig.TryAddAvailableResolution(new ResolutionConfig(width, height));
    }
}
