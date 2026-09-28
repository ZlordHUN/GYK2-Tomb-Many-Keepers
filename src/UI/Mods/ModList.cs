using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Mods;

// The mods the Mods window lists, each on a copy of the save list's own card, as GYK1 listed them on its save cards:
// the mod's name, then its version and how many settings it shows. A click picks a mod, lit in gold as a picked game
// is in Join Game, and the window shows its settings; a gamepad picks the mod it moves to, and its select key enters
// the settings.
internal sealed class ModList
{
    private static readonly Color PickColor = new Color(1f, 0.82f, 0.35f);
    // How far a card's two lines move down to stand in its middle once its third, the standings, is gone.
    private const float LinesDown = 7f;
    private readonly Transform list;
    private readonly UISaveSlot card;
    private readonly int group;
    private readonly Action<PluginInfo> picked;
    private readonly Action entered;
    private readonly List<Card> cards = new List<Card>();

    private sealed class Card
    {
        internal PluginInfo Plugin;
        internal LazyButton Button;
        internal GameObject Hover;
        internal GameObject Lit;
    }

    internal PluginInfo Picked { get; private set; }

    // The list's content holds the cards; a gamepad reaches them in the navigation group given.
    internal ModList(Transform list, UISaveSlot card, int group, Action<PluginInfo> picked, Action entered)
    {
        this.list = list;
        this.card = card;
        this.group = group;
        this.picked = picked;
        this.entered = entered;
    }

    // A mod's name as the list shows it: every mod listed is for this game, so a name that begins with the game's own
    // leaves it out.
    internal static string Named(PluginInfo plugin)
    {
        const string Game = "Graveyard Keeper 2:";
        string name = plugin.Metadata.Name ?? string.Empty;
        return name.StartsWith(Game, StringComparison.OrdinalIgnoreCase) && name.Length > Game.Length ? name.Substring(Game.Length).Trim() : name;
    }

    // Lists the mods loaded now by their names, each with the settings it shows, and picks the mod picked before if
    // it is still loaded, or else the first.
    internal void Show(IEnumerable<PluginInfo> plugins, Func<PluginInfo, int> settings)
    {
        foreach (var old in cards)
        {
            // Hidden at once, so nothing finds the card in the frame before it goes.
            old.Button.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(old.Button.gameObject);
        }
        cards.Clear();
        foreach (var plugin in plugins.OrderBy(Named, StringComparer.OrdinalIgnoreCase))
            cards.Add(Add(plugin, settings(plugin)));
        // The list's end ornament follows the cards.
        list.Find("Footer")?.SetAsLastSibling();
        var again = cards.FirstOrDefault(item => item.Plugin == Picked) ?? cards.FirstOrDefault();
        Picked = null;
        if (again != null)
            Pick(again);
    }

    // The picked mod's card, which a gamepad returns to from its settings.
    internal GamepadNavigationItem PickedItem =>
        cards.FirstOrDefault(item => item.Plugin == Picked)?.Button.GetComponent<GamepadNavigationItem>();

    // Whether a part, such as a gamepad's focus, is one of the cards.
    internal bool Lists(Component part) => part != null && part.transform.parent == list;

    private Card Add(PluginInfo plugin, int settings)
    {
        var button = NativeWindow.Card(card, list, plugin.Metadata.GUID, null);
        var root = button.gameObject;
        root.transform.Find("SourceLabel").gameObject.SetActive(false);
        var name = root.transform.Find("Date").GetComponent<TMP_Text>();
        var status = root.transform.Find("Days").GetComponent<TMP_Text>();
        foreach (var line in new[] { name.rectTransform, status.rectTransform })
        {
            line.offsetMin -= new Vector2(0f, LinesDown);
            line.offsetMax -= new Vector2(0f, LinesDown);
            // A long name ends in dots; the settings' plate shows it whole.
            var text = line.GetComponent<TMP_Text>();
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }
        name.text = NativeWindow.Literal(Named(plugin));
        status.text = NativeWindow.Literal(plugin.Metadata.Version.ToString()) + "  -  " +
                      (settings == 0 ? "No settings" : settings == 1 ? "1 setting" : settings + " settings");
        var item = new Card
        {
            Plugin = plugin,
            Button = button,
            Hover = root.transform.Find("Selection").gameObject
        };
        item.Lit = UnityEngine.Object.Instantiate(item.Hover, root.transform);
        item.Lit.name = "Picked";
        item.Lit.transform.SetSiblingIndex(item.Hover.transform.GetSiblingIndex());
        item.Lit.GetComponent<Image>().color = PickColor;
        item.Lit.SetActive(false);
        item.Hover.SetActive(false);
        button.onEnter.AddListener(() =>
        {
            item.Hover.SetActive(true);
            LazyAudio.PlayAndForget("gui_hover");
        });
        button.onExit.AddListener(() => item.Hover.SetActive(false));
        button.onClick.AddListener(() => Pick(item));
        NativeWindow.Navigable(button);
        var navigation = root.GetComponent<GamepadNavigationItem>();
        navigation.group = group;
        navigation.OnFocus.AddListener(() =>
        {
            if (Picked != item.Plugin)
                Pick(item);
        });
        navigation.OnSelect.AddListener(() => entered());
        return item;
    }

    private void Pick(Card item)
    {
        item.Hover.SetActive(false);
        if (Picked == item.Plugin)
            return;
        Picked = item.Plugin;
        foreach (var other in cards)
            other.Lit.SetActive(other == item);
        picked(item.Plugin);
    }
}
