using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace GYK2.TombManyKeepers.UI;

// The game's option rows as they are drawn, moved to stand in their middle. The settings window's rows keep a wide
// column for their names, for its longer translations, so shorter names stand right of the middle. Every row's parts
// move by the same whole units so the rows, as drawn from the first letter of the longest name to the end of the last
// drawn part, stand in the middle.
internal static class DrawnRows
{
    // The arrow and slider buttons draw their faces over 14 of their 25 units; the rest of their art is clear.
    private const float ButtonFace = 14f;
    private static readonly string[] Buttons = { "ToLeft", "ToRight", "Minus", "Plus" };

    // Moves these rows, which share a middle, as a block. A slider's value is measured at the widest of the values its
    // row shows, and a typed row to its field's edges.
    internal static void Centre(IReadOnlyList<RectTransform> rows, Func<RectTransform, IEnumerable<string>> values)
    {
        float left = float.MaxValue, right = float.MinValue, middle = 0f;
        foreach (var row in rows)
        {
            var name = row.Find("LeftName")?.GetComponent<TMP_Text>();
            if (name == null)
                continue;
            middle = row.rect.center.x;
            name.ForceMeshUpdate();
            var (from, to) = Drawn(name);
            if (from <= to)
            {
                left = Mathf.Min(left, InRow(row, name.transform, from));
                right = Mathf.Max(right, InRow(row, name.transform, to));
            }
            foreach (string button in Buttons)
            {
                var face = (RectTransform)row.Find(button);
                if (face == null || !face.gameObject.activeSelf)
                    continue;
                float centre = InRow(row, face, face.rect.center.x);
                left = Mathf.Min(left, centre - ButtonFace / 2f);
                right = Mathf.Max(right, centre + ButtonFace / 2f);
            }
            var field = (RectTransform)row.Find("Field");
            if (field != null && field.gameObject.activeSelf)
            {
                left = Mathf.Min(left, InRow(row, field, field.rect.xMin));
                right = Mathf.Max(right, InRow(row, field, field.rect.xMax));
            }
            var value = row.Find("Value")?.GetComponent<TMP_Text>();
            if (value != null && value.gameObject.activeSelf)
                right = Mathf.Max(right, InRow(row, value.transform, WidestEnd(value, values(row))));
        }
        if (left > right)
            return;
        var shift = new Vector2(Mathf.Round(middle - (left + right) / 2f), 0f);
        if (shift.x == 0f)
            return;
        foreach (var row in rows)
            foreach (RectTransform part in row)
                part.anchoredPosition += shift;
    }

    // Where the widest of these values ends as drawn. The value is left-aligned, so each is laid out once in its place
    // and the shown value put back before anything is drawn.
    private static float WidestEnd(TMP_Text value, IEnumerable<string> values)
    {
        string shown = value.text;
        float end = float.MinValue;
        foreach (string text in values.DefaultIfEmpty(shown))
        {
            value.text = text;
            value.ForceMeshUpdate();
            end = Mathf.Max(end, Drawn(value).right);
        }
        value.text = shown;
        value.ForceMeshUpdate();
        return end;
    }

    // Where a text's letters are drawn, across in its own units: from the first visible letter's own left edge to the
    // last one's right edge. TMP's bounds end where the next letter would start, a unit of spacing further.
    internal static (float left, float right) Drawn(TMP_Text text)
    {
        float left = float.MaxValue, right = float.MinValue;
        var info = text.textInfo;
        for (int i = 0; i < info.characterCount; i++)
        {
            var character = info.characterInfo[i];
            if (!character.isVisible || character.textElement?.glyph == null)
                continue;
            var metrics = character.textElement.glyph.metrics;
            float start = character.origin + metrics.horizontalBearingX * character.scale;
            left = Mathf.Min(left, start);
            right = Mathf.Max(right, start + metrics.width * character.scale);
        }
        return (left, right);
    }

    // A point on a part, across in its row's units.
    private static float InRow(RectTransform row, Transform part, float x) => row.InverseTransformPoint(part.TransformPoint(new Vector3(x, 0f))).x;
}
