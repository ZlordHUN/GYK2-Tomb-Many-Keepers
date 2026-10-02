using System.Collections.Generic;
using GYK2.TombManyKeepers.Network.Session;
using GYK2.TombManyKeepers.UI.Multiplayer;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Patches.Saves;

// Multiplayer campaigns and single-player ones stay apart for now, as the owner asked: a multiplayer save loaded alone
// left its keeper in the void. A campaign ever hosted, whose host settings lie beside its save, lists and loads only in
// Host Game and a multiplayer game's pause menu; every other save only outside multiplayer, where the main menu's
// Continue loads the latest of them and its Continue and Load Game show only while there is one. It holds also while
// the mod's multiplayer or manual saves are switched off. A card that only shows a save, such as the lobby's world in
// a joined player's game, which has no file of it, keeps the look of a save that can be loaded.
[HarmonyPatch(typeof(UISaveSlotsWindow))]
internal static class SaveKindPatches
{
    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, List<UISaveSlot>> Showing =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, List<UISaveSlot>>("showingSlotsElements");
    private static readonly AccessTools.FieldRef<UISaveSlotsWindow, Stack<UISaveSlot>> Pool =
        AccessTools.FieldRefAccess<UISaveSlotsWindow, Stack<UISaveSlot>>("uiSaveSlotsElementPool");

    internal static bool IsMultiplayer(SaveSlotData slot) => HostSettings.IsHosted(slot);

    // A card being drawn to show a save, not to load it.
    internal static bool Displaying { get; set; }

    // Picking a campaign to host, or in a multiplayer game.
    private static bool ForMultiplayer => CampaignPicker.Picking || CoopSession.Current != null;

    // The list takes back the entries of the other kind as the game takes back a deleted save's.
    [HarmonyPostfix]
    [HarmonyPatch("ReadSlotsAndDisplay")]
    private static void Listed(UISaveSlotsWindow __instance)
    {
        bool multiplayer = ForMultiplayer;
        var showing = Showing(__instance);
        for (int i = showing.Count - 1; i >= 0; i--)
        {
            var slot = showing[i];
            if (IsMultiplayer(slot.LinkedSaveSlot) == multiplayer)
                continue;
            slot.gameObject.SetActive(false);
            showing.RemoveAt(i);
            Pool(__instance).Push(slot);
        }
    }

    // The main menu's Continue and Load Game, and the game's loading itself, ask this of every save.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.CanLoadSaveSlot))]
    private static void Loadable(SaveSlotData slotData, ref bool __result)
    {
        if (!Displaying)
            __result &= IsMultiplayer(slotData) == ForMultiplayer;
    }
}
