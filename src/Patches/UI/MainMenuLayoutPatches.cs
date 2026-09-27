using System.Collections.Generic;
using GYK2.TombManyKeepers.UI.MainMenu;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.Patches.UI;

[HarmonyPatch(typeof(UIExtensions), nameof(UIExtensions.RefreshContentFitterAndDisable))]
internal static class MainMenuLayoutPatches
{
    private static readonly Vector3[] Corners = new Vector3[4];
    private static readonly AccessTools.FieldRef<UIMainMenuWindow, List<(RectTransform, Vector2)>> IntroRests =
        AccessTools.FieldRefAccess<UIMainMenuWindow, List<(RectTransform, Vector2)>>("introButtonRests");
    private static readonly List<(RectTransform, Vector2)> Moving = new List<(RectTransform, Vector2)>();

    [HarmonyPostfix]
    private static void FitContent(RectTransform __0)
    {
        if (__0 == null || __0.GetComponent<UIMainMenuWindow>() == null)
            return;

        var content = (RectTransform)__0.Find("Bg/Vertical Group");
        var available = ((RectTransform)content.parent).rect;
        var margin = 16f / ResolutionConfig.GetUiScaleFactor();
        // The mod's title stands under the logo, both giving way on short screens before the buttons do.
        ModTitle.Arrange(content, available.height - 2f * margin);
        // The menu's intro slides the buttons up from below their places; they are measured where they come to rest.
        Moving.Clear();
        foreach (var (part, rest) in IntroRests(__0.GetComponent<UIMainMenuWindow>()))
        {
            if (part == null)
                continue;
            Moving.Add((part, part.anchoredPosition));
            part.anchoredPosition = rest;
        }
        var minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (RectTransform child in content)
        {
            if (!child.gameObject.activeSelf)
                continue;
            // Measure the logo container, not the logo moving inside it during the intro.
            child.GetWorldCorners(Corners);
            foreach (var corner in Corners)
            {
                var point = (Vector2)content.InverseTransformPoint(corner);
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
            }
        }

        foreach (var (part, now) in Moving)
            part.anchoredPosition = now;
        var size = maximum - minimum;
        var scale = Mathf.Min(1f, (available.width - 2f * margin) / size.x,
            (available.height - 2f * margin) / size.y);
        content.localScale = Vector3.one * scale;
        var center = available.center - (minimum + maximum) * (0.5f * scale);
        content.localPosition = new Vector3(center.x, center.y, content.localPosition.z);
    }
}
