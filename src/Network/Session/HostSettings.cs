using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Session;

// What the host chose on its settings screen: how many keepers the game takes, who may find and join it and
// whether cheats are allowed. Everyone in the lobby sees them, but never the password; a saved campaign starts
// from the ones it was last hosted with.
[HarmonyPatch]
internal sealed class HostSettings
{
    // Public games are listed for everyone, a friends' game for the host's Steam friends, a password game for
    // everyone with a lock; a private game is never listed. An invite lets its friend in to any of them.
    internal enum Access : byte
    {
        Public,
        Friends,
        Private,
        Password
    }

    internal const int FewestPlayers = 2;
    private const string Extension = ".tmkhost";
    // The file's format; the first had no password.
    private const byte Format = 2, FirstFormat = 1;
    internal const int PasswordLength = 32;

    internal int Players = CoopSession.MaxPlayers;
    internal Access Visibility = Access.Public;
    // What joining a password game asks for; kept by the host alone.
    internal string Password = string.Empty;
    // For the cheat commands to come; nothing uses it yet.
    internal bool Cheats;

    internal HostSettings Copy() => (HostSettings)MemberwiseClone();

    internal void Write(BinaryWriter writer)
    {
        writer.Write((byte)Players);
        writer.Write((byte)Visibility);
        writer.Write(Cheats);
    }

    internal static HostSettings Read(BinaryReader reader) => new HostSettings
    {
        Players = Mathf.Clamp(reader.ReadByte(), FewestPlayers, CoopSession.MaxPlayers),
        Visibility = (Access)Mathf.Min(reader.ReadByte(), (int)Access.Password),
        Cheats = reader.ReadBoolean()
    };

    // A new campaign starts from the defaults; a saved one from the settings it was last hosted with.
    internal static HostSettings Of(SaveSlotData campaign)
    {
        string path = campaign == null ? null : PathOf(campaign);
        if (path == null || !File.Exists(path))
            return new HostSettings();
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            byte format = reader.ReadByte();
            if (format != Format && format != FirstFormat)
                return new HostSettings();
            var settings = Read(reader);
            if (format == Format)
                settings.Password = reader.ReadString();
            return settings;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Multiplayer] Could not read the campaign's host settings: " + exception.Message);
            return new HostSettings();
        }
    }

    // The settings file follows the campaign's save; the game's own saving and removing never fail over it.
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
            CoopSession.Current.Settings.Write(writer);
            writer.Write(CoopSession.Current.Settings.Password);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Multiplayer] Could not save the campaign's host settings: " + exception.Message);
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
            Debug.LogWarning("[Multiplayer] Could not remove the campaign's host settings: " + exception.Message);
        }
    }

    private static string PathOf(SaveSlotData slot) => SaveSystem.SaveFolder + slot.slotName + Extension;
}
