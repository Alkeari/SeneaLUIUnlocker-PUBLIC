using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// Without SeneaL UI's HUD editor (SeneaL UI 1.1.x) nothing places this plugin's elements, and the ones that stand at the right edge would cover each other wherever their contents grow. They stack under the minimap, time and wind card and task list those builds draw in the top right corner, in a fixed order, each under the one above that is showing.
internal static class FallbackColumn
{
    private static readonly string[] Order = ["Forecast", "BaseStatus", "ShelterComfort"];
    private static readonly Vector3[] Corners = new Vector3[4];
    private static System.Reflection.FieldInfo _minimapElement;
    private static bool _minimapLooked;

    public static float Margin => Screen.height * 0.025f;

    public static bool Holds(string id) => System.Array.IndexOf(Order, id) >= 0;

    // Places the stacked cards and returns the height on screen where whatever goes under them starts.
    public static float Place()
    {
        var top = MinimapColumn() is { } column ? column.yMin - Margin * 0.5f : Screen.height - Margin;
        if (!SeneaLHud.Fallback)
        {
            return top;
        }
        foreach (var id in Order)
        {
            var rect = SeneaLHud.RectOf(id);
            if (rect == null || !rect.gameObject.activeInHierarchy)
            {
                continue;
            }
            rect.GetWorldCorners(Corners);
            rect.position += new Vector3(Screen.width - Margin - Corners[2].x, top - Corners[2].y, 0f);
            rect.GetWorldCorners(Corners);
            top = Corners[0].y - Margin * 0.5f;
        }
        return top;
    }

    private static Rect? MinimapColumn()
    {
        if (!_minimapLooked)
        {
            _minimapLooked = true;
            _minimapElement = TypeLookup.Find("SeneaLUI.Hud.HudRoot") is { } hud ? HarmonyLib.AccessTools.Field(hud, "_elMinimap") : null;
        }
        var element = _minimapElement?.GetValue(null);
        var layer = element == null ? null : HarmonyLib.AccessTools.Field(element.GetType(), "Layer")?.GetValue(element) as RectTransform;
        if (layer == null || !layer.gameObject.activeInHierarchy)
        {
            return null;
        }
        Rect? column = null;
        foreach (var graphic in layer.GetComponentsInChildren<Graphic>())
        {
            if (!graphic.enabled || graphic.color.a <= 0f)
            {
                continue;
            }
            graphic.rectTransform.GetWorldCorners(Corners);
            var rect = Rect.MinMaxRect(Corners[0].x, Corners[0].y, Corners[2].x, Corners[2].y);
            if (rect.width <= 0f || rect.width > Screen.width * 0.5f || rect.height > Screen.height * 0.5f)
            {
                continue;
            }
            column = column is { } so ? Rect.MinMaxRect(Mathf.Min(so.xMin, rect.xMin), Mathf.Min(so.yMin, rect.yMin), Mathf.Max(so.xMax, rect.xMax), Mathf.Max(so.yMax, rect.yMax)) : rect;
        }
        return column;
    }
}
