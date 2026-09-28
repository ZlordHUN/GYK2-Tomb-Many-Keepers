using System;
using System.Collections.Generic;
using GYK2.TombManyKeepers.Network.Discovery;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The games a browser lists, each on a copy of the save list's own card: the host's name, with the game's gold
// star when it is a favourite and its lock when joining asks for a password, whether its game waits in the lobby or
// runs and which campaign it plays, how many keepers it has and its ping, and, where a tab mixes them, where it was
// found. A game of another build of the mod, or on another version of the game, says so after its campaign, in
// GYK1's warning colour for a game that cannot be joined. Favourites that do not answer follow, dimmed. A click picks a game, lit in gold as the host's
// campaign pick is; picking it again joins, as a double click does. Without games the list says why in its middle.
internal sealed class ServerList
{
    private static readonly Color PickColor = new Color(1f, 0.82f, 0.35f);
    // A ping reads as quick, usable or slow at a glance, as in GYK1's browser, and dim before it is timed.
    private static readonly Color Quick = new Color(0.55f, 0.8f, 0.4f), Slow = new Color(0.9f, 0.42f, 0.3f);
    private static readonly Color Dim = new Color(0.588f, 0.553f, 0.533f);
    private static readonly Color Incompatible = new Color(0.95f, 0.5f, 0.35f);
    private const string OtherBuild = "  -  incompatible: mod build", OtherVersion = "  -  incompatible: game version";
    private const float QuickPing = 50f, UsablePing = 100f;
    // How far a card's two lines move down to stand in its middle once its third, the standings, is gone. The
    // card's right end keeps a margin from its border and holds the keepers, the ping just after them and, where
    // shown, where the game was found.
    private const float LinesDown = 7f, Margin = 14f, SourceWidth = 44f, SourceGap = 10f, PingWidth = 48f, PingGap = 12f;
    private const float KeepersWidth = 36f, Gap = 10f;
    // The star's sprite draws it 9 units wide in the middle of its 16; it stands this far after the name.
    private const float StarGap = 4f, StarHalf = 4.5f, Missing = 0.55f;
    private const string Star = "icon_star", Lock = "icon_lock";
    private readonly Transform list;
    private readonly Transform end;
    private readonly UISaveSlot card;
    private readonly TMP_Text notice;
    private readonly Action<LanDiscovery.Game> join;
    // Rows by the game they list, or by the favourite that did not answer.
    private readonly Dictionary<object, Row> rows = new Dictionary<object, Row>();
    private Row pointed;

    private sealed class Row
    {
        internal GameObject Root;
        internal CanvasGroup Group;
        internal LanDiscovery.Game Game;
        internal FavoriteHosts.Host Favorite;
        internal TMP_Text Name;
        internal TMP_Text Status;
        // The status line's own colour, for a game that can be joined.
        internal Color StatusColor;
        internal TMP_Text Keepers;
        internal TMP_Text Ping;
        internal TMP_Text Source;
        // Whether the card shows where its game was found, or null before it is laid out.
        internal bool? Sourced;
        internal Image Star;
        internal Image Lock;
        internal GameObject Hover;
        internal GameObject Lit;
    }

    internal LanDiscovery.Game Picked { get; private set; }

    // Cards came or went since the browser last took the news, which its gamepad navigation must follow.
    private bool changed;

    // The list's content holds the rows; the notice stands in the middle of its viewport.
    internal ServerList(Transform list, UISaveSlot card, TMP_Text notice, Action<LanDiscovery.Game> join)
    {
        this.list = list;
        end = list.Find("Footer");
        this.card = card;
        this.notice = notice;
        this.join = join;
    }

    internal void Show(IReadOnlyList<LanDiscovery.Game> games, string none) =>
        Show(games, Array.Empty<FavoriteHosts.Host>(), null, none, null);

