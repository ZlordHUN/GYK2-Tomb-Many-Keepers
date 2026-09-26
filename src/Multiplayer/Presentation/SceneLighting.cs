using System.IO;
using GYK2.TombManyKeepers.Features.MultiplayerKeepers;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// A scene looks the same to every player in it. The time of day preset a player's game lights its
// scene with during play, from a teleport such as the opening's into the prison or from a story node,
// applies in the other games whose players are in that scene, and so do the light overrides the story
// sets. Each game runs the shared weather, whose light overrides stay its own.
[HarmonyPatch]
internal static class SceneLighting
{
    private static bool weather;
    // The player whose override lights this game; theirs resets it wherever this player has gone.
    private static int overriddenBy;

    // A joined game starts with the lighting of the host's save, which is the host's to share.
    internal static void Adopt(string preset) =>
        SharedPresentation.Mirror(() => EnvironmentEngine.Instance.SetTimeOfDayPreset(preset));

    // Presets chosen while this game loads are its own; a teleport sets its destination's after moving
    // the keeper there.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnvironmentEngine), nameof(EnvironmentEngine.SetTimeOfDayPreset))]
    private static void Lit(string presetName)
    {
        if (SharedPresentation.Applying || !KeeperSpawn.Active || !CoopSession.SharesWorld || string.IsNullOrEmpty(presetName))
            return;
        string scene = MainGame.PlayerData.currentGameSceneId ?? string.Empty;
        SharedPresentation.Send(SharedPresentation.Cue.Lighting, writer =>
        {
            writer.Write(scene);
            writer.Write(presetName);
        });
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CPLightEnvironmentPreset), nameof(CPLightEnvironmentPreset.UpdateParameter))]
    private static void WeatherLights() => weather = true;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(CPLightEnvironmentPreset), nameof(CPLightEnvironmentPreset.UpdateParameter))]
    private static void WeatherLit() => weather = false;

    // This game's own override replaces a shared one; the story's own is shared.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnvironmentEngine), nameof(EnvironmentEngine.ApplyOverridePreset), typeof(LightEnvironmentPreset), typeof(float))]
    private static void Overridden(LightEnvironmentPreset preset, float intensity)
    {
        if (SharedPresentation.Applying)
            return;
        overriddenBy = 0;
        if (weather || !CoopSession.SharesWorld)
            return;
        string scene = MainGame.PlayerData.currentGameSceneId ?? string.Empty;
        string name = preset != null ? preset.name : string.Empty;
        SharedPresentation.Send(SharedPresentation.Cue.LightOverride, writer =>
        {
            writer.Write(scene);
            writer.Write(name);
            writer.Write(intensity);
        });
    }

    internal static void Apply(int slot, SharedPresentation.Cue cue, BinaryReader reader)
    {
        string scene = reader.ReadString();
        string preset = reader.ReadString();
        float intensity = cue == SharedPresentation.Cue.LightOverride ? reader.ReadSingle() : 0f;
        bool here = scene == MainGame.PlayerData.currentGameSceneId;
        if (cue == SharedPresentation.Cue.Lighting)
        {
            if (here)
                EnvironmentEngine.Instance.SetTimeOfDayPreset(preset);
            return;
        }
        if (preset.Length == 0)
        {
            if (overriddenBy == slot)
                Reset();
            return;
        }
        if (!here)
            return;
        EnvironmentEngine.Instance.ApplyOverridePreset(preset, intensity);
        overriddenBy = slot;
    }

    // A player who leaves takes their override with them.
    internal static void Forget(int slot)
    {
        if (overriddenBy == slot)
            Reset();
    }

    private static void Reset()
    {
        overriddenBy = 0;
        if (MainGame.Instance.gameState == MainGame.GameState.InGame)
            EnvironmentEngine.Instance.ApplyOverridePreset((LightEnvironmentPreset)null, 0f);
    }
}
