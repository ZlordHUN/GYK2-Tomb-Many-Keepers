using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Sleeping restores a keeper's own energy. Natively it also runs the whole world faster until
// morning; in a shared game one player's sleep leaves the world at its normal pace for everyone
// else, restoring the sleeper as quickly as a night's sleep would, and the night passes quickly
// only while every player in the game sleeps.
[HarmonyPatch]
internal static class RestAgreement
{
    // How much faster the native sleep runs the world.
    private const float SleepPace = 50f;
    private static bool forwarding;
    private static bool resting;

    // Host: the world runs at the sleep's pace while everyone sleeps, and at its own otherwise.
    internal static void FastForward(bool everyone)
    {
        float pace = everyone ? SleepPace : 1f;
        if (Mathf.Approximately(MainGame.UpdateManager.TimeMultiplier, pace))
            return;
        forwarding = true;
        try
        {
            MainGame.UpdateManager.SetTimeSpeedMultiplier(pace);
        }
        finally
        {
            forwarding = false;
        }
    }

    internal static void Clear() => resting = false;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UpdateManager), nameof(UpdateManager.SetTimeSpeedMultiplier))]
    private static bool Pace(float value)
    {
        if (forwarding || !CoopSession.SharesWorld)
            return true;
        bool sleeping = value > 1f;
        if (sleeping != resting)
        {
            resting = sleeping;
            CoopSession.ShareRest(sleeping);
        }
        // Falling asleep leaves the pace to the whole party; waking restores the normal one.
        return !sleeping;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnergySystem), nameof(EnergySystem.UpdateSleepLogic))]
    private static void Restore(EnergySystem __instance, ref float dayDeltaTime)
    {
        if (__instance.IsSleeping && CoopSession.SharesWorld && WorldClock.Pace <= 1f)
            dayDeltaTime *= SleepPace;
    }
}
