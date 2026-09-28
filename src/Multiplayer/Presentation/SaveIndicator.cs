using System.Reflection;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// The host's saving on joined players' screens, in the game's own saving overlay, as GYK1 shared its saving
// indicator: it shows as the host starts writing and goes as the host finishes, or after a while without word from a
// host that left, and with the session.
internal static class SaveIndicator
{
    private const float LongestSave = 60f;
    private static readonly MethodInfo Enable = AccessTools.Method(typeof(UISaveOverlay), "EnableOverlay");
    private static readonly MethodInfo Disable = AccessTools.Method(typeof(UISaveOverlay), "DisableOverlay");
    private static float shownAt = -1f;

    internal static void Show(bool shown)
    {
        var overlay = LazyUI.Get<UISaveOverlay>();
        if (overlay == null)
            return;
        (shown ? Enable : Disable).Invoke(overlay, null);
        shownAt = shown ? Time.unscaledTime : -1f;
    }

    internal static void Update()
    {
        if (shownAt >= 0f && Time.unscaledTime - shownAt > LongestSave)
            Show(false);
    }

    internal static void Clear()
    {
        if (shownAt >= 0f)
            Show(false);
    }
}
