using System.Collections.Generic;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.Widescreen.MainMenu;

internal sealed class MainMenuEffects
{
    private readonly (SpriteRenderer Renderer, bool Hidden)[] scenery;
    private readonly (Transform Transform, Vector3 Position, Vector3 Scale,
        Vector3 WidePosition, Vector3 WideScale)[] groups;
    private bool wide;

    internal MainMenuEffects(Transform root)
    {
        var hidden = new List<(SpriteRenderer, bool)>();
        foreach (var renderer in root.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (renderer.name == "titlescreen-z_10_02-bg_1-sky_night-stars" ||
                renderer.GetComponentInParent<Animator>() != null)
                continue;
            hidden.Add((renderer, renderer.forceRenderingOff));
        }
        scenery = hidden.ToArray();

        // Register each region to the supplied artwork; its landmarks were repainted.
        groups = new[]
        {
            Register(root, "BG_1 Z_10", 1.36f, 512f, -4f),
            Register(root, "BG_2 Z_20", 1.4f, 488f, 9f),
            Register(root, "City Z_30", 1.38f, 503f, 4f),
            Register(root, "Tower Z_40", 1.4f, 503.4f, 2.2f),
            Register(root, "Graveyard Z_50", 1.4f, 498f, -6f),
            Register(root, "Mine Z_60", 1.4f, 513f, -8f),
            Register(root, "Crossrod Z_70", 1.4f, 493f, -2f),
            Register(root, "Forest Z_80", 1.4f, 510f, 0f),
            Register(root, "Vilage Z_90", 1.4f, 492f, 6f)
        };
    }

    internal void SetWide(bool value)
    {
        if (wide == value)
            return;
        wide = value;
        foreach (var state in scenery)
            state.Renderer.forceRenderingOff = value || state.Hidden;
        foreach (var state in groups)
        {
            state.Transform.localPosition = value ? state.WidePosition : state.Position;
            state.Transform.localScale = value ? state.WideScale : state.Scale;
        }
    }

    private static (Transform, Vector3, Vector3, Vector3, Vector3) Register(
        Transform root, string name, float horizontalScale, float offsetX, float offsetY)
    {
        var transform = root.Find(name);
        var scale = new Vector3(horizontalScale * 50f / MainMenuBackground.PixelsPerUnit,
            1.4f * 50f / MainMenuBackground.PixelsPerUnit, 1f);
        var offset = new Vector3((480f * horizontalScale + offsetX - 958f) / MainMenuBackground.PixelsPerUnit,
            (410.5f - (270f * 1.4f + offsetY)) / MainMenuBackground.PixelsPerUnit, 0f);
        return (transform, transform.localPosition, transform.localScale,
            Vector3.Scale(transform.localPosition, scale) + offset,
            Vector3.Scale(transform.localScale, scale));
    }
}
