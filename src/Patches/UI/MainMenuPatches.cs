using HarmonyLib;
using GYK2.TombManyKeepers.UI.MainMenu;
using GYK2.TombManyKeepers.UI.Mods;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.Patches.UI;

// The main menu's Mods button after Settings, and the mod's title under the game's logo. Features add their own
// buttons with the same copies of the menu's buttons.
[HarmonyPatch(typeof(UIMainMenuWindow))]
internal static class MainMenuPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenuWindow.Init))]
    private static void AddButtons(UIMainMenuWindow __instance, LazyButton ___gameSettingsButton)
    {
        if (___gameSettingsButton.transform.parent.Find("Mods") == null)
            AddButton(___gameSettingsButton, ___gameSettingsButton, "Mods", () => ModsWindow.Open(__instance));
        ModTitle.Add(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenuWindow.Open))]
    private static void RefreshButtons(LazyButton ___gameSettingsButton)
    {
        // Child text-style components apply their own style during activation.
        var mods = ___gameSettingsButton.transform.parent.Find("Mods");
        if (mods != null)
            mods.GetComponent<LazyButton>().SetKeepPressed(false);
    }

    // A copy of the menu's template button after another, under its own label and with its own click.
    internal static LazyButton AddButton(LazyButton template, LazyButton after, string label, UnityAction onClick)
    {
        var button = Object.Instantiate(template, template.transform.parent);
        button.transform.SetSiblingIndex(after.transform.GetSiblingIndex() + 1);
        // This menu has no ID registry; the button uses direct callbacks.
        button.LazyUIElementId = string.Empty;
        SetLabel(button, label);
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(onClick);
        button.SetCallbacksIntoGamepadNavigationItem();
        return button;
    }

    internal static void SetLabel(LazyButton button, string text)
    {
        var label = button.GetComponentInChildren<LocalizedLabel>(true);
        label.IgnoreLocalize = true;
        label.GetComponent<TMP_Text>().text = text;
        button.name = text;
    }
}
