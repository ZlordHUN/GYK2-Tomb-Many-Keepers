using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using GYK2.TombManyKeepers.Features.MultiplayerKeepers;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.Presentation;
using GYK2.TombManyKeepers.Multiplayer.Progression;
using GYK2.TombManyKeepers.Multiplayer.Session;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using Pathfinding;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// A joined player loads the host's running game instead of starting the story itself: the
// host's save as it is now, with the scenes the host has loaded and the player's own keeper
// from the campaign, placed by the host.
[HarmonyPatch]
internal static class WorldSnapshot
{
    // Far above a late-game save; a larger announcement is refused before allocating.
    private const int MaxSize = 64 * 1024 * 1024;
    private static readonly OdinBinaryFileSerializer Serializer = new OdinBinaryFileSerializer(".dat");
    private static Plan loading;

    internal static bool IsLoading => loading != null;

    // The host's game is captured between scene loads once it runs; a starting party's copy is
    // taken while every loading screen still waits.
    internal static bool CanCapture => MainGame.Instance.gameState == MainGame.GameState.InGame && KeeperSpawn.Active &&
        (!LazyUI.Get<UILoadingOverlay>().IsShown || EntryBarrier.Waiting);

    // Where a player's keeper starts. A player new to the campaign waits chained in their bay while
    // the host is still chained, or arrives beside the host; a returning player resumes exactly
    // where they left, still chained if they left chained while the bays are loaded. Players in a
    // new campaign's lobby all wake chained. Either way the keeper has the campaign's standing with
    // the townsfolk.
    internal static Placement Place(string key, int slot, bool startsChained)
    {
        var record = CharacterRecords.Find(key);
        bool returning = record != null;
        if (!returning)
            record = CharacterRecords.Create(key, slot);
        record.clientId = slot;
        SharedKnowledge.CopyStanding(record.playerData);
        int shackles = startsChained ? 2
            : returning ? KeeperSpawn.BaysOpen ? CharacterRecords.ChainedWhenLeft(key) : 0
            : (KeeperSpawn.BaysOpen || KeeperSpawn.BaysAhead) && RemoteKeeper.LocalShackles() > 0 ? 2 : 0;
        var player = record.playerData;
        var placement = new Placement
        {
            CharacterId = player.Guid.Id,
            Shackles = shackles,
            Barrier = EntryBarrier.Waiting,
            Scene = player.currentGameSceneId,
            Position = player.position.Value
        };
        if (shackles > 0 && KeeperSpawn.BaysOpen)
        {
            placement.Scene = KeeperSpawn.BayScene;
            placement.Position = KeeperSpawn.Bay(slot);
        }
        else if (shackles > 0 || !returning)
        {
            // A new campaign's bays open with its story; until then a chained keeper waits at the host.
            placement.Scene = MainGame.PlayerData.currentGameSceneId;
            placement.Position = shackles > 0 ? MainGame.PlayerController.PhysicalBody.transform.position : BesideHost();
        }
        player.position.Value = placement.Position;
        player.currentGameSceneId = placement.Scene;
        return placement;
    }

    // The native save format, compressed for the network; empty if serialization failed.
    internal static byte[] Capture()
    {
        var save = MainGame.Instance.GameSave;
        save.OnBeforeSerialize();
        byte[] bytes = Serializer.Serialize(save);
        if (bytes.Length == 0)
            return bytes;
        var stream = new MemoryStream();
        using (var deflate = new DeflateStream(stream, System.IO.Compression.CompressionLevel.Fastest, true))
            deflate.Write(bytes, 0, bytes.Length);
        return stream.ToArray();
    }

    internal static void WritePlan(BinaryWriter writer, Placement placement, int size)
    {
        writer.Write(size);
        var scenes = LazySingleton<GameSceneManager>.Instance.LoadedGameSceneIds;
        writer.Write((byte)scenes.Count);
        foreach (var scene in scenes)
            writer.Write(scene);
        writer.Write(placement.CharacterId);
        var (talents, perks) = CharacterRecords.Extras(placement.CharacterId);
        CharacterRecords.WriteBlock(writer, talents);
        CharacterRecords.WriteBlock(writer, perks);
        writer.Write((byte)placement.Shackles);
        writer.Write(placement.Barrier);
        writer.Write(placement.Scene);
        Write(writer, placement.Position);
        writer.Write(KeeperSpawn.BaysOpen);
        if (KeeperSpawn.BaysOpen)
            WriteBays(writer);
        writer.Write(WatchedCutscene.DarkenedByStory);
    }

