using GYK2.TombManyKeepers.Patches.UI;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The main menu's Multiplayer button: the scene's console save-slot button, hidden in this PC build, under the
// Multiplayer name, with the multiplayer screen's Host Game, Join Game and Back after it. It comes with multiplayer.
[HarmonyPatch(typeof(UIMainMenuWindow))]
internal static class MultiplayerButton
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenuWindow.Init))]
    private static void Add(UIMainMenuWindow __instance, ref LazyButton ___consolesGameButton, LazyButton ___gameSettingsButton)
    {
        var button = ___consolesGameButton;
        if (button == null || SaveSystem.IsLimitedSaveSlotsEnabled)
            return;
        MainMenuPatches.SetLabel(button, "Multiplayer");
        button.onClick.RemoveListener(__instance.OnConsolesGameButtonClicked);
        button.onClick.AddListener(() => MultiplayerMenu.Show(__instance, true));
        button.gameObject.SetActive(true);
        var host = MainMenuPatches.AddButton(___gameSettingsButton, button, "Host Game", () => MultiplayerMenu.Host(__instance));
        var join = MainMenuPatches.AddButton(___gameSettingsButton, host, "Join Game", () => MultiplayerMenu.Join(__instance));
        MultiplayerMenu.Init(__instance, host, join,
            MainMenuPatches.AddButton(___gameSettingsButton, join, "Back", () => MultiplayerMenu.Show(__instance, false)));
        // Open() must no longer treat this button as the console save-slot entry.
        ___consolesGameButton = null;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMainMenuWindow.Open))]
    private static void Refresh(UIMainMenuWindow __instance) => MultiplayerMenu.Refresh(__instance);
}
