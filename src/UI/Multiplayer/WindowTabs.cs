using System;
using System.Collections.Generic;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// A window header with page tabs, copied from the game's character window: its plate and ornaments, a row of
// tabs with the chosen one inset and diamonds between them, the gamepad's bumper hints and the close button.
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

    // Replaces the window frame's own header with the tabbed one.
    internal WindowTabs(Transform frame, IReadOnlyList<string> names, Action<int> chosen)
    {
        this.chosen = chosen;
        source ??= Addressables.LoadAssetAsync<GameObject>(Source).WaitForCompletion().GetComponent<CharacterWindow>();
        var sourceRow = TabRow(source).transform;
        var own = frame.Find("HeaderGroup");
        var header = UnityEngine.Object.Instantiate(sourceRow.parent.gameObject, frame).transform;
        header.name = "HeaderGroup";
        header.SetSiblingIndex(own.GetSiblingIndex());
        UnityEngine.Object.DestroyImmediate(own.gameObject);
        Close = header.Find("CloseButton").GetComponent<LazyButton>();

        var row = header.Find(sourceRow.name);
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
        next = row.Find("NextTabGamepadHelper").GetComponent<TMP_Text>();
        previous = row.Find("PrevTabGamepadHelper").GetComponent<TMP_Text>();
        UpdateHints();
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
        hint.gameObject.SetActive(gamepad);
        if (gamepad)
            hint.text = ControllerIconLibrary.GetIconId(key);
        hint.transform.SetAsLastSibling();
    }
}