    // Shows these games in their order, then the favourites that did not answer with the status given, or the
    // reason there is nothing to list. Where the tab mixes games found in different places, each says where.
    internal void Show(IReadOnlyList<LanDiscovery.Game> games, IReadOnlyList<FavoriteHosts.Host> missing, string missingStatus,
        string none, Func<LanDiscovery.Game, string> source)
    {
        var listed = new HashSet<object>();
        int index = 0;
        foreach (var game in games)
        {
            var row = Take(game, listed);
            row.Game = game;
            row.Group.alpha = 1f;
            Columns(row, source != null);
            if (source != null)
                row.Source.text = source(game);
            Name(row, game.Name, FavoriteHosts.Contains(game.Host), game.Access == (byte)Network.Session.HostSettings.Access.Password);
            // The mod's build is named before the game's version, as the host checks them.
            string incompatible = !Network.Session.ModBuild.Matches(game.Build) ? OtherBuild :
                !Network.Session.GameVersion.Matches(game.GameVersion) ? OtherVersion : null;
            row.Status.text = $"{(game.InLobby ? "In lobby" : "In game")}  -  {game.Campaign}{incompatible}";
            row.Status.color = incompatible != null ? Incompatible : row.StatusColor;
            row.Keepers.text = $"{game.Players}/{game.Capacity}";
            row.Ping.text = game.Ping < 0f ? "..." : $"{Math.Max(1, Mathf.RoundToInt(game.Ping))} ms";
            row.Ping.color = game.Ping < 0f ? Dim : game.Ping <= QuickPing ? Quick : game.Ping <= UsablePing ? PickColor : Slow;
            Order(row, index++);
        }
        foreach (var favorite in missing)
        {
            var row = Take(favorite, listed);
            row.Favorite = favorite;
            row.Group.alpha = Missing;
            Columns(row, source != null);
            row.Source.text = string.Empty;
            Name(row, favorite.Name, favorite: true, locked: false);
            row.Status.text = missingStatus;
            row.Status.color = row.StatusColor;
            row.Keepers.text = "-/-";
            row.Ping.text = "---";
            row.Ping.color = Dim;
            Order(row, index++);
        }
        foreach (var key in new List<object>(rows.Keys))
        {
            if (listed.Contains(key))
                continue;
            var row = rows[key];
            if (Picked == row.Game)
                Picked = null;
            if (pointed == row)
                pointed = null;
            // Hidden at once, so nothing finds the card in the frame before it goes.
            row.Root.SetActive(false);
            UnityEngine.Object.Destroy(row.Root);
            rows.Remove(key);
            changed = true;
        }
        foreach (var row in rows.Values)
            row.Lit.SetActive(row.Game != null && row.Game == Picked);
        notice.gameObject.SetActive(index == 0);
        notice.text = none;
        // The list's end ornament follows its games; without any it would stand alone above the notice.
        if (end != null)
            end.gameObject.SetActive(index > 0);
    }

    internal void Clear()
    {
        Picked = null;
        Show(Array.Empty<LanDiscovery.Game>(), notice.text);
    }

    // The listed host the player points at, under the mouse or at a gamepad's focus: its Steam account, its game
    // unless it is a favourite that did not answer, and where a menu for it opens.
    internal bool TryPointed(out ulong host, out LanDiscovery.Game game, out Vector3 at)
    {
        host = 0;
        game = null;
        at = default;
        if (pointed == null || !pointed.Root.activeInHierarchy)
            return false;
        game = pointed.Game;
        host = game?.Host ?? pointed.Favorite.Account;
        at = LazyInput.IsGamepadActive ? pointed.Root.transform.position : Input.mousePosition;
        return true;
    }

    internal bool TakeChanges()
    {
        bool taken = changed;
        changed = false;
        return taken;
    }

    // Whether a part, such as a gamepad's focus, is one of the listed games.
    internal bool Lists(Component part) => part != null && part.transform.parent == list;

    private Row Take(object key, HashSet<object> listed)
    {
        listed.Add(key);
        if (!rows.TryGetValue(key, out var row))
        {
            rows[key] = row = Add();
            changed = true;
        }
        return row;
    }

    private void Order(Row row, int index)
    {
        if (row.Root.transform.GetSiblingIndex() != index)
            row.Root.transform.SetSiblingIndex(index);
    }

    // The host's name, with the star after it for a favourite, then the lock for a password game.
    private static void Name(Row row, string name, bool favorite, bool locked)
    {
        string shown = NativeWindow.Literal(name);
        bool renamed = row.Name.text != shown;
        row.Name.text = shown;
        if (row.Star.gameObject.activeSelf == favorite && row.Lock.gameObject.activeSelf == locked && !renamed)
            return;
        row.Star.gameObject.SetActive(favorite);
        row.Lock.gameObject.SetActive(locked);
        // The name's own width, which a card not laid out yet already knows.
        float after = row.Name.GetPreferredValues(shown).x + StarGap + StarHalf;
        if (favorite)
        {
            row.Star.rectTransform.anchoredPosition = new Vector2(Mathf.Round(after), 0f);
            after += 2f * StarHalf + StarGap;
        }
        if (locked)
            row.Lock.rectTransform.anchoredPosition = new Vector2(Mathf.Round(after), 0f);
    }

