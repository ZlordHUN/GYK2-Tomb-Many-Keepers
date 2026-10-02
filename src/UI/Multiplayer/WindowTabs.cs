using System;
using System.Collections.Generic;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// Page tabs copied from the game's character window: a row of tabs with the chosen one inset and diamonds between
// them. A window's header takes the character window's own, with its plate and ornaments, the gamepad's bumper hints
// and the close button; a plate of its own, such as a panel's name, takes the tabs alone, as a narrow plate has no
// room for the row's end ornaments.
internal sealed class WindowTabs
{
    private const string Source = "Assets/AddressableAssets/UIElements/WindowsBig/CharacterWindow_Big.prefab";
    private static readonly AccessTools.FieldRef<CharacterWindow, GameObject> TabRow =
        AccessTools.FieldRefAccess<CharacterWindow, GameObject>("charPageTabButtonParent");
    private static readonly AccessTools.FieldRef<CharacterWindow, CharPageTabButton> TabTemplate =
        AccessTools.FieldRefAccess<CharacterWindow, CharPageTabButton>("charPageTabButtonPrefab");
    // The character window's asset stays loaded once its header is copied, as the copies draw with its sprites.
    private static CharacterWindow source;

    private readonly List<CharPageTabButton> tabs = new List<CharPageTabButton>();
    private readonly TMP_Text next;
    private readonly TMP_Text previous;
    private readonly Action<int> chosen;

    internal int Current { get; private set; } = -1;

    internal LazyButton Close { get; }

    private WindowTabs(Transform row, IReadOnlyList<string> names, Action<int> chosen, LazyButton close)
    {
        this.chosen = chosen;
        Close = close;
        var template = row.Find(TabTemplate(source).name).GetComponent<CharPageTabButton>();
        template.gameObject.SetActive(false);
        for (int i = 0; i < names.Count; i++)
        {
            int index = i;
            var tab = UnityEngine.Object.Instantiate(template, row);
            tab.name = names[i];
            tab.gameObject.SetActive(true);
            // The tab's label reads its name through the game's texts, which give back names they do not know.
            tab.Init(names[i], () => Choose(index), i == names.Count - 1);
            tab.UpdateState(false);
            tabs.Add(tab);
        }
        next = row.Find("NextTabGamepadHelper")?.GetComponent<TMP_Text>();
        previous = row.Find("PrevTabGamepadHelper")?.GetComponent<TMP_Text>();
        UpdateHints();
    }

    // Replaces the window frame's own header with the tabbed one.
    internal static WindowTabs Header(Transform frame, IReadOnlyList<string> names, Action<int> chosen)
    {
        var sourceRow = Row();
        var own = frame.Find("HeaderGroup");
        var header = UnityEngine.Object.Instantiate(sourceRow.parent.gameObject, frame).transform;
        header.name = "HeaderGroup";
        header.SetSiblingIndex(own.GetSiblingIndex());
        UnityEngine.Object.DestroyImmediate(own.gameObject);
        return new WindowTabs(header.Find(sourceRow.name), names, chosen, header.Find("CloseButton").GetComponent<LazyButton>());
    }

    // The tabs across a plate standing on its own, in place of its name.
    internal static WindowTabs OnPlate(TMP_Text name, IReadOnlyList<string> names, Action<int> chosen)
    {
        var sourceRow = Row();
        var row = (RectTransform)UnityEngine.Object.Instantiate(sourceRow.gameObject, name.transform.parent).transform;
        row.name = "Tabs";
        foreach (string end in new[] { "DecorLeft", "DecorRight", "NextTabGamepadHelper", "PrevTabGamepadHelper" })
            UnityEngine.Object.DestroyImmediate(row.Find(end).gameObject);
        name.gameObject.SetActive(false);
        return new WindowTabs(row, names, chosen, null);
    }

    private static Transform Row()
    {
        source ??= Addressables.LoadAssetAsync<GameObject>(Source).WaitForCompletion().GetComponent<CharacterWindow>();
        return TabRow(source).transform;
    }

    internal void Choose(int index)
    {
        if (index == Current)
            return;
        Current = index;
        for (int i = 0; i < tabs.Count; i++)
            tabs[i].UpdateState(i == index);
        chosen?.Invoke(index);
    }

    internal bool Next()
    {
        Choose((Current + 1) % tabs.Count);
        return true;
    }

    internal bool Previous()
    {
        Choose((Current + tabs.Count - 1) % tabs.Count);
        return true;
    }

    // With a gamepad, the bumpers' icons stand at the row's ends, as in the character window.
    internal void UpdateHints()
    {
        bool gamepad = LazyInput.IsGamepadActive;
        Hint(next, GameKey.NextTab, gamepad);
        Hint(previous, GameKey.PrevTab, gamepad);
    }

    private static void Hint(TMP_Text hint, GameKey key, bool gamepad)
    {
        if (hint == null)
            return;
        hint.gameObject.SetActive(gamepad);
        if (gamepad)
            hint.text = ControllerIconLibrary.GetIconId(key);
        hint.transform.SetAsLastSibling();
    }
}
