using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The host picks the campaign to host from the native save list: a new game or a saved one.
[HarmonyPatch(typeof(UISaveSlotsWindow))]
internal static class CampaignPicker
{
    private static UIMainMenuWindow menu;
    private static bool picking;

    internal static void Open(UIMainMenuWindow mainMenu)
    {
        menu = mainMenu;
        picking = true;
        mainMenu.Close();
        LazyUI.GetWindow<UISaveSlotsWindow>().Open(null);
    }

    [HarmonyPrefix]
    [HarmonyPatch("OnSaveSlotSelectedHandler")]
    private static bool Picked(UISaveSlotsWindow __instance, SaveSlotData slotData)
    {
        if (!picking || !__instance.IsShownAndTop)
            return true;
        picking = false;
        __instance.Close();
        MultiplayerMenu.Host(menu, slotData);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("ReturnToPreviousWindow")]
    private static bool Back()
    {
        if (!picking)
            return true;
        picking = false;
        menu.Open(null);
        return false;
    }
}
