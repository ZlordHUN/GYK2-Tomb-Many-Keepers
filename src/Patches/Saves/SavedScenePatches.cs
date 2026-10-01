using System.Collections.Generic;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Patches.Saves;

// A save made away from a bed, as Save Game makes one anywhere, can leave its keeper in a scene other than the world's
// entry scene, such as the prison. The game continues a save by loading only the entry scene, since a bed's save is
// always made there, and gives it a bedroom's lighting: such a keeper woke in the void, alone or hosting. Continuing
// now loads the keeper's own scene after the entry scene, which makes it the keeper's, with the opening's other scene
// when it is one of the opening's, as a new game loads them; and the save keeps the lighting it was made in, which for
// a bed's save is the bedroom's. A joined player's game loads the host's scenes its own way.
[HarmonyPatch(typeof(MainGame))]
internal static class SavedScenePatches
{
    private static readonly AccessTools.FieldRef<MainGame, bool> Continuing =
        AccessTools.FieldRefAccess<MainGame, bool>("continueGame");
    // The keeper's scene, and the save's lighting, while a continued save loads.
    private static string scene, preset;

    [HarmonyPrefix]
    [HarmonyPatch("LoadGameScene")]
    private static void Loading(MainGame __instance, ref bool isNewGame)
    {
        scene = preset = null;
        if (isNewGame || !Continuing(__instance) || WorldSnapshot.IsLoading)
            return;
        var save = __instance.GameSave;
        preset = save?.environmentData?.timeOfDayPresetName;
        string saved = save?.playerData?.currentGameSceneId;
        if (string.IsNullOrEmpty(saved) || saved == MainGame.EntrySceneToLoad)
            return;
        scene = saved;
        // The native loader adds its first quest scenes after the entry scene.
        isNewGame = true;
    }

    // The scene started last becomes the keeper's, so the keeper's own loads last.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MainGame.FirstQuestSceneIds), MethodType.Getter)]
    private static void Scenes(ref HashSet<string> __result)
    {
        if (scene == null)
            return;
        var scenes = new HashSet<string>();
        if (__result.Contains(scene))
        {
            foreach (string opening in __result)
            {
                if (opening != scene)
                    scenes.Add(opening);
            }
        }
        scenes.Add(scene);
        __result = scenes;
    }

    // The game lights a continued save as a bedroom before its first wait; the save's own lighting follows.
    [HarmonyPostfix]
    [HarmonyPatch("AfterSceneHasLoaded")]
    private static void Loaded()
    {
        if (!string.IsNullOrEmpty(preset))
            EnvironmentEngine.Instance.SetTimeOfDayPreset(preset);
        scene = preset = null;
    }
}
