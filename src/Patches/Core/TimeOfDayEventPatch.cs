using System.Reflection;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Patches.Core;

// The game sets a save's clock first as it prepares the save to play, before its quests and world are ready. A story
// script's time-of-day event that started under the clock the previous game left fires at that jump if the jump passes
// its time: a new game begun after leaving a game in the first hundredth of its day threw in the goddess tears
// script's quest check, and its start went no further. While a save is prepared, the event takes the clock as the time
// it starts at, as an event starting then does, and fires once play passes its time.
[HarmonyPatch]
internal static class TimeOfDayEventPatch
{
    private static bool preparing;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.PrepareForGame))]
    private static void Preparing() => preparing = true;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.PrepareForGame))]
    private static void Prepared() => preparing = false;

    // The event node lives in the story scripts' assembly, which the mod does not build against.
    [HarmonyPatch]
    private static class Check
    {
        private static FieldInfo called, timeToCall;
        private static PropertyInfo value;

        private static MethodBase TargetMethod()
        {
            var node = AccessTools.TypeByName("Flow_OnTimeOfDayEvent");
            called = AccessTools.Field(node, "called");
            timeToCall = AccessTools.Field(node, "timeToCall");
            value = AccessTools.DeclaredProperty(timeToCall.FieldType, "value");
            return AccessTools.Method(node, "CheckCall");
        }

        private static bool Prefix(object __instance, float currentTime, bool isFake)
        {
            if (!preparing || isFake)
                return true;
            called.SetValue(__instance, currentTime >= (float)value.GetValue(timeToCall.GetValue(__instance)));
            return false;
        }
    }
}
