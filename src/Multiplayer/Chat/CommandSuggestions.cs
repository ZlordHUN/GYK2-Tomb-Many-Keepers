using System;
using GYK2.TombManyKeepers.Multiplayer.Cheats;
using GYK2.TombManyKeepers.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.Multiplayer.Chat;

// GYK1's suggestions for a command typed into the game's chat: the first completion, or the one picked, shows as grey
// ghost text over the line, and a list of the completions grows upward from the chat, a slice of eight at a time with
// the picked one marked. Tab completes the line with it, Up and Down pick another.
internal sealed class CommandSuggestions : MonoBehaviour
{
    private const int Rows = 8;
    // GYK1's colours: the ghost text a cool grey, the list's lines on near-opaque black, its footer dimmer.
    private static readonly Color Ghost = new Color(0.48f, 0.58f, 0.64f), Back = new Color(0f, 0f, 0f, 0.88f),
        Option = new Color(0.86f, 0.9f, 0.94f), Footer = new Color(0.64f, 0.7f, 0.75f);
    private const float Padding = 2f;
    private TMP_InputField field;
    private TMP_Text ghost;
    private RectTransform list;
    private readonly TMP_Text[] options = new TMP_Text[Rows];
    private TMP_Text footer;
    private float pitch;
    private string source;
    private ChatCommands.Completion[] completions = Array.Empty<ChatCommands.Completion>();
    private int picked, first;
    private bool toEnd;

    // The ghost text shares the line's view and look; the list stands on the chat's canvas, from this height up.
    internal static CommandSuggestions Attach(TMP_InputField field, RectTransform canvas, Vector2 at, float width, TMP_Text template, float pitch)
    {
        var suggestions = field.gameObject.AddComponent<CommandSuggestions>();
        suggestions.field = field;
        suggestions.pitch = pitch;
        suggestions.Build(canvas, at, width, template);
        return suggestions;
    }

    private void Build(RectTransform canvas, Vector2 at, float width, TMP_Text template)
    {
        var text = field.textComponent;
        ghost = Instantiate(text, text.transform.parent);
        ghost.name = "Suggestion";
        ghost.transform.SetSiblingIndex(text.transform.GetSiblingIndex());
        ghost.color = Ghost;
        ghost.richText = false;
        ghost.raycastTarget = false;
        ghost.text = string.Empty;

        list = (RectTransform)new GameObject("Suggestions", typeof(RectTransform), typeof(Image)) { layer = canvas.gameObject.layer }.transform;
        list.SetParent(canvas, false);
        list.anchorMin = list.anchorMax = list.pivot = Vector2.zero;
        list.anchoredPosition = at;
        list.sizeDelta = new Vector2(width, pitch + 2f * Padding);
        var back = list.GetComponent<Image>();
        back.color = Back;
        back.raycastTarget = false;
        for (int row = 0; row < Rows; row++)
            options[row] = Line(template, "Option " + row, Option);
        footer = Line(template, "Footer", Footer);
        list.gameObject.SetActive(false);
    }

    // One line of the list, in the chat's font and outline, cut short where it runs past the list's edge.
    private TMP_Text Line(TMP_Text template, string name, Color color)
    {
        var line = Instantiate(template, list);
        line.name = name;
        line.enabled = true;
        line.color = color;
        // Names and ids show as they are.
        line.richText = false;
        line.alignment = TextAlignmentOptions.BottomLeft;
        line.textWrappingMode = TextWrappingModes.NoWrap;
        line.overflowMode = TextOverflowModes.Ellipsis;
        line.margin = Vector4.zero;
        line.raycastTarget = false;
        var rect = line.rectTransform;
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.sizeDelta = new Vector2(list.sizeDelta.x - 2f * Padding, pitch);
        return line;
    }

    // Tab: a command's line takes the ghost's completion, and the next word's suggestions follow. Another line is no
    // command's, nor is any while this player's cheats are off, and Tab is the chat's own.
    internal bool Complete()
    {
        string text = field.text ?? string.Empty;
        if (!ChatCommands.Suggests(text))
            return false;
        if (!AtEnd())
            return true;
        if (text != source)
            Refresh(text);
        if (completions.Length == 0)
            return true;
        field.text = completions[Mathf.Clamp(picked, 0, completions.Length - 1)].Text;
        toEnd = true;
        source = null;
        return true;
    }