    // The host's bays: where they are and how many shackles each keeper still wears.
    internal static void WriteBays(BinaryWriter writer)
    {
        Write(writer, KeeperSpawn.Wake);
        Write(writer, KeeperSpawn.ChainAnchor);
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
            writer.Write((byte)Shackles(slot));
    }

    internal static void Load(Download download)
    {
        var plan = download.Plan;
        GameSave save;
        using (var deflate = new DeflateStream(new MemoryStream(download.Data), CompressionMode.Decompress))
        using (var raw = new MemoryStream())
        {
            deflate.CopyTo(raw);
            save = Serializer.Deserialize<GameSave>(raw.ToArray());
        }
        // This player's own keeper in the campaign, placed where the host decided.
        var player = save.clientPlayers.Find(client => client.playerData != null && client.playerData.Guid.Id == plan.CharacterId)?.playerData;
        if (player == null)
        {
            player = PlayerData.CreatePlayerData();
            player.Guid.SetGuid(new SGuid(plan.CharacterId));
            player.TryApplyStartState();
            player.ApplyCustomization(PlayerSkinHelper.playerStandardCustomizationData);
        }
        player.position.Value = plan.Position;
        player.currentGameSceneId = plan.Scene;
        save.playerData = player;
        CharacterRecords.Install(save, plan.Talents, plan.Perks);
        // Loading a save afterwards restores the time of day of the bedroom it was made in.
        plan.Preset = save.environmentData.timeOfDayPresetName;
        loading = plan;
        if (plan.Barrier)
            EntryBarrier.Expect();
        MainGame.OnGameStarted += Enter;
        Debug.Log($"[Multiplayer] Loading the host's game: {download.Data.Length} bytes, scenes {string.Join(", ", plan.Scenes)}");
        MainGame.Instance.ContinueGame(new SaveSlotData { slotName = "multiplayer" }, save);
        EntryStatus.LoadGame();
    }

    // The host's bays opened with its story after this game entered.
    internal static void OpenHostBays(BinaryReader reader)
    {
        var wake = ReadVector(reader);
        var anchor = ReadVector(reader);
        var shackles = ReadShackles(reader);
        if (KeeperSpawn.BaysOpen)
            return;
        OpenBays(wake, anchor, shackles);
        if (!KeeperSpawn.LocalChainedPending)
            return;
        KeeperSpawn.LocalChainedPending = false;
        BindChained(shackles[CoopSession.Current.LocalSlot - 1]);
    }

    private static void Enter()
    {
        MainGame.OnGameStarted -= Enter;
        var plan = loading;
        loading = null;
        // The host left while this game loaded; it returns to the menu instead.
        if (!CoopSession.IsGuest)
            return;
        SceneLighting.Adopt(plan.Preset);
        WatchedCutscene.Adopt(CoopSession.HostSlot, plan.Dark);
        KeeperSpawn.Activate();
        if (plan.Bays)
        {
            OpenBays(plan.Wake, plan.Anchor, plan.Shackles);
            if (plan.OwnShackles > 0)
                BindChained(plan.OwnShackles);
        }
        else if (plan.OwnShackles > 0)
        {
            // The host's story places the bays once everyone has loaded.
            KeeperSpawn.LocalChainedPending = true;
        }
        CoopSession.Current.Entered(plan.Barrier);
    }

    private static void OpenBays(Vector3 wake, Vector3 anchor, int[] shackles)
    {
        KeeperSpawn.OpenBays(wake, anchor);
        for (int slot = 1; slot <= shackles.Length; slot++)
        {
            if (slot == CoopSession.Current.LocalSlot)
                continue;
            var chains = KeeperSpawn.Keeper(slot);
            if (chains != null)
                chains.Restore(shackles[slot - 1]);
        }
    }

    private static void BindChained(int shackles)
    {
        var local = KeeperSpawn.BindLocal();
        local.Restore(shackles);
        ChainedPlayer.Attach(local, KeeperSpawn.Anchor(CoopSession.Current.LocalSlot));
    }