    // The card's right end: the keepers and the ping, then where the game was found when the tab shows it. The
    // name and status take the rest.
    private static void Columns(Row row, bool source)
    {
        if (row.Sourced == source)
            return;
        row.Sourced = source;
        row.Source.gameObject.SetActive(source);
        float ping = Margin + (source ? SourceWidth + SourceGap : 0f);
        float keepers = ping + PingWidth + PingGap;
        row.Ping.rectTransform.anchoredPosition = new Vector2(-ping, 0f);
        row.Keepers.rectTransform.anchoredPosition = new Vector2(-keepers, 0f);
        foreach (var line in new[] { row.Name.rectTransform, row.Status.rectTransform })
            line.offsetMax = new Vector2(-(keepers + KeepersWidth + Gap), line.offsetMax.y);
    }

    private Row Add()
    {
        // The copy lists a game, not a save.
        var button = NativeWindow.Card(card, list, "Game", "LAN");
        var root = button.gameObject;
        var row = new Row
        {
            Root = root,
            Group = root.TryGetComponent<CanvasGroup>(out var group) ? group : root.AddComponent<CanvasGroup>(),
            Name = root.transform.Find("Date").GetComponent<TMP_Text>(),
            Status = root.transform.Find("Days").GetComponent<TMP_Text>(),
            Hover = root.transform.Find("Selection").gameObject
        };
        row.StatusColor = row.Status.color;
        foreach (var line in new[] { row.Name.rectTransform, row.Status.rectTransform })
        {
            line.offsetMin -= new Vector2(0f, LinesDown);
            line.offsetMax -= new Vector2(0f, LinesDown);
        }
        row.Source = root.transform.Find("SourceLabel").GetComponent<TMP_Text>();
        RightEnd(row.Source, SourceWidth);
        row.Ping = UnityEngine.Object.Instantiate(row.Name, root.transform);
        row.Ping.name = "Ping";
        RightEnd(row.Ping, PingWidth);
        // The ping follows the keepers closely, whatever its digits.
        row.Ping.alignment = TextAlignmentOptions.MidlineLeft;
        row.Keepers = UnityEngine.Object.Instantiate(row.Name, root.transform);
        row.Keepers.name = "Keepers";
        RightEnd(row.Keepers, KeepersWidth);
        row.Source.rectTransform.anchoredPosition = new Vector2(-Margin, 0f);
        row.Star = Badge(row.Name, "Favorite", Star);
        row.Lock = Badge(row.Name, "Password", Lock);
        row.Lit = UnityEngine.Object.Instantiate(row.Hover, root.transform);
        row.Lit.name = "Picked";
        row.Lit.transform.SetSiblingIndex(row.Hover.transform.GetSiblingIndex());
        row.Lit.GetComponent<Image>().color = PickColor;
        row.Lit.SetActive(false);
        row.Hover.SetActive(false);
        button.onEnter.AddListener(() =>
        {
            pointed = row;
            row.Hover.SetActive(true);
            LazyAudio.PlayAndForget("gui_hover");
        });
        button.onExit.AddListener(() =>
        {
            if (pointed == row)
                pointed = null;
            row.Hover.SetActive(false);
        });
        button.onClick.AddListener(() => Clicked(row));
        NativeWindow.Navigable(button);
        return row;
    }

    // One of the game's small icons after a card's name, which is drawn from its left edge, in the middle of its
    // height.
    private static Image Badge(TMP_Text name, string called, string sprite)
    {
        var badge = new GameObject(called, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
        badge.transform.SetParent(name.transform, false);
        badge.sprite = LazySingletonSO<EasySpritesCollection>.Instance.GetSprite(sprite);
        badge.SetNativeSize();
        badge.raycastTarget = false;
        badge.rectTransform.anchorMin = badge.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        badge.gameObject.SetActive(false);
        return badge;
    }

    // A label at the card's right end, right-aligned and its own alignment kept.
    private static void RightEnd(TMP_Text label, float width)
    {
        UnityEngine.Object.DestroyImmediate(label.GetComponent<TextStyleComponent>());
        label.alignment = TextAlignmentOptions.MidlineRight;
        var rect = label.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(width, 20f);
    }

    // A favourite that did not answer cannot be joined; the others are picked, then joined.
    private void Clicked(Row row)
    {
        row.Hover.SetActive(false);
        if (row.Game == null)
            return;
        if (Picked == row.Game)
        {
            join(row.Game);
            return;
        }
        Picked = row.Game;
        foreach (var other in rows.Values)
            other.Lit.SetActive(other == row);
    }
}
