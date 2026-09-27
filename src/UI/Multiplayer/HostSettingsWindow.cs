using System;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// The host's second step, after picking the campaign: the settings the game is hosted with, as option
// rows in a copy of the game's settings window. Back returns to the campaigns and Next opens the lobby.
// Picking the campaign first lets a campaign keep settings that must not change once it has begun. A password
// game shows a row for its password, typed in the game's own text field, and Next waits for one.
internal sealed class HostSettingsWindow : LazyWindow<LazyWidgetDataBase>
{
    private static readonly string[] OnOff = { "Off", "On" };
    // The arrow buttons draw their faces over 14 of their 25 units; the rest of their art is clear.
    private const float ArrowFace = 14f;
    private static HostSettingsWindow instance;

    private UISwitchButton players;
    private UISwitchButton visibility;
    private UISwitchButton cheats;
    private TMP_InputField password;
    private UIDialogWindowButton nextButton;
    private HostSettings settings;
    private Action<HostSettings> back;
    private Action<HostSettings> next;
    private bool centred;

    // Back and Next each take the settings as the host left them.
    internal static void Open(HostSettings current, Action<HostSettings> back, Action<HostSettings> next)
    {
        if (instance == null)
            instance = Build();
        instance.settings = current.Copy();
        instance.back = back;
        instance.next = next;
        instance.Open(null);
    }

    private static HostSettingsWindow Build()
    {
        var window = NativeWindow.Copy<HostSettingsWindow>(LazyUI.GetWindow<UIGameSettingsWindow>(), "Host Settings");
        var content = NativeWindow.Content(window);
        var row = NativeWindow.Keep(window, content.Find("VoiceOverSwitchBtn").GetComponent<UISwitchButton>());
        var button = NativeWindow.Keep(window, content.Find("DialogueButtonPrefab").GetComponent<UIDialogWindowButton>());
        NativeWindow.ClearContent(window);

        var counts = new string[CoopSession.MaxPlayers - HostSettings.FewestPlayers + 1];
        for (int i = 0; i < counts.Length; i++)
            counts[i] = (HostSettings.FewestPlayers + i).ToString();
        window.players = NativeWindow.Choice(row, content, "Players", counts, 0,
            index => window.settings.Players = HostSettings.FewestPlayers + index);
        window.visibility = NativeWindow.Choice(row, content, "Visibility", Enum.GetNames(typeof(HostSettings.Access)), 0, index =>
        {
            window.settings.Visibility = (HostSettings.Access)index;
            window.ShowPassword();
        });
        var field = LazyUI.GetWindow<UIBugReportWindow>().transform.Find("GenericWIndowLayout/Content/Title").GetComponent<TMP_InputField>();
        window.password = NativeWindow.TextRow(row, field, content, "Password", "Type a password", HostSettings.PasswordLength);
        window.password.onValueChanged.AddListener(text =>
        {
            window.settings.Password = text;
            window.UpdateNext();
        });
        window.cheats = NativeWindow.Choice(row, content, "Cheats", OnOff, 0, index => window.settings.Cheats = index == 1);
        var buttons = NativeWindow.ButtonRow(content);
        NativeWindow.Button(button, buttons, LLBase.L("tip_back"), window.Back);
        window.nextButton = NativeWindow.Button(button, buttons, "Next", window.Next, main: true);
        window.Init();
        return window;
    }

    public override void Open(LazyWidgetDataBase data)
    {
        base.Open(data);
        if (!centred)
        {
            CentreRows();
            centred = true;
        }
        players.UpdateField(settings.Players - HostSettings.FewestPlayers, fireCallback: false);
        visibility.UpdateField((int)settings.Visibility, fireCallback: false);
        cheats.UpdateField(settings.Cheats ? 1 : 0, fireCallback: false);
        password.SetTextWithoutNotify(settings.Password);
        ShowPassword();
        GamepadNavigationController.ReinitItems(focusOnFirstActive: true);
    }

    // The password's row shows for a password game only.
    private void ShowPassword()
    {
        password.transform.parent.gameObject.SetActive(settings.Visibility == HostSettings.Access.Password);
        UpdateNext();
        ((RectTransform)transform).RefreshContentFitter();
    }

    // The game's own buttons take whether they can be pressed as they are drawn, so Next is told again here.
    private void UpdateNext() =>
        nextButton.LazyButton.interactable = settings.Visibility != HostSettings.Access.Password || settings.Password.Length > 0;

    // The native rows leave room for the settings window's long names. These are short, so every row's parts
    // move by the same whole units to stand, as drawn from the first letter of the longest name to the right
    // arrow's face, in the middle of the window.
    private void CentreRows()
    {
        var rows = new[] { players, visibility, cheats };
        var typed = password.transform.parent;
        float left = float.MaxValue, right = float.MinValue, middle = 0f;
        foreach (var row in rows)
        {
            var rect = (RectTransform)row.transform;
            var name = NativeWindow.Find<TMP_Text>(rect, "LeftName");
            name.ForceMeshUpdate();
            left = Mathf.Min(left, rect.InverseTransformPoint(name.transform.TransformPoint(name.textBounds.min)).x);
            var arrow = (RectTransform)rect.Find("ToRight");
            right = Mathf.Max(right, rect.InverseTransformPoint(arrow.TransformPoint(arrow.rect.center)).x + ArrowFace / 2f);
            middle = rect.rect.center.x;
        }
        var shift = new Vector2(Mathf.Round(middle - (left + right) / 2f), 0f);
        foreach (var row in rows)
            foreach (RectTransform part in row.transform)
                part.anchoredPosition += shift;
        foreach (RectTransform part in typed)
            part.anchoredPosition += shift;
    }

    protected override bool OnPressedBack()
    {
        Back();
        return true;
    }

    private void Back()
    {
        CloseWithoutCallback();
        back?.Invoke(settings.Copy());
    }

    private void Next()
    {
        if (settings.Visibility == HostSettings.Access.Password && settings.Password.Length == 0)
            return;
        CloseWithoutCallback();
        next?.Invoke(settings.Copy());
    }

    protected override void InitCloseButton(LazyButton button) => button.onClick.AddListener(Back);

    protected override void TestDraw()
    {
    }
}
