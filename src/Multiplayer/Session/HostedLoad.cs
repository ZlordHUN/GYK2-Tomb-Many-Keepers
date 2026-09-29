using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;

namespace GYK2.TombManyKeepers.Multiplayer.Session;

// The host's Load Game while others play: the save it picked has been read and the game begins loading it, and the
// players in the host's game load it with the host, as they would a saved campaign started from the lobby.
[HarmonyPatch]
internal static class HostedLoad
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.ContinueGame))]
    private static void Load(SaveSlotData saveSlotData)
    {
        var session = CoopSession.Current;
        // The lobby's own start continues its campaign from the main menu.
        if (session != null && session.IsHost && !session.InLobby && MainGame.Instance.gameState == MainGame.GameState.InGame)
            session.LoadCampaign(saveSlotData);
    }
}