    // Beside the host on ground its scene's keepers can walk, clear of walls.
    private static Vector3 BesideHost()
    {
        var host = MainGame.PlayerController;
        var position = host.PhysicalBody.transform.position + Vector3.left;
        var graph = host.SceneRecastGraph;
        if (graph != null)
        {
            var walkable = NearestNodeConstraint.Walkable;
            walkable.distanceMetric = DistanceMetric.ClosestAsSeenFromAbove();
            var nearest = graph.GetNearest(position, walkable);
            if (nearest.node != null)
                position = nearest.position;
        }
        return RaycastUtils.TrySnapToTheGround(position, 1f, 10f);
    }

    private static int Shackles(int slot)
    {
        if (slot == CoopSession.Current.LocalSlot)
            return RemoteKeeper.LocalShackles();
        var chains = KeeperSpawn.Keeper(slot);
        return chains == null || chains.IsFree ? 0 : chains.RemainingShackles;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainGame), "LoadGameScene")]
    private static void LoadHostScenes(ref bool isNewGame)
    {
        // The native loader adds the opening's scenes after the entry scene; a joined game adds the host's.
        if (loading != null)
            isNewGame = HostScenes().Count > 0;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.FirstQuestSceneIds), MethodType.Getter)]
    private static void UseHostScenes(ref HashSet<string> __result)
    {
        if (loading != null)
            __result = HostScenes();
    }

    // The scene started last becomes the player's, so the keeper's own scene loads last.
    private static HashSet<string> HostScenes()
    {
        var scenes = new HashSet<string>();
        if (loading.Scene == MainGame.EntrySceneToLoad)
            return scenes;
        foreach (var scene in loading.Scenes)
        {
            if (scene != MainGame.EntrySceneToLoad && scene != loading.Scene)
                scenes.Add(scene);
        }
        scenes.Add(loading.Scene);
        return scenes;
    }

    private static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    private static Vector3 ReadVector(BinaryReader reader) =>
        new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static int[] ReadShackles(BinaryReader reader)
    {
        var shackles = new int[CoopSession.MaxPlayers];
        for (int i = 0; i < shackles.Length; i++)
            shackles[i] = reader.ReadByte();
        return shackles;
    }

    // Where the host placed a player's keeper, decided as the player's copy is captured.
    internal sealed class Placement
    {
        internal string CharacterId;
        internal int Shackles;
        internal bool Barrier;
        internal string Scene;
        internal Vector3 Position;
    }

    internal sealed class Plan
    {
        internal string[] Scenes;
        internal string CharacterId;
        internal byte[] Talents;
        internal byte[] Perks;
        internal int OwnShackles;
        internal bool Barrier;
        internal string Scene;
        internal Vector3 Position;
        internal bool Bays;
        internal Vector3 Wake;
        internal Vector3 Anchor;
        internal int[] Shackles;
        internal string Preset;
        // The host's story keeps its screen dark, as a new campaign's opening does before it reveals the
        // prison with everyone in their bays.
        internal bool Dark;
    }

    // The host's game as it arrives in parts.
    internal sealed class Download
    {
        private int received;

        internal Plan Plan { get; private set; }
        internal byte[] Data { get; private set; }
        internal bool Complete => received == Data.Length;
        internal float Progress => (float)received / Data.Length;

        // Null when the announcement is not a game this player can load.
        internal static Download Begin(BinaryReader reader)
        {
            int size = reader.ReadInt32();
            if (size <= 0 || size > MaxSize)
                return null;
            var plan = new Plan { Scenes = new string[reader.ReadByte()] };
            for (int i = 0; i < plan.Scenes.Length; i++)
                plan.Scenes[i] = reader.ReadString();
            plan.CharacterId = reader.ReadString();
            plan.Talents = CharacterRecords.ReadBlock(reader);
            plan.Perks = CharacterRecords.ReadBlock(reader);
            plan.OwnShackles = reader.ReadByte();
            plan.Barrier = reader.ReadBoolean();
            plan.Scene = reader.ReadString();
            plan.Position = ReadVector(reader);
            plan.Bays = reader.ReadBoolean();
            if (plan.Bays)
            {
                plan.Wake = ReadVector(reader);
                plan.Anchor = ReadVector(reader);
                plan.Shackles = ReadShackles(reader);
            }
            plan.Dark = reader.ReadBoolean();
            return new Download { Plan = plan, Data = new byte[size] };
        }

        internal bool Add(byte[] source, int offset, int count)
        {
            if (count > Data.Length - received)
                return false;
            Buffer.BlockCopy(source, offset, Data, received, count);
            received += count;
            return true;
        }
    }
}
