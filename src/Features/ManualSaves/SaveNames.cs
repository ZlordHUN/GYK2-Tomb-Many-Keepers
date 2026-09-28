using System;
using System.IO;
using System.Text;
using GYK2.TombManyKeepers.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.ManualSaves;

// The names players give their manual saves, kept beside each save in a small text file as the mod keeps its other
// campaign files, and shown in the name line the game's own save cards have but leave empty. Saves without a name,
// among them the backups the game rotates beside each save, show as the game draws them.
[HarmonyPatch]
internal static class SaveNames
{
    private const string Extension = ".tmkname";
    internal const int Longest = 48;

    // The save's name, or null for one without.
    internal static string Of(SaveSlotData slot)
    {
        if (slot == null || string.IsNullOrEmpty(slot.slotName))
            return null;
        try
        {
            string path = PathOf(slot);
            if (!File.Exists(path))
                return null;
            string name = File.ReadAllText(path, Encoding.UTF8).Trim();
            return name.Length == 0 ? null : name.Length > Longest ? name.Substring(0, Longest) : name;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[ManualSaves] Could not read a save's name: " + exception.Message);
            return null;
        }
    }

    internal static void Keep(SaveSlotData slot, string name)
    {
        try
        {
            File.WriteAllText(PathOf(slot), name, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[ManualSaves] Could not keep the save's name: " + exception.Message);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Remove))]
    private static void Forget(SaveSlotData slotData, bool __result)
    {
        if (!__result || slotData == null)
            return;
        try
        {
            // A save without a name has no file to remove.
            File.Delete(PathOf(slotData));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[ManualSaves] Could not remove the save's name: " + exception.Message);
        }
    }

    // The card hides its name line as it shows a save; a named save shows it again, its name as typed.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UISaveSlot), nameof(UISaveSlot.Show))]
    private static void ShowName(SaveSlotData saveSlotDataToDisplay, TextMeshProUGUI ___saveNameLabel)
    {
        string name = Of(saveSlotDataToDisplay);
        if (name == null || ___saveNameLabel == null)
            return;
        ___saveNameLabel.richText = true;
        NativeWindow.SetText(___saveNameLabel, NativeWindow.Literal(name));
        ___saveNameLabel.gameObject.SetActive(true);
    }

    private static string PathOf(SaveSlotData slot) => SaveSystem.SaveFolder + slot.slotName + Extension;
}
