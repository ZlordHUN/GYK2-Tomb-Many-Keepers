using System;
using System.Collections.Generic;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.Presentation;
using GYK2.TombManyKeepers.Network.Session;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// /tp <player>: this player's keeper to another player's, as GYK1's, through the game's own teleport with its fade, into
// their scene where it is another. The keeper arrives lit as that scene was last lit; a scene no game has spoken of is
// lit with this game's guess, which it keeps to itself.
internal static partial class ChatCommands
{
    private static void Teleport(List<string> args, Action<string> respond)
    {
        if (args.Count != 2)
        {
            respond("Usage: /tp <player>");
            return;
        }
        var session = CoopSession.Current;
        int slot = FindPlayer(args[1]);
        if (slot == 0)
        {
            respond($"No player {args[1]} is in the game.");
            return;
        }
        if (slot == session.LocalSlot)
        {
            respond("That is you.");
            return;
        }
        string scene = RemoteKeeper.SceneOf(slot);
        var at = RemoteKeeper.PositionOf(slot);
        if (scene == null || at == null)
        {
            respond($"Where {session.PlayerName(slot)} stands has not reached this game yet.");
            return;
        }
        if (!MainGame.PlayerController.IsControlsEnabled)
        {
            respond("Your keeper cannot teleport while something holds it.");
            return;
        }
        string preset = scene == MainGame.PlayerData.currentGameSceneId ? EnvironmentEngine.Instance.Data.timeOfDayPresetName
            : SceneLighting.PresetOf(scene);
        if (string.IsNullOrEmpty(preset))
        {
            preset = "outdoor";
            SceneLighting.Guess(scene);
        }
        if (!PlayerController.Teleport(new KeeperPlace(scene, at.Value, preset)))
        {
            respond("The game refused the teleport.");
            return;
        }
        respond($"Teleporting to {session.PlayerName(slot)}.");
    }

    // Another player's keeper's place in their scene, as a teleport's destination.
    private sealed class KeeperPlace : TeleportDataBase
    {
        private readonly string scene;
        private readonly Vector3 position;

        internal KeeperPlace(string scene, Vector3 position, string preset) : base(preset)
        {
            this.scene = scene;
            this.position = position;
        }

        public override string GetDestinationId() => scene;

        public override GameSceneData GetDestinationSceneData() => MainGame.WorldData.GetGameSceneDataById(scene);

        public override Vector3 GetPosition() => position;
    }
}
