using System;
using System.Globalization;
using System.Text;
using GYK2.TombManyKeepers.Multiplayer.Cheats;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Network.Session;
using GYK2.TombManyKeepers.UI;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.Multiplayer.Chat;

// The game's chat, as GYK1's: the chat log's latest lines at the bottom left above the line to type in, each with the
// time it was said and its player's name in their colour, notices in italics. The lines stand on the game itself with
// the eight-sided outline the game gives its text over the world, and on see-through black while the player types.
// Enter or Y, keys the game leaves free, opens the line unless a window is open or the game loads; Enter sends it and
// Escape drops it, and meanwhile the game's keys wait. The chat shows the tab chosen in it or in the lobby, Players or
// NPCs, and while the line is open its tabs stand on the panel and Tab turns them. A line starting with / is a command,
// GYK1's cheats (ChatCommands): its suggestions show as it is typed, Tab completes it, and sent, it runs instead of being
// said. The mouse wheel over the lines scrolls back through the log as
// the lobby's chat does; the chat follows its newest line unless scrolled back, and returns to it when the player sends
// a line or it has faded. It fades once nothing has been said, typed or scrolled for ten seconds and comes back with
// the next line or key. It shows over the game's windows and its fades to black, where a new campaign opens, and under
// the speech shown in the dark and the loading screen.
internal sealed class ChatOverlay : MonoBehaviour
{
    // Over windows (400-455) and fades (800), under speech over the dark (900) and the loading screen.
    private const int SortingOrder = 850;
    // The HUD's distance from the screen's edges, in the game's UI units, which grow with the screen by whole pixels as
    // the HUD does; the width stays clear of the hotbar in the middle of the narrowest screens the game offers.
    private const float Margin = 10f, Width = 208f;
    // Inside the panel the lines' view stands 10 units in from its left and right edges, above the line to type in along
    // its bottom, and the top line at least 8 units below the panel's top: nine lines of the game's small font, 12 units
    // each. The view's edges fade what they cut over two pixels, so the lines and their outline keep clear of them, as
    // the native field keeps its text: 2 units in from the sides, and the view reaches 2 units above the top line and 1
    // below the bottom one, the unit each outline takes beyond its line's letters and one more; scrolled to the oldest
    // line, the lines keep those units above them. Only lines wholly in the view are drawn, so no part of a line beyond
    // it shows on the game. The wheel scrolls two lines a notch.
    private const float Inset = 10f, TextMargin = 2f, ClipAbove = 2f, ClipBelow = 1f, TopInset = 8f;
    private const float FieldBottom = 5f, FieldHeight = 18f, LogBottom = 22f;
    private const float Height = LogBottom + 9 * 12f + TopInset;
    private const int WheelLines = 2;
    // Lines measured without a height limit, as TMP measures them.
    private const float AnyHeight = 32767f;
    // GYK1's times: shown for ten seconds after the last line or key, then faded out; once a window closes, Enter waits a
    // moment, so the press that closed it does not open the chat.
    private const float Shown = 10f, Fading = 0.25f, Settling = 0.15f;
    private const string Prompt = "[ Press Enter or Y to chat ]";
    // NPCs only shows what was said with the game's people; the players chat under Players.
    private const string ReadOnly = "[ Tab to Players to chat ]";
    // The see-through black under the lines while the player types, and the grey of the game's secondary text for the
    // prompt, which stands on the game itself.
    private static readonly Color Shade = new Color(0f, 0f, 0f, 0.7f), PromptColor = new Color(0.588f, 0.553f, 0.533f);
    // The tabs stand on the panel's top while the player types, each around its name: the chosen one on the panel's own
    // black, the other on a lighter shade in grey, and the key that turns them beside them.
    private const float TabHeight = 14f, TabPadding = 5f, TabGap = 2f;
    private const string TabKey = "[Tab]";
    private static readonly Color OtherTab = new Color(0f, 0f, 0f, 0.4f);
    // GYK1's names for players without a colour; a character's name in a warm gold, grey while unknown.
    private const string OwnName = "#00FF00", OtherName = "#00FFFF", NpcName = "#E0B878", UnknownName = "#A09A94";
    private const string Template = "GenericWIndowLayout/Content/";
    private static ChatOverlay instance;
    private CanvasGroup group;
    private GameObject panel;
    private Image background;
    private Material outlined;
    private TMP_Text log;
    private ScrollRect scroll;
    private readonly Vector3[] viewCorners = new Vector3[4];
    private TMP_InputField field;
    private TMP_Text prompt;
    private GameObject tabStrip;
    private CommandSuggestions suggestions;
    private readonly Image[] tabBacks = new Image[ChatTabs.Names.Length];
    private readonly TMP_Text[] tabNames = new TMP_Text[ChatTabs.Names.Length];
    // How many lines the chosen tab had when the player last saw it.
    private int seen = -1;
    private float lastActive;
    private bool typing;
    private bool present;
    private bool reachable;
    private float settled;
    private int closedFrame = -1;

