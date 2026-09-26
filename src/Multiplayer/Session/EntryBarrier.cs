using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Session;

// Nobody plays until everyone starting together has loaded the campaign. Each game keeps its
// loading screen up, its world paused and its keeper still; the host also holds the story's
// start, and its release sets everyone going together.
[HarmonyPatch]
internal static class EntryBarrier
{
    // A start is loading: its loading screen stays and a new story waits.
    private static bool expected;
    // This game has loaded and waits for the release.
    private static bool holding;
    private static bool heldStory;

    internal static bool Waiting => expected || holding;

    internal static void Expect() => expected = true;

    internal static void Hold()
    {
        if (!expected || holding)
            return;
        holding = true;
        MainGame.UpdateManager.IsActive = false;
        MainGame.PlayerController.SetControlTakenType(TakenControlType.ByCinematics, isEnabled: false);
    }

    internal static void Release()
    {
        if (!Waiting)
            return;
        Finish();
        if (heldStory)
        {
            heldStory = false;
            GlobalEventsSystem.FireTrigger(GlobalEventsSystem.Event.Type.StartNewGame);
        }
        LazyUI.Get<UILoadingOverlay>().Hide();
    }

    // Leaving before the release drops the wait without starting anything.
    internal static void Cancel()
    {
        if (!Waiting)
            return;
        Finish();
        heldStory = false;
        var overlay = LazyUI.Get<UILoadingOverlay>();
        if (overlay.IsShown)
            overlay.Hide();
    }

    private static void Finish()
    {
        bool wasHolding = holding;
        expected = holding = false;
        if (!wasHolding)
            return;
        MainGame.UpdateManager.IsActive = true;
        MainGame.PlayerController.SetControlTakenType(TakenControlType.ByCinematics, isEnabled: true);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GlobalEventsSystem), nameof(GlobalEventsSystem.FireTrigger))]
    private static bool HoldStory(GlobalEventsSystem.Event.Type type)
    {
        if (type != GlobalEventsSystem.Event.Type.StartNewGame || !expected)
            return true;
        heldStory = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UILoadingOverlay), nameof(UILoadingOverlay.Hide))]
    private static bool KeepLoadingScreen() => !Waiting;
}
