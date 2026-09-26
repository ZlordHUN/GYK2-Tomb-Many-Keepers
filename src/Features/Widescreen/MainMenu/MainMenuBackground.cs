using System.IO;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.Widescreen.MainMenu;

internal sealed class MainMenuBackground : MonoBehaviour
{
    internal const float Height = 10.8f;
    internal const float Aspect = 1916f / 821f;
    internal const float PixelsPerUnit = 821f / Height;

    private Texture2D texture;
    private Sprite sprite;
    private GameObject artwork;
    private MainMenuEffects effects;
    private MainMenuArtworkLayers layers;

    internal void Initialize()
    {
        effects = new MainMenuEffects(transform);
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = "Tomb Many Keepers Wide Background",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        using (var stream = typeof(MainMenuBackground).Assembly.GetManifestResourceStream("GYK2.TombManyKeepers.WideBackground.png"))
        using (var bytes = new MemoryStream())
        {
            stream.CopyTo(bytes);
            ImageConversion.LoadImage(texture, bytes.ToArray());
        }

        artwork = new GameObject("Wide Menu Background");
        artwork.layer = gameObject.layer;
        artwork.transform.SetParent(transform, false);
        var renderer = artwork.AddComponent<SpriteRenderer>();
        sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
            Vector2.one * 0.5f, PixelsPerUnit, 0, SpriteMeshType.FullRect);
        renderer.sprite = sprite;
        // Use the built-in sprite material; the game's unused titlescreen-1x object was removed.
        layers = new MainMenuArtworkLayers(transform, artwork.transform, texture);
        texture.Apply(false, true);
    }

    internal void SetWide(bool wide)
    {
        artwork.SetActive(wide);
        effects.SetWide(wide);
    }

    private void OnDestroy()
    {
        layers?.Dispose();
        if (sprite != null)
            Destroy(sprite);
        if (texture != null)
            Destroy(texture);
    }
}
