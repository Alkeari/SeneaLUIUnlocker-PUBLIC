using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// SeneaL UI's own fonts, colors and drawn panels, read by name so this plugin's elements look like SeneaL UI's; a SeneaL UI build without one of them gets the fallback instead.
internal static class SeneaLTheme
{
    private static readonly Type Fonts = TypeLookup.Find("SeneaLUI.Theme.Fonts");
    private static readonly Type Palette = TypeLookup.Find("SeneaLUI.Core.Palette");
    private static readonly Type Styles = TypeLookup.Find("SeneaLUI.Theme.Styles");
    private static readonly Type Skin = TypeLookup.Find("SeneaLUI.Theme.Skin");
    private static readonly MethodInfo Sliced = Skin == null ? null : AccessTools.Method(Skin, nameof(SeneaLUI.Theme.Skin.Sliced));
    private static readonly MethodInfo CircleSprite = Skin == null ? null : AccessTools.Method(Skin, nameof(SeneaLUI.Theme.Skin.Circle));
    private static readonly Type Ui = TypeLookup.Find("SeneaLUI.Components.Ui");
    private static readonly MethodInfo UiPanel = Ui == null ? null : AccessTools.Method(Ui, nameof(SeneaLUI.Components.Ui.Panel));

    public static TMP_FontAsset Font(string name) =>
        Fonts == null ? null : AccessTools.Field(Fonts, name)?.GetValue(null) as TMP_FontAsset;

    public static Color Color(string name, Color fallback) =>
        Palette != null && AccessTools.Field(Palette, name)?.GetValue(null) is Color color ? color : fallback;

    // A nine-sliced panel in one of SeneaL UI's box styles (Card, Pill, Track, Glass and so on), with the padding SeneaL UI lays it out with.
    public static (Sprite Sprite, int Pad) Panel(string style, int height)
    {
        var box = Styles == null ? null : AccessTools.Field(Styles, style)?.GetValue(null);
        if (box == null || Sliced == null)
        {
            return (null, 0);
        }
        try
        {
            var sprite = Sliced.Invoke(null, [height, box, -1]) as Sprite;
            var pad = AccessTools.Property(box.GetType(), nameof(SeneaLUI.Theme.BoxStyle.Pad))?.GetValue(box, null) is int p ? p : 0;
            return (sprite, pad);
        }
        catch (Exception)
        {
            return (null, 0);
        }
    }

    // A background behind everything in host, drawn as SeneaL UI draws its own panels: the sliced image reaches past the host by the style's shadow margin, so the box itself lines up with the host's edges, and it joins SeneaL UI's panel transparency and per-element background, color and transparency settings. Without SeneaL UI's drawing, a plain dark box.
    public static Image Back(RectTransform host, string style, int height)
    {
        var box = Styles == null ? null : AccessTools.Field(Styles, style)?.GetValue(null);
        var (sprite, pad) = Panel(style, height);
        if (sprite != null && UiPanel != null)
        {
            try
            {
                var image = UiPanel.Invoke(null, [host, sprite, box, false]) as Image;
                if (image != null)
                {
                    image.raycastTarget = false;
                    image.transform.SetAsFirstSibling();
                    return image;
                }
            }
            catch (Exception)
            {
            }
        }
        var node = new GameObject("bg", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)node.transform;
        rect.SetParent(host, false);
        rect.SetAsFirstSibling();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(-pad, -pad);
        rect.offsetMax = new Vector2(pad, pad);
        var fallback = node.GetComponent<Image>();
        fallback.sprite = sprite;
        fallback.type = sprite == null ? Image.Type.Simple : Image.Type.Sliced;
        fallback.color = sprite == null ? new UnityEngine.Color(0.055f, 0.043f, 0.035f, 0.78f) : UnityEngine.Color.white;
        fallback.raycastTarget = false;
        return fallback;
    }

    public static Sprite Circle(string style, int diameter)
    {
        var box = Styles == null ? null : AccessTools.Field(Styles, style)?.GetValue(null);
        if (box == null || CircleSprite == null)
        {
            return null;
        }
        try
        {
            return CircleSprite.Invoke(null, [diameter, box, -1]) as Sprite;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
