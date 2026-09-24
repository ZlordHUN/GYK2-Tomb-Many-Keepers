using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GYK2.TombManyKeepers.Features.Widescreen.MainMenu;

internal sealed class MainMenuArtworkLayers : IDisposable
{
    private readonly List<Object> resources = new();

    internal MainMenuArtworkLayers(Transform nativeRoot, Transform parent, Texture2D artwork)
    {
        var native = nativeRoot.GetComponentsInChildren<SpriteRenderer>(true);
        var pixels = artwork.GetPixels32();
        foreach (var region in MainMenuArtworkRegions.All)
        {
            var source = Array.Find(native, renderer => renderer.sharedMaterial.name == region.Material).sharedMaterial;
            AddRegion(region, source, parent, artwork, pixels);
        }
    }

    private void AddRegion(MainMenuArtworkRegion region, Material source, Transform parent, Texture2D artwork, Color32[] pixels)
    {
        var left = artwork.width;
        var top = artwork.height;
        var right = 0;
        var bottom = 0;
        foreach (var polygon in region.Include)
            for (var i = 0; i < polygon.Length; i += 2)
            {
                left = Math.Min(left, polygon[i]);
                right = Math.Max(right, polygon[i]);
                top = Math.Min(top, polygon[i + 1]);
                bottom = Math.Max(bottom, polygon[i + 1]);
            }
        var width = right - left;
        var height = bottom - top;
        var mask = new bool[width * height];
        Rasterize(region.Include, true);
        Rasterize(region.Exclude, false);
        var colors = new Color32[mask.Length];
        var vertices = new List<Vector2>();
        var triangles = new List<ushort>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width;)
            {
                if (!mask[y * width + x])
                {
                    x++;
                    continue;
                }
                var start = x;
                while (x < width && mask[y * width + x])
                {
                    colors[(height - y - 1) * width + x] = pixels[(artwork.height - top - y - 1) * artwork.width + left + x];
                    x++;
                }
                // Fixed geometry clips displaced alpha at shorelines and solid objects.
                var index = (ushort)vertices.Count;
                vertices.Add(new Vector2(start, height - y - 1));
                vertices.Add(new Vector2(x, height - y - 1));
                vertices.Add(new Vector2(start, height - y));
                vertices.Add(new Vector2(x, height - y));
                // Native title-screen shaders cull backfaces; face the menu camera.
                triangles.Add(index);
                triangles.Add((ushort)(index + 2));
                triangles.Add((ushort)(index + 1));
                triangles.Add((ushort)(index + 1));
                triangles.Add((ushort)(index + 2));
                triangles.Add((ushort)(index + 3));
            }
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = region.Name + " Cutout",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixels32(colors);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), Vector2.one * 0.5f,
            MainMenuBackground.PixelsPerUnit, 0, SpriteMeshType.FullRect);
        sprite.OverrideGeometry(vertices.ToArray(), triangles.ToArray());
        var material = new Material(source) { name = region.Name + " Animation" };
        if (region.Material == "titlescreen-vegitation_1")
        {
            material.SetFloat("_f1", 1f);
            material.SetFloat("_f2", 1f);
            material.SetVector("_texcoord_ST", new Vector4(width / (width + 1f), height / (height + 1f),
                -1f / (width * (width + 1f)), -1f / (height * (height + 1f))));
        }
        else
        {
            material.SetFloat("_DistortionOffsetX", 0f);
            if (region.Name == "Waterfalls")
            {
                material.SetFloat("_DistortionX", 0.2f);
                material.SetFloat("_DistortionY", 1.2f);
                material.SetFloat("_DistortionScale_XFactor", 0.2f);
                material.SetFloat("_DistortionScale_YFactor", 0.2f);
                material.SetFloat("_DistorionSpeedY", 8f);
            }
        }
        resources.Add(texture);
        resources.Add(sprite);
        resources.Add(material);
        var layer = new GameObject(region.Name) { layer = parent.gameObject.layer };
        layer.transform.SetParent(parent, false);
        layer.transform.localPosition = new Vector3(left + width * 0.5f - artwork.width * 0.5f,
            artwork.height * 0.5f - top - height * 0.5f, 0f) / MainMenuBackground.PixelsPerUnit;
        var renderer = layer.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sharedMaterial = material;
        renderer.sortingOrder = 1;

        void Rasterize(int[][] polygons, bool value)
        {
            var intersections = new List<float>();
            foreach (var polygon in polygons)
                for (var y = 0; y < height; y++)
                {
                    intersections.Clear();
                    var row = top + y + 0.5f;
                    for (int i = 0, previous = polygon.Length - 2; i < polygon.Length; previous = i, i += 2)
                        if ((polygon[i + 1] > row) != (polygon[previous + 1] > row))
                            intersections.Add(polygon[i] + (row - polygon[i + 1]) *
                                (polygon[previous] - polygon[i]) / (polygon[previous + 1] - polygon[i + 1]));
                    intersections.Sort();
                    for (var i = 0; i < intersections.Count; i += 2)
                        for (var x = Math.Max(0, Mathf.CeilToInt(intersections[i] - left - 0.5f));
                             x < Math.Min(width, Mathf.CeilToInt(intersections[i + 1] - left - 0.5f)); x++)
                            mask[y * width + x] = value;
                }
        }
    }

    public void Dispose()
    {
        foreach (var resource in resources)
            Object.Destroy(resource);
    }
}
