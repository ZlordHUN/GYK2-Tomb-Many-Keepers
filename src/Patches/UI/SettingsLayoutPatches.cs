using System.Collections;
using System.Collections.Generic;
using GYK2.TombManyKeepers.UI;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Patches.UI;

// The game's settings rows keep a wide column for their names, for its longer translations, so in a shorter language
// they stand right of the window's middle. Every row's parts move by the same whole units to stand, as drawn from the
// first letter of the longest name to the end of a volume's widest value, in the middle of the window, as the window
// opens and again once a new language has renamed them.
[HarmonyPatch(typeof(UIGameSettingsWindow))]
internal static class SettingsLayoutPatches
{
    // A volume's widest value.
    private static readonly string[] WidestVolume = { "100" };
    private static readonly List<RectTransform> Rows = new List<RectTransform>();
    private static UIGameSettingsWindow shown;
    private static bool listening;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGameSettingsWindow.Open))]
    private static void CentreOnOpen(UIGameSettingsWindow __instance)
    {
        shown = __instance;
        if (!listening)
        {
            GameSettings.OnLanguageChanged += CentreAfterRenaming;
            listening = true;
        }
        Centre(__instance);
    }

    private static void CentreAfterRenaming()
    {
        if (shown != null && shown.IsShown)
            shown.StartCoroutine(CentreNextFrame(shown));
    }

    // The names take the new language's words and font as the language changes; they are measured a frame later.
    private static IEnumerator CentreNextFrame(UIGameSettingsWindow window)
    {
        yield return null;
        if (window != null && window.IsShown)
            Centre(window);
    }

    private static void Centre(UIGameSettingsWindow window)
    {
        var content = window.transform.Find("GenericWIndowLayout/Content");
        if (content == null)
            return;
        Rows.Clear();
        foreach (RectTransform row in content)
            if (row.gameObject.activeSelf && row.Find("LeftName") != null)
                Rows.Add(row);
        DrawnRows.Centre(Rows, row => WidestVolume);
    }
}