    // The chat opens with the game this game's player plays in the session, showing what was said so far.
    internal static void Follow()
    {
        if (instance != null)
            return;
        var bubbles = UIObjectBubbleManager.Instance;
        if (bubbles == null)
            return;
        // Its canvas takes the pointer for the wheel over the lines, and only there.
        var root = Made("Multiplayer Chat", bubbles.transform.parent, bubbles.gameObject.layer, typeof(Canvas), typeof(CanvasGroup),
            typeof(GraphicRaycaster));
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;
        var canvas = root.GetComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = SortingOrder;
        instance = root.gameObject.AddComponent<ChatOverlay>();
        instance.Build();
        ChatLog.Changed += instance.Heard;
        NpcNames.Changed += instance.Draw;
        instance.Heard();
    }

    internal static void Close()
    {
        if (instance == null)
            return;
        Destroy(instance.gameObject);
        instance = null;
    }

    private void OnDestroy()
    {
        ChatLog.Changed -= Heard;
        NpcNames.Changed -= Draw;
        ChatTabs.Changed -= ShowTab;
        Destroy(outlined);
    }

    private void Build()
    {
        int layer = gameObject.layer;
        group = GetComponent<CanvasGroup>();
        var back = Made("Panel", transform, layer, typeof(Image));
        back.anchorMin = back.anchorMax = back.pivot = Vector2.zero;
        back.anchoredPosition = new Vector2(Margin, Margin);
        back.sizeDelta = new Vector2(Width, Height);
        background = back.GetComponent<Image>();
        background.color = Shade;
        background.raycastTarget = false;
        background.enabled = false;
        panel = back.gameObject;

        var content = LazyUI.GetWindow<UIBugReportWindow>().transform;
        field = Field(NativeWindow.Find<TMP_InputField>(content, Template + "Title"), back);
        // The typed line's text starts and ends where the log's lines do.
        var (left, right) = TextInsets(field);
        var line = (RectTransform)field.transform;
        line.offsetMin = new Vector2(Inset + TextMargin - left, FieldBottom);
        line.offsetMax = new Vector2(right - Inset - TextMargin, FieldBottom + FieldHeight);
        var area = Made("Log", back, layer, typeof(RectMask2D));
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(Inset, LogBottom - ClipBelow);
        log = Log(NativeWindow.Find<TMP_InputField>(content, Template + "Steps").textComponent, area);
        // As many whole lines as fit below the top inset; another language's font may give fewer.
        float pitch = log.GetPreferredValues("[\n[", AnyHeight, AnyHeight).y - log.GetPreferredValues("[", AnyHeight, AnyHeight).y;
        int lines = Mathf.Max(1, Mathf.FloorToInt((Height - LogBottom - TopInset + 0.5f) / pitch));
        area.offsetMax = new Vector2(-Inset, -(Height - LogBottom - lines * pitch - ClipAbove));
        scroll = Scrolling(log, lines * pitch + ClipAbove + ClipBelow, WheelLines * pitch);
        log.OnPreRenderText += DrawWholeLines;

        // The native text's font and outline colour, drawn with the game's eight-sided outline, as its hints over the
        // world are, so the lines read on any ground.
        outlined = new Material(log.fontSharedMaterial)
        {
            name = "Multiplayer Chat Outline",
            shader = TMPShaderSetup.FindShader(outline: true, secondOutline: false, shadow: false, overlayTexture: false, eightSide: true)
        };
        log.fontSharedMaterial = outlined;
        field.textComponent.fontSharedMaterial = outlined;
        prompt.font = log.font;
        prompt.fontSharedMaterial = outlined;
        prompt.color = PromptColor;

        tabStrip = Tabs(back, layer);
        // A command's suggestions grow upward from above the tabs.
        suggestions = CommandSuggestions.Attach(field, (RectTransform)transform, new Vector2(Margin, Margin + Height + TabHeight + TabGap), Width,
            prompt, pitch);
        MarkTab();
        ChatTabs.Changed += ShowTab;
    }

