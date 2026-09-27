using System;
using System.Collections.Generic;
using LazyBearTechnology;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The player's Steam friends who are online, each on a copy of the save list's card: their Steam avatar in the
// game's dark cell, their name and what they are doing, green when online or playing this game, gold when away and
// red when busy. Friends playing this game come first, then the others by how reachable they are and by name;
// those already in the lobby are left out, and those invited say so. A click picks a friend, lit in gold as a
// picked game is; picking them again invites them.
internal sealed class FriendsList
{
    private static readonly Color PickColor = new Color(1f, 0.82f, 0.35f);
    private static readonly Color Reachable = new Color(0.55f, 0.8f, 0.4f), Busy = new Color(0.9f, 0.42f, 0.3f);
    // The card's avatar cell at its left, and the room the name and status leave around themselves.
    private const float Inset = 5f, CellSize = 28f, TextGap = 6f, EndMargin = 6f;

    private readonly Transform list;
    private readonly UISaveSlot card;
    private readonly Image cell;
    private readonly TMP_Text notice;
    private readonly Action<ulong> invite;
    private readonly Dictionary<ulong, Row> rows = new Dictionary<ulong, Row>();
    private readonly List<Friend> friends = new List<Friend>();
    private bool changed;

    private sealed class Friend
    {
        internal ulong Account;
        internal string Name;
        internal string Status;
        internal Color Color;
        internal int Rank;
    }

    private sealed class Row
    {
        internal GameObject Root;
        internal TMP_Text Name;
        internal TMP_Text Status;
        internal RawImage Avatar;
        internal GameObject Hover;
        internal GameObject Lit;
    }

    internal ulong Picked { get; private set; }

    // Whether a listed friend's avatar is still to come from Steam.
    internal bool AvatarsPending { get; private set; }

    // The list's content holds the cards; the notice stands in the middle of its view. The cell is the game's
    // dark cell, under each avatar.
    internal FriendsList(Transform list, UISaveSlot card, Image cell, TMP_Text notice, Action<ulong> invite)
    {
        this.list = list;
        this.card = card;
        this.cell = cell;
        this.notice = notice;
        this.invite = invite;
    }

    internal string NameOf(ulong account) => SteamFriends.GetFriendPersonaName(new CSteamID(account));

    // Lists the friends online now, without those the lobby already has; those invited say so.
    internal void Show(Func<ulong, bool> present, Func<ulong, bool> invited)
    {
        friends.Clear();
        uint game = SteamUtils.GetAppID().m_AppId;
        int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (int i = 0; i < count; i++)
        {
            var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            var state = SteamFriends.GetFriendPersonaState(id);
            if (state == EPersonaState.k_EPersonaStateOffline || state == EPersonaState.k_EPersonaStateInvisible || present(id.m_SteamID))
                continue;
            bool playing = SteamFriends.GetFriendGamePlayed(id, out var played) && played.m_gameID.IsValid() && played.m_gameID.AppID().m_AppId == game;
            var friend = new Friend { Account = id.m_SteamID, Name = SteamFriends.GetFriendPersonaName(id) };
            Describe(friend, state, playing, invited(friend.Account));
            friends.Add(friend);
        }
        friends.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var listed = new HashSet<ulong>();
        AvatarsPending = false;
        for (int i = 0; i < friends.Count; i++)
        {
            var friend = friends[i];
            listed.Add(friend.Account);
            if (!rows.TryGetValue(friend.Account, out var row))
            {
                rows[friend.Account] = row = Add(friend.Account);
                changed = true;
            }
            row.Name.text = NativeWindow.Literal(friend.Name);
            row.Status.text = friend.Status;
            row.Status.color = friend.Color;
            if (row.Avatar.texture == null)
            {
                var avatar = PlayerAvatars.Of(friend.Account);
                row.Avatar.texture = avatar;
                row.Avatar.enabled = avatar != null;
                AvatarsPending |= avatar == null;
            }
            if (row.Root.transform.GetSiblingIndex() != i)
                row.Root.transform.SetSiblingIndex(i);
        }
        foreach (ulong account in new List<ulong>(rows.Keys))
        {
            if (listed.Contains(account))
                continue;
            if (Picked == account)
                Picked = 0;
            // Hidden at once, so nothing finds the card in the frame before it goes.
            rows[account].Root.SetActive(false);
            UnityEngine.Object.Destroy(rows[account].Root);
            rows.Remove(account);
            changed = true;
        }
        foreach (var pair in rows)
            pair.Value.Lit.SetActive(pair.Key == Picked);
        notice.gameObject.SetActive(friends.Count == 0);
        notice.text = "No friends online";
    }

    internal bool TakeChanges()
    {
        bool taken = changed;
        changed = false;
        return taken;
    }

