using System;
using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Players;

// Each player's colour, as GYK1's player identification colours: one of eight hues, the bright tone for their name in
// chat and the same hue darker for the outline around their white name tag. The host gives every player a colour as
// they arrive: the one the campaign keeps for them unless a player present has it, otherwise a free one at random,
// preferring colours the campaign keeps for no one. The campaign keeps them beside its save, so a returning player
// keeps their colour in it.
[HarmonyPatch]
internal static class PlayerColors
{
    internal const int None = -1;
    private const string Extension = ".tmkcolors";
    private const byte Format = 1;
    // GYK1's palette in its order: red, orange, lime, green, cyan, blue, purple and pink.
    private static readonly Color32[] Bright =
    {
        new Color32(230, 70, 70, 255), new Color32(240, 145, 40, 255), new Color32(155, 220, 60, 255), new Color32(70, 200, 80, 255),
        new Color32(60, 200, 215, 255), new Color32(90, 140, 250, 255), new Color32(175, 105, 245, 255), new Color32(240, 90, 180, 255)
    };
    private static readonly Color32[] Outline =
    {
        new Color32(128, 35, 35, 255), new Color32(134, 79, 20, 255), new Color32(84, 120, 32, 255), new Color32(38, 110, 44, 255),
        new Color32(30, 110, 120, 255), new Color32(48, 76, 138, 255), new Color32(96, 58, 135, 255), new Color32(132, 50, 100, 255)
    };
    private static readonly System.Random Random = new System.Random();
    // Host: the colour the hosted campaign keeps for each player, by their key in it.
    private static readonly Dictionary<string, int> kept = new Dictionary<string, int>();

    internal static bool Valid(int color) => color >= 0 && color < Bright.Length;

    internal static Color BrightOf(int color) => Bright[color];

    internal static Color OutlineOf(int color) => Outline[color];

    // The bright tone as a text colour tag's value.
    internal static string Hex(int color) => "#" + ColorUtility.ToHtmlStringRGB(Bright[color]);

    // Host: the colour of a player arriving, beside the colours of the players present.
    internal static int Pick(string key, ICollection<int> taken)
    {
        if (kept.TryGetValue(key, out int color) && !taken.Contains(color))
            return color;
        var free = new List<int>();
        var unkept = new List<int>();
        for (int candidate = 0; candidate < Bright.Length; candidate++)
        {
            if (taken.Contains(candidate))
                continue;
            free.Add(candidate);
            if (!kept.ContainsValue(candidate))
                unkept.Add(candidate);
        }
        var from = unkept.Count > 0 ? unkept : free;
        color = from[Random.Next(from.Count)];
        kept[key] = color;
        return color;
    }

    // Host: a player present keeps their colour in the campaign now hosted.
    internal static void Keep(string key, int color)
    {
        if (Valid(color))
            kept[key] = color;
    }

    // Host: the colours of the saved campaign hosted; none for a new one.
    internal static void LoadCampaign(SaveSlotData slot)
    {
        kept.Clear();
        string path = slot == null ? null : PathOf(slot);
        if (path == null || !File.Exists(path))
            return;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadByte() != Format)
                return;
            for (int count = reader.ReadByte(); count > 0; count--)
            {
                string key = reader.ReadString();
                int color = reader.ReadByte();
                if (Valid(color))
                    kept[key] = color;
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Multiplayer] Could not read the players' colours: " + exception.Message);
        }
    }

    internal static void Clear() => kept.Clear();

    // The colours follow the campaign's save; the game's own saving and removing never fail over them.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Save))]
    private static void Remember(SaveSlotData slotData)
    {
        if (!CoopSession.IsHosting || slotData == null)
            return;
        try
        {
            using var writer = new BinaryWriter(File.Create(PathOf(slotData)));
            writer.Write(Format);
            int count = Math.Min(kept.Count, byte.MaxValue);
            writer.Write((byte)count);
            foreach (var pair in kept)
            {
                if (count-- == 0)
                    break;
                writer.Write(pair.Key);
                writer.Write((byte)pair.Value);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Multiplayer] Could not save the players' colours: " + exception.Message);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Remove))]
    private static void Forget(SaveSlotData slotData, bool __result)
    {
        if (!__result || slotData == null)
            return;
        try
        {
            // A campaign never hosted has no file, which leaves nothing to remove.
            File.Delete(PathOf(slotData));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Multiplayer] Could not remove the players' colours: " + exception.Message);
        }
    }

    private static string PathOf(SaveSlotData slot) => SaveSystem.SaveFolder + slot.slotName + Extension;
}
