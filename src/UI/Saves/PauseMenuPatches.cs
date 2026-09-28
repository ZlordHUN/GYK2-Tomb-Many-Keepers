using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Saves;

// Save Game and Load Game first in the game's pause menu, as GYK1's followed Continue, which this menu does not have:
// copies of its own Settings button, with its art, sounds and gamepad navigation. Save Game opens the game's save list
// to save into and Load Game the list to load from. In a multiplayer game a joined player's Save Game asks the host to
// save the campaign, and nobody loads another save while the game is shared.
[HarmonyPatch(typeof(UIGamePauseWindow))]
internal static class PauseMenuPatches
{
    private const string SaveName = "Save Game", LoadName = "Load Game";

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGamePauseWindow.Init))]
    private static void AddButtons(LazyButton ___settingsBtn)
    {
        if (___settingsBtn == null || ___settingsBtn.transform.parent.Find(SaveName) != null)
            return;
        var load = Copy(___settingsBtn, LoadName, () => SaveList.Open(SaveList.Mode.Loading));
        var save = Copy(___settingsBtn, SaveName, Save);
        save.transform.SetSiblingIndex(___settingsBtn.transform.GetSiblingIndex());
        load.transform.SetSiblingIndex(save.transform.GetSiblingIndex() + 1);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIGamePauseWindow.Open))]
    private static void ShowButtons(UIGamePauseWindow __instance, LazyButton ___settingsBtn)
    {
        var load = ___settingsBtn == null ? null : ___settingsBtn.transform.parent.Find(LoadName);
        if (load == null)
            return;
        bool shared = CoopSession.Current != null;
        if (load.gameObject.activeSelf == !shared)
            return;
        load.gameObject.SetActive(!shared);
        ((RectTransform)__instance.transform).RefreshContentFitter();
        __instance.GetComponent<GamepadNavigationController>().ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
    }

    // A joined player's game holds no campaign to save; the host saves it for everyone.
    private static void Save()
    {
        if (!CoopSession.IsGuest)
        {
            SaveList.Open(SaveList.Mode.Saving);
            return;
        }
        LazyUI.GetWindow<UIGamePauseWindow>().Close();
        CoopSession.RequestSave();
    }

    private static LazyButton Copy(LazyButton template, string label, UnityAction pressed)
    {
        var button = Object.Instantiate(template, template.transform.parent);
        button.name = label;
        // The menu finds its buttons by their own references; a copy is known by its callbacks alone.
        button.LazyUIElementId = string.Empty;
        var text = button.GetComponentInChildren<TMP_Text>(true);
        if (text.TryGetComponent<LocalizedLabel>(out var localized))
            localized.IgnoreLocalize = true;
        text.text = label;
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(pressed);
        button.SetCallbacksIntoGamepadNavigationItem();
        return button;
    }
}
