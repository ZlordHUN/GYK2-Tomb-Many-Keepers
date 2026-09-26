using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Steamworks;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Session;

// Who a player is within a campaign: their Steam account, never a name or connection order.
// Two games on one account, such as a local test, are told apart by a profile kept beside each
// installation's saves.
internal static class PlayerIdentity
{
    private const string ProfileFile = "tmk_profile.txt";
    private static string profile;

    internal static ulong SteamId => SteamUser.GetSteamID().m_SteamID;

    internal static string Profile
    {
        get
        {
            if (profile != null)
                return profile;
            string path = Path.Combine(Application.persistentDataPath, ProfileFile);
            try
            {
                if (File.Exists(path))
                    profile = File.ReadAllText(path).Trim();
                if (string.IsNullOrEmpty(profile))
                {
                    profile = Guid.NewGuid().ToString("N");
                    File.WriteAllText(path, profile);
                }
            }
            catch (IOException exception)
            {
                Debug.LogWarning("[Multiplayer] Could not keep the player profile: " + exception.Message);
                profile ??= Guid.NewGuid().ToString("N");
            }
            return profile;
        }
    }

    // A player's key in a campaign; the profile only matters when another player shares the account.
    internal static string Key(ulong steamId, string profile, bool sharedAccount) =>
        sharedAccount ? $"{steamId}/{profile}" : steamId.ToString();

    // The id a player's keeper carries in the campaign's save.
    internal static SGuid CharacterId(string key)
    {
        using var md5 = MD5.Create();
        return new SGuid(new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("tmk-keeper:" + key))));
    }
}