    // Up and Down pick another suggestion, the line as typed.
    internal bool Navigate()
    {
        int step = Input.GetKeyDown(KeyCode.DownArrow) ? 1 : Input.GetKeyDown(KeyCode.UpArrow) ? -1 : 0;
        if (step == 0 || completions.Length == 0 || source != field.text)
            return false;
        picked = (picked + step + completions.Length) % completions.Length;
        if (picked < first)
            first = picked;
        else if (picked >= first + Rows)
            first = picked - Rows + 1;
        toEnd = true;
        Present();
        return true;
    }

    internal void Hide()
    {
        source = null;
        completions = Array.Empty<ChatCommands.Completion>();
        ghost.text = string.Empty;
        field.textComponent.alpha = 1f;
        list.gameObject.SetActive(false);
    }

    // After the field has taken this frame's keys: the caret goes back to the end, and the suggestions follow the line.
    private void LateUpdate()
    {
        if (!field.isFocused)
        {
            if (source != null || list.gameObject.activeSelf)
                Hide();
            return;
        }
        if (toEnd)
        {
            toEnd = false;
            field.MoveTextEnd(false);
        }
        string text = field.text ?? string.Empty;
        if (!AtEnd() || !ChatCommands.Suggests(text))
        {
            if (source != null)
                Hide();
            return;
        }
        if (text != source)
            Refresh(text);
        else
            Align();
    }

    private bool AtEnd() => field.stringPosition == (field.text ?? string.Empty).Length && field.selectionAnchorPosition == field.selectionFocusPosition;

    private void Refresh(string text)
    {
        source = text;
        picked = first = 0;
        completions = ChatCommands.Complete(text);
        Present();
    }

    private void Present()
    {
        string text = field.text ?? string.Empty;
        string completion = completions.Length > 0 ? completions[Mathf.Clamp(picked, 0, completions.Length - 1)].Text : string.Empty;
        // A completion spelled otherwise than the line, as another case, shows over it.
        bool over = completion.Length > 0 && !completion.StartsWith(text, StringComparison.Ordinal);
        ghost.text = completion;
        ghost.transform.SetSiblingIndex(field.textComponent.transform.GetSiblingIndex() + (over ? 1 : 0));
        field.textComponent.alpha = over ? 0f : 1f;
        Align();
        Draw();
    }

    // The field moves its text within the view to keep the caret in sight; the ghost moves with it.
    private void Align()
    {
        var text = field.textComponent.rectTransform;
        var rect = ghost.rectTransform;
        rect.anchorMin = text.anchorMin;
        rect.anchorMax = text.anchorMax;
        rect.pivot = text.pivot;
        rect.anchoredPosition = text.anchoredPosition;
        rect.sizeDelta = text.sizeDelta;
    }

    private void Draw()
    {
        if (completions.Length == 0)
        {
            list.gameObject.SetActive(false);
            return;
        }
        first = Mathf.Clamp(first, 0, Mathf.Max(0, completions.Length - Rows));
        int shown = Mathf.Min(Rows, completions.Length - first);
        for (int row = 0; row < Rows; row++)
        {
            var line = options[row];
            line.gameObject.SetActive(row < shown);
            if (row >= shown)
                continue;
            int index = first + row;
            NativeWindow.SetText(line, (index == picked ? "> " : "  ") + completions[index].Label);
            // The first of the slice stands on top, above the footer.
            line.rectTransform.anchoredPosition = new Vector2(Padding, Padding + (shown - row) * pitch);
        }
        string counted = completions.Length > Rows ? $"  {first + 1}-{first + shown}/{completions.Length}" : string.Empty;
        NativeWindow.SetText(footer, "  Up/Down: select  Tab: complete" + counted);
        footer.rectTransform.anchoredPosition = new Vector2(Padding, Padding);
        list.sizeDelta = new Vector2(list.sizeDelta.x, (shown + 1) * pitch + 2f * Padding);
        list.gameObject.SetActive(true);
    }
}
