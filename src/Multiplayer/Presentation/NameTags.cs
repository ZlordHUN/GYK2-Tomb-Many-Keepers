using Cinemachine;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using GYK2.TombManyKeepers.UI;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Every other player's name above their keeper, as GYK1's name tags: the game's own bold letters in white with the
// eight-sided outline of its window headers, drawn in the darker tone of the player's colour, over the world and its
// objects' bubbles but under the HUD, speech bubbles, letterbox, windows and fades. A name follows its keeper as the
// camera moves, steps aside while a speech bubble or answer menu of that player's stands by their keeper's head, and
// every name hides while a window is open.
internal sealed class NameTags : MonoBehaviour
{
    // Above the objects' bubbles (40), under the sleep fade (49) and the HUD (50).
    private const int SortingOrder = 45;
    private const float Width = 160f, Height = 20f;
    private const string Template = "GenericWIndowLayout/Frame/HeaderGroup/Header";
    // The style's own outline colour gives way to the player's, which the style keeps through language changes.
    private static readonly AccessTools.FieldRef<TextStyleComponent, bool> HasOutline =
        AccessTools.FieldRefAccess<TextStyleComponent, bool>("hasCustomOutlineColor");
    private static readonly AccessTools.FieldRef<TextStyleComponent, Color> OutlineColor =
        AccessTools.FieldRefAccess<TextStyleComponent, Color>("customOutlineColor");
    private static NameTags instance;
    private readonly TMP_Text[] tags = new TMP_Text[CoopSession.MaxPlayers + 1];
    private readonly string[] names = new string[CoopSession.MaxPlayers + 1];
    private readonly int[] colors = new int[CoopSession.MaxPlayers + 1];
    private Canvas canvas;

    // The names follow the keepers while this game plays a shared game.
    internal static void Follow()
    {
        if (instance != null)
            return;
        var bubbles = UIObjectBubbleManager.Instance;
        if (bubbles == null)
            return;
        var root = new GameObject("Multiplayer Name Tags", typeof(RectTransform), typeof(Canvas)) { layer = bubbles.gameObject.layer };
        var area = (RectTransform)root.transform;
        area.SetParent(bubbles.transform.parent, false);
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.offsetMin = area.offsetMax = Vector2.zero;
        instance = root.AddComponent<NameTags>();
        instance.canvas = root.GetComponent<Canvas>();
        instance.canvas.overrideSorting = true;
        instance.canvas.sortingOrder = SortingOrder;
    }

    internal static void Clear()
    {
        if (instance == null)
            return;
        Destroy(instance.gameObject);
        instance = null;
    }

    // As the game places its bubbles, after the camera has moved.
    private void OnEnable() => CinemachineCore.CameraUpdatedEvent.AddListener(Place);

    private void OnDisable() => CinemachineCore.CameraUpdatedEvent.RemoveListener(Place);

    // Each name stands on the point the game's speech bubbles point at, above its keeper's head, in whole units of the
    // canvas so the letters stay sharp.
    private void Place(CinemachineBrain brain)
    {
        var session = CoopSession.Current;
        bool open = session != null && KeeperSpawn.Active && LazyWindowsStackController.ActiveWindow == null;
        float unit = canvas.scaleFactor;
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
        {
            string name = open && slot != session.LocalSlot ? session.PlayerName(slot) : null;
            var point = name == null || SharedSpeech.Speaking(slot) || SharedAnswers.Answering(slot) ? null : RemoteKeeper.BubblePoint(slot);
            var screen = point == null ? Vector3.back : CameraSystem.WorldToScreenPoint(point.position);
            var tag = tags[slot];
            if (screen.z <= 0f)
            {
                if (tag != null && tag.gameObject.activeSelf)
                    tag.gameObject.SetActive(false);
                continue;
            }
            if (tag == null)
                tag = tags[slot] = Make(slot);
            if (names[slot] != name)
            {
                names[slot] = name;
                NativeWindow.SetText(tag, NativeWindow.Literal(name));
            }
            int color = session.ColorOf(slot);
            if (colors[slot] != color)
            {
                colors[slot] = color;
                Outline(tag, color);
            }
            tag.rectTransform.position = new Vector3(Mathf.Round(screen.x / unit) * unit, Mathf.Round(screen.y / unit) * unit, 0f);
            if (!tag.gameObject.activeSelf)
                tag.gameObject.SetActive(true);
        }
    }

    // A copy of the native dialog window's header text, which keeps its style through language changes.
    private TMP_Text Make(int slot)
    {
        var header = NativeWindow.Find<TMP_Text>(LazyUI.GetWindow<UIDialogWindow>().transform, Template);
        var tag = Instantiate(header, transform);
        tag.name = "Keeper " + slot;
        tag.raycastTarget = false;
        var rect = tag.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.sizeDelta = new Vector2(Width, Height);
        tag.alignment = TextAlignmentOptions.Bottom;
        tag.textWrappingMode = TextWrappingModes.NoWrap;
        tag.overflowMode = TextOverflowModes.Ellipsis;
        tag.margin = Vector4.zero;
        names[slot] = null;
        colors[slot] = int.MinValue;
        return tag;
    }

    // The outline in the darker tone of the player's colour, or the style's own for a player without one.
    private static void Outline(TMP_Text tag, int color)
    {
        var style = tag.GetComponent<TextStyleComponent>();
        HasOutline(style) = PlayerColors.Valid(color);
        if (PlayerColors.Valid(color))
            OutlineColor(style) = PlayerColors.OutlineOf(color);
        style.ApplyStyle();
    }
}