    // The tabs above the panel, shown while the player types.
    private GameObject Tabs(RectTransform panel, int layer)
    {
        var strip = Made("Tabs", panel, layer);
        strip.anchorMin = strip.anchorMax = new Vector2(0f, 1f);
        strip.pivot = Vector2.zero;
        strip.anchoredPosition = Vector2.zero;
        strip.sizeDelta = new Vector2(Width, TabHeight);
        float x = 0f;
        for (int i = 0; i < ChatTabs.Names.Length; i++)
        {
            var tab = Made(ChatTabs.Names[i], strip, layer, typeof(Image));
            tabBacks[i] = tab.GetComponent<Image>();
            tabBacks[i].raycastTarget = false;
            tabNames[i] = Label(tab, ChatTabs.Names[i]);
            x = Place(tab, x, tabNames[i].GetPreferredValues(ChatTabs.Names[i], AnyHeight, AnyHeight).x + 2f * TabPadding) + TabGap;
        }
        var key = Made("Key", strip, layer);
        var hint = Label(key, TabKey);
        hint.color = PromptColor;
        Place(key, x + TabPadding - TabGap, hint.GetPreferredValues(TabKey, AnyHeight, AnyHeight).x);
        strip.gameObject.SetActive(false);
        return strip.gameObject;
    }

    // A name across its part of the strip, in the prompt's font and outline.
    private TMP_Text Label(RectTransform parent, string text)
    {
        var label = Instantiate(prompt, parent);
        label.name = "Label";
        // The field turns its prompt off while a line is typed.
        label.enabled = true;
        var rect = label.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        label.margin = Vector4.zero;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        NativeWindow.SetText(label, text);
        return label;
    }

    // A part of the strip from its left at this distance, as wide as given; where the next may start.
    private static float Place(RectTransform part, float left, float width)
    {
        part.anchorMin = part.anchorMax = part.pivot = Vector2.zero;
        part.anchoredPosition = new Vector2(left, 0f);
        part.sizeDelta = new Vector2(width, TabHeight);
        return left + width;
    }

    // The strip marks the chosen tab. NPCs takes no typing.
    private void MarkTab()
    {
        int chosen = (int)ChatTabs.Current;
        for (int i = 0; i < tabBacks.Length; i++)
        {
            tabBacks[i].color = i == chosen ? Shade : OtherTab;
            tabNames[i].color = i == chosen ? Color.white : PromptColor;
        }
        field.readOnly = ChatTabs.Current != ChatTabs.Tab.Players;
        ShowPrompt();
    }

    // The prompt shows while the line waits, and on NPCs while it is open, where nothing is typed.
    private void ShowPrompt() =>
        NativeWindow.SetText(prompt, !typing ? Prompt : ChatTabs.Current == ChatTabs.Tab.Players ? string.Empty : ReadOnly);

