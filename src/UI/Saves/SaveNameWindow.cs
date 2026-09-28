using System;
using GYK2.TombManyKeepers.Features.ManualSaves;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Saves;

// What a new save asks first, as GYK1's did: a copy of the game's settings window with a line asking for the save's
// name, the name's row in the game's own text field holding a name to keep or change, and Cancel and Save, which waits
// for a name. Enter saves as Save does.
internal sealed class SaveNameWindow : LazyWindow<LazyWidgetDataBase>
{
    private static SaveNameWindow instance;

    private TMP_Text line;
    private LayoutElement lineSize;
    private TMP_InputField field;
    private bool centred;
    private UIDialogWindowButton save;
    private Action<string> named;

    internal static bool IsOpen => instance != null && instance.IsShown;

    // Asks for a new save's name, starting from this one.
    internal static void Open(string suggested, Action<string> name)
    {
        if (instance == null)
            instance = Build();
        instance.named = name;
        instance.Open((LazyWidgetDataBase)null);
        instance.field.SetTextWithoutNotify(suggested);
        instance.UpdateSave();
        // The whole name is chosen, to keep as it is or to type over.
        if (!LazyInput.IsGamepadActive)
        {
            instance.field.ActivateInputField();
            instance.field.selectionAnchorPosition = 0;
            instance.field.selectionFocusPosition = suggested.Length;
        }
    }

    private static SaveNameWindow Build()
    {
        var window = NativeWindow.Copy<SaveNameWindow>(LazyUI.GetWindow<UIGameSettingsWindow>(), "Save Game");
        var content = NativeWindow.Content(window);
        var row = NativeWindow.Keep(window, content.Find("VoiceOverSwitchBtn").GetComponent<UISwitchButton>());
        var button = NativeWindow.Keep(window, content.Find("DialogueButtonPrefab").GetComponent<UIDialogWindowButton>());
        NativeWindow.ClearContent(window);

        // The line takes a row's name's letters, across the rows' width and in their middle.
        var name = row.transform.Find("LeftName").GetComponent<TMP_Text>();
        window.line = Instantiate(name, content);
        window.line.name = "Line";
        DestroyImmediate(window.line.GetComponent<LocalizedLabel>());
        DestroyImmediate(window.line.GetComponent<TextStyleComponent>());
        window.line.alignment = TextAlignmentOptions.Center;
        window.line.enableAutoSizing = false;
        window.line.fontSize = name.fontSize;
        window.line.textWrappingMode = TextWrappingModes.Normal;
        window.line.text = "Name the new save.";
        window.lineSize = window.line.gameObject.AddComponent<LayoutElement>();
        window.lineSize.preferredWidth = ((RectTransform)row.transform).rect.width;
        var native = LazyUI.GetWindow<UIBugReportWindow>().transform.Find("GenericWIndowLayout/Content/Title").GetComponent<TMP_InputField>();
        window.field = NativeWindow.TextRow(row, native, content, "Name", "Type a name", SaveNames.Longest);
        window.field.onValueChanged.AddListener(_ => window.UpdateSave());
        window.field.onSubmit.AddListener(_ => window.Save());
        var buttons = NativeWindow.ButtonRow(content);
        NativeWindow.Button(button, buttons, "Cancel", window.Cancel);
        window.save = NativeWindow.Button(button, buttons, "Save", window.Save, main: true);
        window.Init();
        return window;
    }

    public override void Open(LazyWidgetDataBase data)
    {
        base.Open(data);
        float height = line.GetPreferredValues(line.text, lineSize.preferredWidth, 0f).y;
        lineSize.preferredHeight = height;
        line.rectTransform.sizeDelta = new Vector2(lineSize.preferredWidth, height);
        ((RectTransform)transform).RefreshContentFitter();
        if (!centred)
        {
            Centre();
            centred = true;
        }
        GamepadNavigationController.ReinitItems(focusOnFirstActive: LazyInput.IsGamepadActive);
    }

    // The row leaves room for the settings window's long names; this one is short, so its parts move by the same
    // whole units to stand, as drawn from the name's first letter to the field's end, in the middle of the window.
    private void Centre()
    {
        var row = (RectTransform)field.transform.parent;
        var name = row.Find("LeftName").GetComponent<TMP_Text>();
        name.ForceMeshUpdate();
        float left = row.InverseTransformPoint(name.transform.TransformPoint(name.textBounds.min)).x;
        var box = (RectTransform)field.transform;
        float right = row.InverseTransformPoint(box.TransformPoint(new Vector3(box.rect.xMax, 0f))).x;
        var shift = new Vector2(Mathf.Round(row.rect.center.x - (left + right) / 2f), 0f);
        foreach (RectTransform part in row)
            part.anchoredPosition += shift;
    }

    // The game's own buttons take whether they can be pressed as they are drawn, so Save is told again here.
    private void UpdateSave() => save.LazyButton.interactable = field.text.Trim().Length > 0;

    private void Save()
    {
        string name = field.text.Trim();
        if (name.Length == 0)
            return;
        CloseWithoutCallback();
        named?.Invoke(name);
    }

    private void Cancel() => CloseWithoutCallback();

    protected override bool OnPressedBack()
    {
        Cancel();
        return true;
    }

    protected override void InitCloseButton(LazyButton button) => button.onClick.AddListener(Cancel);

    protected override void TestDraw()
    {
    }
}
