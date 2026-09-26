using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The host's first step: the campaign to host, from the native save list with its new-game entry. A pick
// stays lit with a gold copy of the entry's native hover frame, and Next moves on to the host's settings;
// picking the lit entry again moves on as well, as a gamepad's select or a double click does.
[HarmonyPatch(typeof(UISaveSlotsWindow))]
internal static class CampaignPicker
{
    private const string Header = "Host Game";
    private static readonly Color PickColor = new Color(1f, 0.82f, 0.35f);
    private static UIMainMenuWindow menu;
    private static bool picking;
    private static bool hasPick;
    private static SaveSlotData pick;
    private static UIDialogWindowButton next;
    // The frame's splitter under the header, and where the native list has it.
    private static RectTransform splitter;
    private static Vector2 splitterAnchorMin, splitterAnchorMax, splitterPosition;
    private static float splitterFromTop;

    // Opens the list; returning from the settings, the campaign picked before stays picked.
    internal static void Open(UIMainMenuWindow mainMenu, bool keepPick = false)
    {
        menu = mainMenu;
        picking = true;
        hasPick &= keepPick;
        mainMenu.Close();
        LazyUI.GetWindow<UISaveSlotsWindow>().Open(null);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UISaveSlotsWindow.Open))]
    private static void Opened(UISaveSlotsWindow __instance)
    {
        var label = NativeWindow.Find<TMP_Text>(__instance.transform, "GenericWIndowLayout/Frame/HeaderGroup/Header");
        var localized = label.GetComponent<LocalizedLabel>();
        if (!picking)
        {
            // The same window serves the game's own Continue, at its own size at once.
            if (next != null)
            {
                next.transform.parent.gameObject.SetActive(false);
                PlaceSplitter(false);
                ((RectTransform)__instance.transform).RefreshContentFitter();
            }
            if (localized.IgnoreLocalize)
            {
                localized.IgnoreLocalize = false;
                localized.Localize();
            }
            return;
        }
        NativeWindow.SetText(label, Header);
        if (next == null)
            next = AddNext(__instance);
        next.transform.parent.gameObject.SetActive(true);
        PlaceSplitter(true);
        Light(__instance);
        ((RectTransform)__instance.transform).RefreshContentFitter();
    }

    private static UIDialogWindowButton AddNext(UISaveSlotsWindow window)
    {
        splitter = (RectTransform)window.transform.Find("GenericWIndowLayout/Frame/FrameDown");
        splitterAnchorMin = splitter.anchorMin;
        splitterAnchorMax = splitter.anchorMax;
        splitterPosition = splitter.anchoredPosition;
        splitterFromTop = ((RectTransform)splitter.parent).rect.height * (1f - splitterAnchorMin.y) - splitterPosition.y;
        var template = LazyUI.GetWindow<UIGameSettingsWindow>().transform
            .Find("GenericWIndowLayout/Content/DialogueButtonPrefab").GetComponent<UIDialogWindowButton>();
        var row = NativeWindow.ButtonRow(window.transform.Find("GenericWIndowLayout"));
        return NativeWindow.Button(template, row, "Next", Next, available: () => hasPick);
    }

    [HarmonyPrefix]
    [HarmonyPatch("OnSaveSlotSelectedHandler")]
    private static bool Picked(UISaveSlotsWindow __instance, SaveSlotData slotData)
    {
        if (!picking || !__instance.IsShownAndTop)
            return true;
        if (hasPick && Same(pick, slotData))
        {
            Next();
            return false;
        }
        hasPick = true;
        pick = slotData;
        Light(__instance);
        return false;
    }

    // The splitter stands by the frame's middle, which the Next row moves down; while picking it stays under
    // the header, where the native list has it.
    private static void PlaceSplitter(bool picking)
    {
        if (splitter == null)
            return;
        splitter.anchorMin = picking ? new Vector2(splitterAnchorMin.x, 1f) : splitterAnchorMin;
        splitter.anchorMax = picking ? new Vector2(splitterAnchorMax.x, 1f) : splitterAnchorMax;
        splitter.anchoredPosition = picking ? new Vector2(splitterPosition.x, -splitterFromTop) : splitterPosition;
    }

    // Only the picked entry stays lit; the native frame itself follows the pointer. Next waits for a pick,
    // and a pick deleted from the list is no longer one.
    private static void Light(UISaveSlotsWindow window)
    {
        bool listed = false;
        foreach (var slot in window.GetComponentsInChildren<UISaveSlot>(true))
        {
            bool picked = picking && hasPick && slot.gameObject.activeSelf && Same(slot.LinkedSaveSlot, pick);
            if (picked)
            {
                pick = slot.LinkedSaveSlot;
                listed = true;
            }
            var lit = Frame(slot);
            if (lit != null)
                lit.SetActive(picked);
        }
        hasPick &= listed;
        if (next != null)
            next.LazyButton.interactable = hasPick;
    }

    // The list reads the saves again each time it opens, so a campaign is known by its save's name.
    internal static bool Same(SaveSlotData first, SaveSlotData second) =>
        first == null ? second == null : second != null && first.slotName == second.slotName && first.isDemoSave == second.isDemoSave;

    private static GameObject Frame(UISaveSlot slot)
    {
        var picked = slot.transform.Find("Picked");
        if (picked != null)
            return picked.gameObject;
        var hover = slot.transform.Find("Selection");
        if (hover == null)
            return null;
        picked = Object.Instantiate(hover, slot.transform);
        picked.name = "Picked";
        picked.SetSiblingIndex(hover.GetSiblingIndex());
        picked.GetComponent<Image>().color = PickColor;
        return picked.gameObject;
    }

    private static void Next()
    {
        var window = LazyUI.GetWindow<UISaveSlotsWindow>();
        Light(window);
        if (!hasPick)
            return;
        picking = false;
        window.CloseWithoutCallback();
        MultiplayerMenu.Configure(menu, pick);
    }

    [HarmonyPrefix]
    [HarmonyPatch("ReturnToPreviousWindow")]
    private static bool Back()
    {
        if (!picking)
            return true;
        picking = false;
        hasPick = false;
        menu.Open(null);
        return false;
    }

    // A pick lit here must not show when the game lists its saves itself.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UISaveSlotsWindow.Hide))]
    private static void Hidden(UISaveSlotsWindow __instance)
    {
        foreach (var slot in __instance.GetComponentsInChildren<UISaveSlot>(true))
        {
            var picked = slot.transform.Find("Picked");
            if (picked != null)
                picked.gameObject.SetActive(false);
        }
    }
}