    // Another tab was chosen, here or in the lobby: it shows from its newest line.
    private void ShowTab()
    {
        MarkTab();
        Draw();
        Newest();
        seen = ChatLog.Said(ChatTabs.Current);
        lastActive = Time.unscaledTime;
    }

    // A copy of the native field's text, the chat's lines from the bottom up.
    private static TMP_Text Log(TMP_Text template, RectTransform area)
    {
        var log = Instantiate(template, area);
        log.name = "Text";
        DestroyImmediate(log.GetComponent<TextStyleComponent>());
        log.gameObject.SetActive(true);
        log.richText = true;
        log.parseCtrlCharacters = false;
        log.color = Color.white;
        log.alignment = TextAlignmentOptions.BottomLeft;
        log.textWrappingMode = TextWrappingModes.Normal;
        log.overflowMode = TextOverflowModes.Overflow;
        log.margin = new Vector4(TextMargin, ClipAbove, TextMargin, ClipBelow);
        return log;
    }

    // The lines scroll in their view, with the game's own smooth mouse wheel scrolling; they sit at the view's bottom,
    // by the line to type in, until they fill it. The wheel over them counts as the player being there.
    private ScrollRect Scrolling(TMP_Text log, float view, float wheel)
    {
        var content = log.rectTransform;
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;
        log.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        log.gameObject.AddComponent<LayoutElement>().minHeight = view;
        // The wheel over the lines reaches the view.
        log.raycastTarget = true;
        var scroll = content.parent.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = (RectTransform)content.parent;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        SmoothMouseWheelScroll.Ensure(scroll, wheel);
        scroll.onValueChanged.AddListener(_ =>
        {
            lastActive = Time.unscaledTime;
            // Lines move into and out of the view.
            log.ForceMeshUpdate();
        });
        return scroll;
    }

    // Lines the view cuts are not drawn: their letters and outline would show on the game beyond its edges.
    private void DrawWholeLines(TMP_TextInfo info)
    {
        if (scroll == null)
            return;
        var text = log.rectTransform;
        scroll.viewport.GetWorldCorners(viewCorners);
        float top = text.InverseTransformPoint(viewCorners[1]).y, bottom = text.InverseTransformPoint(viewCorners[0]).y;
        for (int row = 0; row < info.lineCount; row++)
        {
            var line = info.lineInfo[row];
            if (line.ascender <= top + 0.01f && line.descender >= bottom - 0.01f)
                continue;
            for (int index = line.firstCharacterIndex; index <= line.lastCharacterIndex; index++)
            {
                var character = info.characterInfo[index];
                if (!character.isVisible)
                    continue;
                var vertices = info.meshInfo[character.materialReferenceIndex].vertices;
                int at = character.vertexIndex;
                vertices[at + 1] = vertices[at + 2] = vertices[at + 3] = vertices[at];
            }
        }
    }

    // A copy of the native text field, without its cell, along the panel's bottom.
    private TMP_InputField Field(TMP_InputField template, RectTransform parent)
    {
        // Copied asleep, so the copy wakes with only the parts it keeps; the template's caret goes.
        bool awake = template.gameObject.activeSelf;
        template.gameObject.SetActive(false);
        var input = Instantiate(template, parent);
        template.gameObject.SetActive(awake);
        input.name = "Say";
        foreach (var caret in input.GetComponentsInChildren<TMP_SelectionCaret>(true))
            DestroyImmediate(caret.gameObject);
        // What the player types shows in white, as GYK1's did, kept as the chat hides and shows again.
        DestroyImmediate(input.textComponent.GetComponent<TextStyleComponent>());
        input.textComponent.color = Color.white;
        foreach (var item in input.GetComponentsInChildren<GamepadNavigationItem>(true))
            DestroyImmediate(item);
        if (input.TryGetComponent<Image>(out var cell))
            cell.enabled = false;
        input.characterLimit = ChatLog.MaxLength;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.text = string.Empty;
        // The arrow keys stay with the line: Up and Down pick a command's suggestion.
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        // As the native field does, a tab is not typed.
        input.onValidateInput = (text, index, added) => added == '\t' ? '\0' : added;
        input.onSubmit.AddListener(Send);
        prompt = (TMP_Text)input.placeholder;
        NativeWindow.SetText(prompt, Prompt);
        // Only the keys open the line; the pointer passes over it.
        input.textComponent.raycastTarget = false;
        prompt.raycastTarget = false;
        var rect = (RectTransform)input.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = new Vector2(0f, FieldBottom);
        rect.offsetMax = new Vector2(0f, FieldBottom + FieldHeight);
        input.gameObject.SetActive(true);
        // Waking, the field makes a caret across its text, which would take a click and open the line.
        foreach (var caret in input.GetComponentsInChildren<TMP_SelectionCaret>(true))
            caret.raycastTarget = false;
        TypedField.Guard(input);
        return input;
    }

