using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Patches.Widescreen.Gameplay;

[HarmonyPatch(typeof(DeformTextureCamera), "GetCameraXY")]
internal static class DeformationCameraPatches
{
    [HarmonyPrefix]
    private static bool ProjectHistoryPosition(Camera ___camera, ref Vector2 __result)
    {
        // Include vertical camera movement and measure offsets in the history texture's pixels.
        var position = ___camera.worldToCameraMatrix.MultiplyVector(___camera.transform.position);
        var projection = ___camera.projectionMatrix;
        var texture = ___camera.targetTexture;
        __result = new Vector2(position.x * projection.m00 * texture.width,
            position.y * projection.m11 * texture.height) * 0.5f;
        return false;
    }
}
