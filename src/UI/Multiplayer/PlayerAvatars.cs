using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// Players' Steam profile pictures as the lobby shows them, fetched from Steam once and kept.
internal static class PlayerAvatars
{
    private static readonly Dictionary<ulong, Texture2D> avatars = new Dictionary<ulong, Texture2D>();
    private static readonly HashSet<ulong> requested = new HashSet<ulong>();

    // Null until Steam has the picture, which takes a moment for a player who is not a friend, and for a
    // player without one.
    internal static Texture2D Of(ulong account)
    {
        if (account == 0)
            return null;
        if (avatars.TryGetValue(account, out var avatar))
            return avatar;
        var id = new CSteamID(account);
        int image = SteamFriends.GetLargeFriendAvatar(id);
        if (image == 0 && requested.Add(account))
            SteamFriends.RequestUserInformation(id, false);
        if (image <= 0 || !SteamUtils.GetImageSize(image, out uint width, out uint height) || width == 0 || height == 0)
            return null;
        var pixels = new byte[width * height * 4];
        if (!SteamUtils.GetImageRGBA(image, pixels, pixels.Length))
            return null;
        // Steam's rows run from the top, a texture's from the bottom.
        int row = (int)width * 4;
        var rows = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * row, rows, ((int)height - 1 - y) * row, row);
        avatar = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false)
        {
            name = "Steam avatar",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        avatar.LoadRawTextureData(rows);
        avatar.Apply(false, true);
        avatars[account] = avatar;
        return avatar;
    }
}
