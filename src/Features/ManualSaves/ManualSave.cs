using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.Session;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Features.ManualSaves;

// Saving the running game when the player asks, through the game's own saving and its overlay: as a new save under
// the name the player gives, over a save they pick, or into the save the game plays from. The game goes on saving
// after sleep into the save it plays from; a new save stands apart, to come back to. In a multiplayer game the host
// saves the campaign, which holds every keeper.
internal static class ManualSave
{
    // New saves are named apart from the game's own, which names every new game after the platform and 1.
    private const string NewSlotPart = "_manual_";

    // The game runs, its loading done, and this game keeps the campaign. With manual saves off, a host refuses the
    // saves its joined players ask for as well.
    internal static bool CanSave
    {
        get
        {
            if (!FeatureSwitches.ManualSaves)
                return false;
            var game = MainGame.Instance;
            if (game == null || game.gameState != MainGame.GameState.InGame || game.GameSave == null || game.SaveSlotData == null ||
                CoopSession.IsGuest)
                return false;
            if (EntryBarrier.Waiting || LazyUI.Get<UILoadingOverlay>().IsShown)
                return false;
            return CoopSession.Current == null || KeeperSpawn.Active;
        }
    }

    // Saves the game as a new save with this name; the callback says whether it was written.
    internal static void SaveAs(string name, Action<bool> done)
    {
        var slot = new SaveSlotData { slotName = NewSlotName() };
        Write(slot, saved =>
        {
            if (saved)
                SaveNames.Keep(slot, name);
            done?.Invoke(saved);
        });
    }

    internal static void SaveOver(SaveSlotData slot, Action<bool> done) => Write(slot, done);

    internal static void SaveCurrent(Action<bool> done) => Write(MainGame.Instance.SaveSlotData, done);

    // The game writes a save at once and answers before it returns.
    private static void Write(SaveSlotData slot, Action<bool> done)
    {
        if (!CanSave || slot == null)
        {
            done?.Invoke(false);
            return;
        }
        bool saved = false;
        SaveSystem.Save(slot, MainGame.Instance.GameSave, () => saved = true);
        UnityEngine.Debug.Log($"[ManualSaves] {(saved ? "Saved" : "Could not save")} the game as {slot.slotName}");
        done?.Invoke(saved);
    }

    // The lowest free number after the platform's name and the manual part, among the saves, their backups and their
    // files.
    private static string NewSlotName()
    {
        string prefix = LazyAPI.Platform.GetPlatformName() + NewSlotPart;
        var taken = new HashSet<string>();
        foreach (var slot in SaveSystem.SaveSlotDataList)
            taken.Add(slot.slotName);
        for (int number = 1; ; number++)
        {
            string name = prefix + number;
            if (!taken.Contains(name) && !File.Exists(SaveSystem.SaveFolder + name + ".info") && !File.Exists(SaveSystem.SaveFolder + name + ".dat"))
                return name;
        }
    }
}
