using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// Each game moves what its own play sets in motion, such as a keeper or townsperson walking in a
// scene that player started, and the others follow the shared motion. What was already moving in
// the host's game when a player joined, and what another player's change sets moving, moves in
// that other game.
[HarmonyPatch]
internal static class LocalMovement
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MovementSystemData), nameof(MovementSystemData.RestoreMovingObjects))]
    private static void LeaveHostMovers(MovementSystemData __instance)
    {
        if (CoopSession.IsGuest)
            __instance.movingObjects.Clear();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MovementSystem), nameof(MovementSystem.AddMovingObject))]
    private static bool MovesHere() => !WorldSync.Applying;
}
