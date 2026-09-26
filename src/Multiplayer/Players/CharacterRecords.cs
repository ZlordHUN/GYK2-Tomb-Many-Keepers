using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using Sirenix.Serialization;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Players;

// Every player's keeper belongs to the campaign. The host's save keeps each joined player's
// character in its native client list, found by the player's identity, so the world and the
// characters are saved together and a returning player gets exactly their character back. The
// native save keeps talents and buffs once per game rather than per character, so each joined
// character's own are kept in a file beside the host's save.
[HarmonyPatch]
internal static class CharacterRecords
{
    private const string ExtrasExtension = ".tmk";
    private const float ShareInterval = 5f;
    // Talents and buffs of each joined character, by character id.
    private static readonly Dictionary<string, (byte[] talents, byte[] perks)> extras =
        new Dictionary<string, (byte[], byte[])>();
    // Shackles a keeper still wore when its player left this session.
    private static readonly Dictionary<string, int> leftChained = new Dictionary<string, int>();
    private static float nextShare;
    private static string sharedHash;

    // Host: the campaign's character for a player who played it before.
    internal static NetworkPlayer Find(string key)
    {
        var id = PlayerIdentity.CharacterId(key);
        return MainGame.Instance.GameSave.clientPlayers.Find(player => player.playerData != null && player.playerData.Guid == id);
    }

    // Host: a first-time player's keeper, with the game's own starting state.
    internal static NetworkPlayer Create(string key, int slot)
    {
        var record = MainGame.Instance.GameSave.CreateClient(slot);
        record.playerData.Guid.SetGuid(PlayerIdentity.CharacterId(key));
        record.playerData.ApplyCustomization(PlayerSkinHelper.playerStandardCustomizationData);
        return record;
    }

    // Host: a character's talents and buffs, empty for a character new to the campaign.
    internal static (byte[] talents, byte[] perks) Extras(string characterId) =>
        extras.TryGetValue(characterId, out var own) ? own : (Array.Empty<byte>(), Array.Empty<byte>());

    // Host: a joined player's character as their game last reported it.
    internal static void Store(string key, int slot, BinaryReader reader)
    {
        var record = Find(key);
        var reported = SerializationUtility.DeserializeValue<PlayerData>(ReadBlock(reader), DataFormat.Binary);
        if (record == null || reported == null || reported.Guid != record.playerData.Guid)
            return;
        record.playerData = reported;
        record.clientId = slot;
        extras[reported.Guid.Id] = (ReadBlock(reader), ReadBlock(reader));
    }

    // Host: the extras of a saved campaign hosted this session.
    internal static void LoadCampaign(SaveSlotData slot)
    {
        extras.Clear();
        string path = ExtrasPath(slot);
        if (!File.Exists(path))
            return;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            for (int count = reader.ReadInt32(); count > 0; count--)
                extras[reader.ReadString()] = (ReadBlock(reader), ReadBlock(reader));
        }
        catch (Exception exception) when (exception is IOException || exception is EndOfStreamException)
        {
            Debug.LogWarning("[Multiplayer] Could not read the joined characters' talents and buffs: " + exception.Message);
        }
    }

    internal static void RememberChained(string key, int shackles)
    {
        if (shackles > 0)
            leftChained[key] = shackles;
        else
            leftChained.Remove(key);
    }

    internal static int ChainedWhenLeft(string key) => leftChained.TryGetValue(key, out int shackles) ? shackles : 0;

    // Joined player: this keeper as it is now, when due and changed, or always when leaving.
    internal static void Report(BinaryWriter writer, out bool due, bool leaving)
    {
        due = false;
        if (!leaving && Time.unscaledTime < nextShare)
            return;
        nextShare = Time.unscaledTime + ShareInterval;
        var save = MainGame.Instance.GameSave;
        byte[] player = SerializationUtility.SerializeValue(save.playerData, DataFormat.Binary);
        byte[] talents = SerializationUtility.SerializeValue(save.talentSystemData, DataFormat.Binary);
        byte[] perks = SerializationUtility.SerializeValue(save.perkSystemData, DataFormat.Binary);
        string hash;
        using (var md5 = MD5.Create())
        {
            md5.TransformBlock(player, 0, player.Length, null, 0);
            md5.TransformBlock(talents, 0, talents.Length, null, 0);
            md5.TransformFinalBlock(perks, 0, perks.Length);
            hash = Convert.ToBase64String(md5.Hash);
        }
        if (!leaving && hash == sharedHash)
            return;
        sharedHash = hash;
        WriteBlock(writer, player);
        WriteBlock(writer, talents);
        WriteBlock(writer, perks);
        due = true;
    }

    // Joined player: installs its own talents and buffs, or the game's defaults for a new character.
    internal static void Install(GameSave save, byte[] talents, byte[] perks)
    {
        save.talentSystemData = talents.Length > 0
            ? SerializationUtility.DeserializeValue<TalentSystemData>(talents, DataFormat.Binary)
            : new TalentSystemData(GameBalance.Me.talentDefs);
        save.perkSystemData = perks.Length > 0
            ? SerializationUtility.DeserializeValue<PerkSystemData>(perks, DataFormat.Binary)
            : new PerkSystemData();
    }

    internal static void WriteBlock(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    internal static byte[] ReadBlock(BinaryReader reader) => reader.ReadBytes(reader.ReadInt32());

    internal static void Clear()
    {
        extras.Clear();
        leftChained.Clear();
        sharedHash = null;
        nextShare = 0f;
    }

    private static string ExtrasPath(SaveSlotData slot) => SaveSystem.SaveFolder + slot.slotName + ExtrasExtension;

    // Only the host saves the campaign; its save holds the joined players' characters.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Save))]
    private static bool SaveOnHost() => !CoopSession.IsGuest;

    // The joined characters' talents and buffs are saved with the host's campaign.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Save))]
    private static void SaveExtras(SaveSlotData slotData)
    {
        if (!CoopSession.IsHosting || slotData == null)
            return;
        try
        {
            using var writer = new BinaryWriter(File.Create(ExtrasPath(slotData)));
            writer.Write(extras.Count);
            foreach (var pair in extras)
            {
                writer.Write(pair.Key);
                WriteBlock(writer, pair.Value.talents);
                WriteBlock(writer, pair.Value.perks);
            }
        }
        catch (IOException exception)
        {
            Debug.LogWarning("[Multiplayer] Could not save the joined characters' talents and buffs: " + exception.Message);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Remove))]
    private static void RemoveExtras(SaveSlotData slotData, bool __result)
    {
        if (!__result || slotData == null)
            return;
        try
        {
            File.Delete(ExtrasPath(slotData));
        }
        catch (IOException)
        {
            // A save without joined characters has no file to remove.
        }
    }
}
