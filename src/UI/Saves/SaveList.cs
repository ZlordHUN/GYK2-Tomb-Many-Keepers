using System;
using System.Globalization;
using GYK2.TombManyKeepers.Features.ManualSaves;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Saves;

// Save Game and Load Game from the pause menu, in the game's own save list, as GYK1's worked. Saving, the list's
// new-game entry reads New Save and asks the new save's name, and any other save is saved over; a written save returns
// to the pause menu. Loading, the list shows the saves alone and a pick loads it as the game's own Continue does. The
// list's back returns to the pause menu.
[HarmonyPatch(typeof(UISaveSlotsWindow))]
internal static class SaveList
{
    internal enum Mode
    {
        None,
        Saving,
        Loading
    }

    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, UISaveSlot> NewSlot =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, UISaveSlot>("newSaveSlot");
    private static readonly AccessTools.FieldRef<UISaveSlot, GameObject> NewSlotText =
        AccessTools.FieldRefAccess<UISaveSlot, GameObject>("newSaveText");
    private static Mode mode;

    internal static Mode Current => mode;

    internal static void Open(Mode opening)
    {
        mode = opening;
        LazyUI.GetWindow<UIGamePauseWindow>().Close();
        LazyUI.GetWindow<UISaveSlotsWindow>().Open(null);
    }

    // After the Host Game list's own, which puts the game's header back while it does not pick.
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(UISaveSlotsWindow.Open))]
    private static void Opened(UISaveSlotsWindow __instance)
    {
        // Only a running game saves and loads from its pause menu.
        if (MainGame.Instance == null || MainGame.Instance.gameState != MainGame.GameState.InGame)
            mode = Mode.None;
        var newSlot = NewSlot(__instance);
        var newLabel = NewSlotText(newSlot)?.GetComponentInChildren<TMP_Text>(true);
        if (mode == Mode.None)
        {
            Restore(newLabel);
            return;
        }
        NativeWindow.SetText(NativeWindow.Find<TMP_Text>(__instance.transform, "GenericWIndowLayout/Frame/HeaderGroup/Header"),
            mode == Mode.Saving ? "Save Game" : "Load Game");
        if (mode == Mode.Saving)
        {
            if (newLabel != null)
                NativeWindow.SetText(newLabel, "New Save");
        }
        else
        {
            Restore(newLabel);
            newSlot.gameObject.SetActive(false);
            __instance.GetComponent<GamepadNavigationController>().ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
        }
        ((RectTransform)__instance.transform).RefreshContentFitter();
    }

    // The new-game entry's own words, in the game's language.
    private static void Restore(TMP_Text label)
    {
        if (label == null || !label.TryGetComponent<LocalizedLabel>(out var localized) || !localized.IgnoreLocalize)
            return;
        localized.IgnoreLocalize = false;
        localized.Localize();
    }

    [HarmonyPrefix]
    [HarmonyPatch("OnSaveSlotSelectedHandler")]
    private static bool Selected(UISaveSlotsWindow __instance, SaveSlotData slotData)
    {
        if (mode == Mode.None || !__instance.IsShownAndTop)
            return true;
        if (mode == Mode.Loading)
        {
            // The game's own handler closes the list and loads the save.
            mode = Mode.None;
            return true;
        }
        if (slotData == null)
            SaveNameWindow.Open(SuggestedName(), name => Finish(__instance, done => ManualSave.SaveAs(name, done)));
        else
            Finish(__instance, done => ManualSave.SaveOver(slotData, done));
        return false;
    }

    // A written save returns to the pause menu; one that could not be written says so over the list.
    private static void Finish(UISaveSlotsWindow window, Action<Action<bool>> write) => write(saved =>
    {
        if (!saved)
        {
            var dialog = LazyUI.GetWindow<UIDialogWindow>();
            dialog.Open(new UIDialogWindowData("Save Game", "The game could not be saved.",
                new UIDialogWindowData.ButtonData(dialog.Close, LLBase.L("btn_ok"), keyToReplace: GameKey.Back)));
            return;
        }
        mode = Mode.None;
        window.CloseWithoutCallback();
        LazyUI.GetWindow<UIGamePauseWindow>().Open(null);
    });

    // As GYK1 named a new save: the time and date it is made, marked as a manual save.
    internal static string SuggestedName() => DateTime.Now.ToString("HH:mm, dd MMM yyyy", CultureInfo.InvariantCulture) + " - Manual";

    [HarmonyPrefix]
    [HarmonyPatch("ReturnToPreviousWindow")]
    private static bool Back()
    {
        if (mode == Mode.None)
            return true;
        mode = Mode.None;
        LazyUI.GetWindow<UIGamePauseWindow>().Open(null);
        return false;
    }
}
