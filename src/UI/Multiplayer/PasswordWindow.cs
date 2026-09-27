using System;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// What joining a password game asks first: a copy of the game's settings window with a line saying whose game
// asks, or that the password given was wrong, the password's row in the game's own text field, and Cancel and
// Join, which waits for a password. Enter joins as Join does.
internal sealed class PasswordWindow : LazyWindow<LazyWidgetDataBase>
{
    private const int Longest = 32;
    private static PasswordWindow instance;

    private TMP_Text line;
    private LayoutElement lineSize;
    private TMP_InputField password;
    private bool centred;
    private UIDialogWindowButton join;
    private Action<string> joined;

    internal static bool IsOpen => instance != null && instance.IsShown;

    // Asks for the password of the named host's game, saying why when it is asked again.
    internal static void Open(string host, string again, Action<string> join)
    {
        if (instance == null)
            instance = Build();
        instance.joined = join;
        NativeWindow.SetText(instance.line, again ?? $"{NativeWindow.Literal(host)}'s game asks for a password.");
        instance.Open((LazyWidgetDataBase)null);
    }

    private static PasswordWindow Build()
    {
        var window = NativeWindow.Copy<PasswordWindow>(LazyUI.GetWindow<UIGameSettingsWindow>(), "Password");
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
        window.line.richText = true;
        window.line.enableAutoSizing = false;
        window.line.fontSize = name.fontSize;
        window.line.textWrappingMode = TextWrappingModes.Normal;
        window.lineSize = window.line.gameObject.AddComponent<LayoutElement>();
        window.lineSize.preferredWidth = ((RectTransform)row.transform).rect.width;
        var field = LazyUI.GetWindow<UIBugReportWindow>().transform.Find("GenericWIndowLayout/Content/Title").GetComponent<TMP_InputField>();
        window.password = NativeWindow.TextRow(row, field, content, "Password", "Type the password", Longest);
        window.password.onValueChanged.AddListener(_ => window.UpdateJoin());
        window.password.onSubmit.AddListener(_ => window.Join());
        var buttons = NativeWindow.ButtonRow(content);
        NativeWindow.Button(button, buttons, "Cancel", window.Cancel);
        window.join = NativeWindow.Button(button, buttons, "Join", window.Join, main: true);
        window.Init();
        return window;
    }

    public override void Open(LazyWidgetDataBase data)
    {
        base.Open(data);
        password.SetTextWithoutNotify(string.Empty);
        UpdateJoin();
        // As many lines as the line's text takes across the rows' width, measured once the window shows.
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
        // The player types at once.
        if (!LazyInput.IsGamepadActive)
            password.ActivateInputField();
    }

    // The row leaves room for the settings window's long names; this one is short, so its parts move by the same
    // whole units to stand, as drawn from the name's first letter to the field's end, in the middle of the window.
    private void Centre()
    {
        var row = (RectTransform)password.transform.parent;
        var name = row.Find("LeftName").GetComponent<TMP_Text>();
        name.ForceMeshUpdate();
        float left = row.InverseTransformPoint(name.transform.TransformPoint(name.textBounds.min)).x;
        var field = (RectTransform)password.transform;
        float right = row.InverseTransformPoint(field.TransformPoint(new Vector3(field.rect.xMax, 0f))).x;
        var shift = new Vector2(Mathf.Round(row.rect.center.x - (left + right) / 2f), 0f);
        foreach (RectTransform part in row)
            part.anchoredPosition += shift;
    }

    // The game's own buttons take whether they can be pressed as they are drawn, so Join is told again here.
    private void UpdateJoin() => join.LazyButton.interactable = password.text.Length > 0;

    private void Join()
    {
        if (password.text.Length == 0)
            return;
        string given = password.text;
        CloseWithoutCallback();
        joined?.Invoke(given);
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
