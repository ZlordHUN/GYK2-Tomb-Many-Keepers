using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace GYK2.TombManyKeepers.UI.MainMenu;

// The mod's title under the game's own in the main menu, as GYK1's mod showed its own: the game's logo, then
// "Tomb Many Keepers", then the buttons. The title arrives with the buttons in the menu's intro. Where the screen
// is too short for all three at their own size, the logo and the title give way before the buttons do, down to half
// their size; the menu's fitting scales the rest.
internal static class ModTitle
{
    // The title's width beside the logo's 274 units; how far it reaches up into the logo's frame, whose drawn part
    // ends about 23 units over its bottom above the title's skull arch, leaving them 5 apart; the room under it, how
    // far the buttons' dark panel reaches below them, and how far the pictures may shrink.
    private const float Width = 232f, Above = -25f, Below = 4f, PanelBelow = 4f, Smallest = 0.5f;
    private static Texture2D texture;
    private static Sprite sprite;

    internal static void Add(UIMainMenuWindow menu)
    {
        var buttons = (RectTransform)menu.transform.Find("Bg/Vertical Group");
        if (buttons == null || buttons.Find("Mod Title") != null)
            return;
        var title = new GameObject("Mod Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement)).GetComponent<Image>();
        title.transform.SetParent(buttons, false);
        title.GetComponent<LayoutElement>().ignoreLayout = true;
        title.sprite = Sprite();
        title.preserveAspect = true;
        title.raycastTarget = false;
        var rect = title.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.sizeDelta = new Vector2(Width, Width * sprite.rect.height / sprite.rect.width);
        // Its resting place, which the menu's intro slides it up to with the buttons.
        rect.anchoredPosition = new Vector2(0f, Below);
        var logo = buttons.Find("LogoContainer");
        if (logo != null)
            title.transform.SetSiblingIndex(logo.GetSiblingIndex() + 1);
    }

    // Stands the logo above the title above the buttons, the pictures at the largest size the height given leaves
    // them beside the buttons, in the buttons' own units.
    internal static void Arrange(RectTransform buttons, float height)
    {
        var title = (RectTransform)buttons.Find("Mod Title");
        var logo = (RectTransform)buttons.Find("LogoContainer");
        if (title == null || logo == null)
            return;
        float pictures = logo.rect.height + Above + title.rect.height;
        float room = height - buttons.rect.height - PanelBelow - Below;
        float scale = Mathf.Clamp(room / pictures, Smallest, 1f);
        title.localScale = logo.localScale = new Vector3(scale, scale, 1f);
        title.anchoredPosition = new Vector2(0f, Below);
        logo.anchoredPosition = new Vector2(logo.anchoredPosition.x,
            Below + scale * (title.rect.height + Above) + scale * logo.rect.height * logo.pivot.y);
    }

    private static Sprite Sprite()
    {
        if (sprite != null)
            return sprite;
        // Drawn much smaller than it is, so smoothly and from its smaller copies.
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
        {
            name = "Tomb Many Keepers Title",
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        using (var stream = typeof(ModTitle).Assembly.GetManifestResourceStream("GYK2.TombManyKeepers.ModTitle.png"))
        using (var bytes = new MemoryStream())
        {
            stream.CopyTo(bytes);
            ImageConversion.LoadImage(texture, bytes.ToArray(), true);
        }
        sprite = UnityEngine.Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect);
        return sprite;
    }
}
