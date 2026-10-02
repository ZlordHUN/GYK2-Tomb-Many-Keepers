using HarmonyLib;

namespace GYK2.TombManyKeepers.Patches.Saves;

// Load Game from the pause menu, the host's load that takes its players along and a joined player's following it load
// a save while a game runs, which the game itself never does: it loads a save from the main menu, after leaving its
// game has unloaded the scenes. Loading into the scenes still loaded kept the world they showed, so what the game had
// taken since the save was made, such as mushrooms picked after it, stayed gone. A save loaded while a game runs now
// first leaves the game as the game leaves for its main menu, without opening the menu or ending a multiplayer
// session, and the save's world loads into scenes loaded afresh.
[HarmonyPatch]
internal static class InGameLoadPatches
{
    // The game is left for a save loading in its place, not for the main menu.
    internal static bool Reloading { get; private set; }

    // After the host's own preparation for its load, which asks whether a game runs.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.ContinueGame))]
    private static void Continuing(MainGame __instance)
    {
        if (__instance.gameState != MainGame.GameState.InGame)
            return;
        Reloading = true;
        try
        {
            // The loading screen already covers the game, so it leaves without the fade; the game's own leaving runs
            // up to its menu without waiting.
            __instance.GoToMenu(skipFadeIn: true);
        }
        finally
        {
            Reloading = false;
        }
    }

    // The menu the game returns to on its way stays closed.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIMainMenuWindow), nameof(UIMainMenuWindow.Open))]
    private static bool OpeningMenu() => !Reloading;
}
