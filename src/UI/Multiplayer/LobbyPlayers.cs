using System.Collections.Generic;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The lobby's players as GYK1 shows them: each keeper's player by their Steam avatar, outlined in gold once
// ready, with their name beneath it, and dim open places for the keepers still to come. The places the game
// takes sit together in the middle of the panel.
internal sealed class LobbyPlayers
{
    // One keeper's place, its avatar's cell and the room above and below them. In the lobby's 76-unit panel the
    // outline's top and the name's lowest letters stand as far from the panel's edges.
    private const float Width = 64f, CellSize = 38f, Top = 14f, NameGap = 4f, NameHeight = 12f;
    // GYK1's ready outline.
    private static readonly Color ReadyColor = new Color(0.94f, 0.64f, 0.24f);
    private static readonly Color OpenColor = new Color(1f, 1f, 1f, 0.35f);
    private static readonly Color Named = new Color(1f, 0.741f, 0f);
    private static readonly Color Faded = new Color(0.588f, 0.553f, 0.533f);

    private readonly List<Place> places = new List<Place>();
    private readonly RectTransform panel;

    private sealed class Place
    {
        internal GameObject Root;
        internal Image Cell;
        internal RawImage Avatar;
        internal GameObject Outline;
        internal TMP_Text Name;
    }

    // Built from the game's dark cell, its selection frame and its text.
    internal LobbyPlayers(RectTransform panel, Image cell, Image frame, TMP_Text line)
    {
        this.panel = panel;
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
        {
            var root = new GameObject($"Keeper {slot}", typeof(RectTransform)).transform;
            root.SetParent(panel, false);
            var place = new Place { Root = root.gameObject };
            float middle = Top + CellSize / 2f;
            place.Outline = NativeWindow.Cell(frame, root, "Ready").gameObject;
            place.Outline.GetComponent<Image>().color = ReadyColor;
            NativeWindow.Place(place.Outline.transform, Width / 2f, middle, CellSize + 6f, CellSize + 6f);
            place.Cell = NativeWindow.Cell(cell, root, "Cell");
            NativeWindow.Place(place.Cell.transform, Width / 2f, middle, CellSize, CellSize);
            place.Avatar = new GameObject("Avatar", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage)).GetComponent<RawImage>();
            place.Avatar.transform.SetParent(place.Cell.transform, false);
            place.Avatar.rectTransform.anchorMin = Vector2.zero;
            place.Avatar.rectTransform.anchorMax = Vector2.one;
            place.Avatar.rectTransform.offsetMin = new Vector2(2f, 2f);
            place.Avatar.rectTransform.offsetMax = new Vector2(-2f, -2f);
            place.Name = Object.Instantiate(line, root);
            place.Name.name = "Name";
            // The lobby sets the name's alignment itself.
            Object.DestroyImmediate(place.Name.GetComponent<TextStyleComponent>());
            place.Name.gameObject.SetActive(true);
            place.Name.alignment = TextAlignmentOptions.Center;
            place.Name.textWrappingMode = TextWrappingModes.NoWrap;
            place.Name.overflowMode = TextOverflowModes.Ellipsis;
            place.Name.richText = true;
            place.Name.margin = Vector4.zero;
            NativeWindow.Place(place.Name.transform, Width / 2f, Top + CellSize + NameGap + NameHeight / 2f, Width - 4f, NameHeight);
            places.Add(place);
        }
    }

    // Returns whether a present player's avatar is still to come from Steam.
    internal bool Draw(CoopSession session)
    {
        bool waiting = false;
        int shown = Mathf.Min(session.Settings.Players, places.Count);
        float first = (panel.rect.width - shown * Width) / 2f;
        for (int slot = 1; slot <= places.Count; slot++)
        {
            var place = places[slot - 1];
            place.Root.SetActive(slot <= shown);
            NativeWindow.Place(place.Root.transform, first + (slot - 0.5f) * Width, panel.rect.height / 2f, Width, panel.rect.height);
            string name = session.PlayerName(slot);
            bool here = name != null;
            place.Cell.color = here ? Color.white : OpenColor;
            place.Name.text = here ? NativeWindow.Literal(name) : "Open";
            place.Name.color = here && slot == session.LocalSlot ? Named : Faded;
            place.Outline.SetActive(here && session.IsReady(slot));
            ulong account = here ? session.PlayerAccount(slot) : 0;
            var avatar = PlayerAvatars.Of(account);
            place.Avatar.texture = avatar;
            place.Avatar.enabled = avatar != null;
            waiting |= account != 0 && avatar == null;
        }
        return waiting;
    }
}
