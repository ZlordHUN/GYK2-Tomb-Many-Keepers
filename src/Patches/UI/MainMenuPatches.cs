using HarmonyLib;
using GYK2.TombManyKeepers.UI.Mods;
using GYK2.TombManyKeepers.UI.Multiplayer;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.Patches.UI;

[HarmonyPatch(typeof(UIMainMenuWindow))]
internal static class MainMenuPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenuWindow.Init))]
    private static void AddButtons(UIMainMenuWindow __instance, ref LazyButton ___consolesGameButton,
        LazyButton ___gameSettingsButton)
    {
        var button = ___consolesGameButton;
        if (button != null && !SaveSystem.IsLimitedSaveSlotsEnabled)
        {
            // The scene's console save-slot button is hidden in this PC build.
            SetLabel(button, "Multiplayer");
            button.onClick.RemoveListener(__instance.OnConsolesGameButtonClicked);
            button.onClick.AddListener(() => MultiplayerMenu.Show(__instance, true));
            button.gameObject.SetActive(true);
            var host = AddButton(___gameSettingsButton, button, "Host", () => MultiplayerMenu.Host(__instance));
            var join = AddButton(___gameSettingsButton, host, "Join", () => MultiplayerMenu.Join(__instance));
            MultiplayerMenu.Init(host, join,
                AddButton(___gameSettingsButton, join, "Back", () => MultiplayerMenu.Show(__instance, false)));

            // Open() must no longer treat this button as the console save-slot entry.
            ___consolesGameButton = null;
        }

        if (___gameSettingsButton.transform.parent.Find("Mods") == null)
            AddButton(___gameSettingsButton, ___gameSettingsButton, "Mods", () => ModsWindow.Open(__instance));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenuWindow.Open))]
    private static void RefreshButtons(UIMainMenuWindow __instance, LazyButton ___gameSettingsButton)
    {
        // Child text-style components apply their own style during activation.
        var mods = ___gameSettingsButton.transform.parent.Find("Mods");
        if (mods != null)
            mods.GetComponent<LazyButton>().SetKeepPressed(false);
        MultiplayerMenu.Refresh(__instance);
    }

    private static LazyButton AddButton(LazyButton template, LazyButton after, string label, UnityAction onClick)
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

    private static void SetLabel(LazyButton button, string text)
    {
        var label = button.GetComponentInChildren<LocalizedLabel>(true);
        label.IgnoreLocalize = true;
        label.GetComponent<TMP_Text>().text = text;
        button.name = text;
    }
}