    // Whether a part, such as a gamepad's focus, is one of the listed friends.
    internal bool Lists(Component part) => part != null && part.transform.parent == list;

    private static void Describe(Friend friend, EPersonaState state, bool playing, bool invited)
    {
        friend.Rank = playing ? 0 : state == EPersonaState.k_EPersonaStateBusy ? 3 :
            state == EPersonaState.k_EPersonaStateAway || state == EPersonaState.k_EPersonaStateSnooze ? 2 : 1;
        friend.Color = friend.Rank == 3 ? Busy : friend.Rank == 2 ? PickColor : Reachable;
        if (invited)
        {
            friend.Status = "Invited";
            friend.Color = PickColor;
            return;
        }
        friend.Status = playing ? "In Graveyard Keeper 2" : state switch
        {
            EPersonaState.k_EPersonaStateBusy => "Busy",
            EPersonaState.k_EPersonaStateAway => "Away",
            EPersonaState.k_EPersonaStateSnooze => "Snooze",
            EPersonaState.k_EPersonaStateLookingToPlay => "Looking to play",
            EPersonaState.k_EPersonaStateLookingToTrade => "Looking to trade",
            _ => "Online"
        };
    }

    private Row Add(ulong account)
    {
        var button = NativeWindow.Card(card, list, "Friend", null);
        var root = button.gameObject;
        var source = root.transform.Find("SourceLabel");
        if (source != null)
            UnityEngine.Object.DestroyImmediate(source.gameObject);
        var row = new Row
        {
            Root = root,
            Name = root.transform.Find("Date").GetComponent<TMP_Text>(),
            Status = root.transform.Find("Days").GetComponent<TMP_Text>(),
            Hover = root.transform.Find("Selection").gameObject
        };
        var avatarCell = NativeWindow.Cell(cell, root.transform, "Cell");
        var area = avatarCell.rectTransform;
        area.anchorMin = area.anchorMax = new Vector2(0f, 0.5f);
        area.pivot = new Vector2(0f, 0.5f);
        area.sizeDelta = new Vector2(CellSize, CellSize);
        area.anchoredPosition = new Vector2(Inset, 0f);
        row.Avatar = new GameObject("Avatar", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage)).GetComponent<RawImage>();
        row.Avatar.transform.SetParent(area, false);
        row.Avatar.rectTransform.anchorMin = Vector2.zero;
        row.Avatar.rectTransform.anchorMax = Vector2.one;
        row.Avatar.rectTransform.offsetMin = new Vector2(2f, 2f);
        row.Avatar.rectTransform.offsetMax = new Vector2(-2f, -2f);
        row.Avatar.enabled = false;
        // The name above the middle and the status below it, beside the avatar.
        float left = Inset + CellSize + TextGap;
        Line(row.Name, left, top: true);
        Line(row.Status, left, top: false);
        // The status keeps the colour it is given.
        UnityEngine.Object.DestroyImmediate(row.Status.GetComponent<TextStyleComponent>());
        row.Lit = UnityEngine.Object.Instantiate(row.Hover, root.transform);
        row.Lit.name = "Picked";
        row.Lit.transform.SetSiblingIndex(row.Hover.transform.GetSiblingIndex());
        row.Lit.GetComponent<Image>().color = PickColor;
        row.Lit.SetActive(false);
        row.Hover.SetActive(false);
        button.onEnter.AddListener(() =>
        {
            row.Hover.SetActive(true);
            LazyAudio.PlayAndForget("gui_hover");
        });
        button.onExit.AddListener(() => row.Hover.SetActive(false));
        button.onClick.AddListener(() => Clicked(account));
        NativeWindow.Navigable(button);
        return row;
    }

    private static void Line(TMP_Text line, float left, bool top)
    {
        var rect = line.rectTransform;
        rect.anchorMin = new Vector2(0f, top ? 0.5f : 0f);
        rect.anchorMax = new Vector2(1f, top ? 1f : 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.offsetMin = new Vector2(left, top ? -1f : 2f);
        rect.offsetMax = new Vector2(-EndMargin, top ? -2f : 1f);
        line.alignment = top ? TextAlignmentOptions.BottomLeft : TextAlignmentOptions.TopLeft;
        line.textWrappingMode = TextWrappingModes.NoWrap;
        line.overflowMode = TextOverflowModes.Ellipsis;
        line.margin = Vector4.zero;
    }

    private void Clicked(ulong account)
    {
        if (!rows.TryGetValue(account, out var row))
            return;
        row.Hover.SetActive(false);
        if (Picked == account)
        {
            invite(account);
            return;
        }
        Picked = account;
        foreach (var pair in rows)
            pair.Value.Lit.SetActive(pair.Key == account);
    }
}
