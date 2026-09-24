using HarmonyLib;
using GYK2.TombManyKeepers.Features.MultiplayerKeepers;
using GYK2.TombManyKeepers.UI.Mods;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
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
            button.onClick.AddListener(() =>
            {
                KeeperSpawn.Enable();
                __instance.OnStartNewGameButtonClicked();
            });
            button.gameObject.SetActive(true);

            // Open() must no longer treat this button as the console save-slot entry.
            ___consolesGameButton = null;
        }

        var parent = ___gameSettingsButton.transform.parent;
        if (parent.Find("Mods") != null)
            return;
        var mods = Object.Instantiate(___gameSettingsButton, parent);
        mods.transform.SetSiblingIndex(___gameSettingsButton.transform.GetSiblingIndex() + 1);
        // This menu has no ID registry; the button uses direct callbacks.
        mods.LazyUIElementId = string.Empty;
        SetLabel(mods, "Mods");
        mods.onClick = new Button.ButtonClickedEvent();
        mods.onClick.AddListener(() => ModsWindow.Open(__instance));
        mods.SetCallbacksIntoGamepadNavigationItem();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenuWindow.Open))]
    private static void RefreshButtonStyle(LazyButton ___gameSettingsButton)
    {
        // Child text-style components apply their own style during activation.
        var mods = ___gameSettingsButton.transform.parent.Find("Mods");
        if (mods != null)
            mods.GetComponent<LazyButton>().SetKeepPressed(false);
    }

    private static void SetLabel(LazyButton button, string text)
    {
        var label = button.GetComponentInChildren<LocalizedLabel>(true);
        label.IgnoreLocalize = true;
        label.GetComponent<TMP_Text>().text = text;
        button.name = text;
    }
}