    // How far in from the field's left and right edges its text starts and ends.
    private static (float left, float right) TextInsets(TMP_InputField input)
    {
        var corners = new Vector3[4];
        var text = input.textComponent;
        text.rectTransform.GetWorldCorners(corners);
        var rect = (RectTransform)input.transform;
        return (rect.InverseTransformPoint(corners[0]).x - rect.rect.xMin + text.margin.x,
            rect.rect.xMax - rect.InverseTransformPoint(corners[2]).x + text.margin.z);
    }

    // A new line of the chosen tab shows the chat for another ten seconds; one of the other tab waits there.
    private void Heard()
    {
        Draw();
        int said = ChatLog.Said(ChatTabs.Current);
        if (said == seen)
            return;
        seen = said;
        lastActive = Time.unscaledTime;
    }

    // The chosen tab's whole log, following its newest line while the player reads at its end; scrolled back, it stays
    // put.
    private void Draw()
    {
        bool following = scroll.verticalNormalizedPosition <= 0.01f || log.rectTransform.rect.height <= scroll.viewport.rect.height + 0.5f;
        int local = CoopSession.Current?.LocalSlot ?? 0;
        var tab = ChatTabs.Current;
        var text = new StringBuilder();
        foreach (var line in ChatLog.Lines)
        {
            if (line.Tab != tab)
                continue;
            if (text.Length > 0)
                text.Append('\n');
            Write(text, line, local);
        }
        NativeWindow.SetText(log, text.ToString());
        // A faded chat comes back at its newest line.
        if (following && panel.activeInHierarchy)
            Newest();
    }

    private void Newest()
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(log.rectTransform);
        scroll.verticalNormalizedPosition = 0f;
    }

    // A line as GYK1's chat wrote it: its time, then a player's name in bold in their colour, or a notice in italics.
    private static void Write(StringBuilder text, ChatLog.Line line, int local)
    {
        text.Append('[').Append(line.Time.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("] ");
        if (line.Tab == ChatTabs.Tab.Npcs)
        {
            // A character's line under the name the players know them by, a keeper's under their player's; its words in
            // this player's language.
            string name = line.Npc != null ? NpcNames.Shown(line.Npc) : line.Name;
            string color = line.Npc != null ? name == NpcNames.Unknown ? UnknownName : NpcName
                : PlayerColors.Valid(line.Color) ? PlayerColors.Hex(line.Color) : line.Slot == local ? OwnName : OtherName;
            text.Append("<b><color=").Append(color).Append('>').Append(NativeWindow.Literal(name)).Append("</color></b>: ")
                .Append(NativeWindow.Literal(LLBase.L(line.Text)));
            return;
        }
        if (line.Name == null)
            text.Append("<i>").Append(NativeWindow.Literal(line.Text)).Append("</i>");
        else
            text.Append("<b><color=").Append(PlayerColors.Valid(line.Color) ? PlayerColors.Hex(line.Color) : line.Slot == local ? OwnName : OtherName)
                .Append('>').Append(NativeWindow.Literal(line.Name)).Append("</color></b>: ").Append(NativeWindow.Literal(line.Text));
    }

    private void Update()
    {
        bool was = present;
        present = Present();
        if (!present)
        {
            if (typing)
                field.DeactivateInputField();
            reachable = false;
            Show(0f);
            return;
        }
        // Every player's chat shows as their game does, whenever its loading screen clears.
        if (!was)
            lastActive = Time.unscaledTime;
        if (typing)
        {
            lastActive = Time.unscaledTime;
            // Tab, which the typed line takes no part in, completes a command, as GYK1's, and otherwise turns the tabs;
            // Up and Down pick a command's suggestion.
            if (Input.GetKeyDown(KeyCode.Tab) && !suggestions.Complete())
                ChatTabs.Next();
            suggestions.Navigate();
        }
        else
            Listen();
        float idle = Time.unscaledTime - lastActive;
        Show(1f - Mathf.Clamp01((idle - Shown) / Fading));
    }

    // Enter or Y opens the line to type in while no window is open and no other text field has the keyboard.
    private void Listen()
    {
        bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        // Y as the player's keyboard types it: a keyboard with Z where Y stands on others has its Y elsewhere.
        bool y = Input.inputString.IndexOf('y') >= 0 || Input.inputString.IndexOf('Y') >= 0;
        bool was = reachable;
        reachable = LazyWindowsStackController.ActiveWindow == null && LazyInput.IsInputActive() && !Typing();
        if (reachable && !was)
            settled = Time.unscaledTime + Settling;
        // The Enter that just sent a line opens nothing.
        if (!reachable || Time.frameCount == closedFrame)
            return;
        if (y || enter && Time.unscaledTime >= settled)
            Open();
    }

    private void Open()
    {
        lastActive = Time.unscaledTime;
        Show(1f);
        field.ActivateInputField();
    }

    private void Send(string text)
    {
        if (!typing)
            return;
        closedFrame = Time.frameCount;
        field.text = string.Empty;
        // The player reads on from their own line.
        Newest();
        // A command runs, its replies for this player alone; any other line is said to everyone.
        if (ChatTabs.Current == ChatTabs.Tab.Players && !ChatCommands.TryRun(text, ChatLog.Notice))
            CoopSession.Current?.Say(text);
        lastActive = Time.unscaledTime;
    }

    // The line starts and stops taking the keyboard as the field does; the prompt shows only while it waits, and the
    // see-through black only while the player types.
    private void LateUpdate()
    {
        bool now = field.isFocused;
        if (now == typing)
            return;
        typing = now;
        background.enabled = typing;
        tabStrip.SetActive(typing);
        ShowPrompt();
        if (typing)
            return;
        // What was not sent goes, and the field no longer takes Enter from the game's own navigation.
        field.text = string.Empty;
        var events = EventSystem.current;
        if (events != null && events.currentSelectedGameObject == field.gameObject)
            events.SetSelectedGameObject(null);
    }

    private void Show(float alpha)
    {
        group.alpha = alpha;
        bool shown = alpha > 0f;
        if (panel.activeSelf == shown)
            return;
        panel.SetActive(shown);
        if (shown)
            Newest();
    }

    private static bool Present() =>
        CoopSession.Current != null && KeeperSpawn.Active && MainGame.Instance.gameState == MainGame.GameState.InGame &&
        MainGame.PlayerController != null && !LazyUI.Get<UILoadingOverlay>().IsShown;

    // A text field has the keyboard: this chat's, or another of the game's or the mod's.
    internal static bool Typing()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && selected.TryGetComponent<TMP_InputField>(out var other) && other.isFocused;
    }

    private static RectTransform Made(string name, Transform parent, int layer, params Type[] components)
    {
        var made = new GameObject(name, typeof(RectTransform)) { layer = layer };
        foreach (var component in components)
            made.AddComponent(component);
        var rect = (RectTransform)made.transform;
        rect.SetParent(parent, false);
        return rect;
    }
}
