using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// Valheim Terrain Tools opens its own brush panel at the right edge of the screen, over whatever sits there. While it is open, each of this plugin's HUD elements it overlaps is clipped away by a RectMask2D of this plugin's own (SeneaL UI fades an element through the CanvasGroup on its rectangle, so that group is left to SeneaL UI), and the chat box moves down, or left when there is no room below, until it clears the panel. The HUD editor shows everything where it is placed.
internal sealed class TerrainToolsRoom
{
    private const string PanelName = "VTT brush panel";
    private const float Gap = 24f;
    private static readonly Vector4 ClipAll = new(100000f, 100000f, 100000f, 100000f);
    private static readonly PropertyInfo SeneaLChatPanel = AccessTools.Property(TypeLookup.Find("SeneaLUI.Hud.ChatSkin"), "Panel");
    private static readonly FieldInfo SeneaLBuildView = AccessTools.Field(TypeLookup.Find("SeneaLUI.Hud.HudRoot"), "_build");
    private static readonly FieldInfo SeneaLBuildRoot = AccessTools.Field(TypeLookup.Find("SeneaLUI.Hud.BuildInfoView"), "_root");

    private readonly ConfigEntry<bool> _enabled;
    private readonly Dictionary<RectTransform, RectMask2D> _masks = [];
    private RectTransform _panel;

    private TerrainToolsRoom(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Compatibility", "Make Room For Terrain Tools Panel", true,
            "While Valheim Terrain Tools' brush panel is open, hide this mod's HUD cards it covers and move the chat box clear of it; both come back when the panel closes.");
    }

    public static TerrainToolsRoom Resolve(ConfigFile cfg) =>
        Chainloader.PluginInfos.ContainsKey(TerrainToolsCosts.TerrainToolsGuid) ? new TerrainToolsRoom(cfg) : null;

    public void Tick()
    {
        var panel = _enabled.Value && !SeneaLHud.EditorOpen ? Panel() : null;
        var area = panel != null && panel.gameObject.activeInHierarchy ? ScreenRect(panel) : (Rect?)null;
        var chatNudge = Vector3.zero;
        foreach (var (id, rect) in SeneaLHud.Registrations)
        {
            if (rect == null || SeneaLHud.ModeOf(id) != ElementMode.Hud)
            {
                continue;
            }
            if (id == "Chat")
            {
                var chat = area == null || !rect.gameObject.activeInHierarchy ? null : ChatLines();
                if (chat is { } lines && lines.Overlaps(area.Value))
                {
                    chatNudge = Clear(lines, area.Value, BuildPanel());
                }
                continue;
            }
            Hide(rect, area is { } room && rect.gameObject.activeInHierarchy && ScreenRect(rect).Overlaps(room));
        }
        ChatElement.Nudge = chatNudge;
    }

    // The chat's text area is taller than the lines it shows, so what must clear the panel is SeneaL UI's chat panel, which it fits around the shown lines (ChatSkin.Panel), or the lines themselves where SeneaL UI has none; either is measured where it would stand unmoved, taking off the displacement the box has now rather than the nudge last asked for, since subtracting a nudge not yet drawn pushes the chat further each frame.
    private static Rect? ChatLines()
    {
        var output = Chat.instance == null ? null : Chat.instance.m_output;
        if (output == null || output.text.Length == 0)
        {
            return null;
        }
        Rect lines;
        if (SeneaLChatPanel?.GetValue(null) is RectTransform panel && panel != null && panel.gameObject.activeInHierarchy)
        {
            lines = ScreenRect(panel);
        }
        else
        {
            var bounds = output.textBounds;
            var rect = output.rectTransform;
            var camera = CameraOf(rect);
            var min = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(bounds.min));
            var max = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(bounds.max));
            lines = Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
        }
        lines.position -= (Vector2)ChatElement.Displacement;
        return lines;
    }

    private static Camera CameraOf(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        var root = canvas == null ? null : canvas.rootCanvas;
        return root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
    }

    private RectTransform Panel()
    {
        if (_panel != null)
        {
            return _panel;
        }
        var build = Hud.instance == null ? null : Hud.instance.m_buildHud;
        var canvas = build == null ? null : build.GetComponentInParent<Canvas>();
        _panel = canvas == null ? null : canvas.transform.Find(PanelName) as RectTransform;
        return _panel;
    }

    private void Hide(RectTransform rect, bool hidden)
    {
        _masks.TryGetValue(rect, out var mask);
        if (mask == null)
        {
            if (!hidden)
            {
                return;
            }
            mask = rect.gameObject.AddComponent<RectMask2D>();
            mask.padding = ClipAll;
            _masks[rect] = mask;
        }
        if (mask.enabled != hidden)
        {
            mask.enabled = hidden;
        }
    }

    // Down by the overlap when the chat still fits above the screen's bottom edge, otherwise left until its right edge clears the panel, and then up over SeneaL UI's build panel when moving left put it on top of that panel.
    private static Vector3 Clear(Rect chat, Rect panel, Rect? build)
    {
        var down = chat.yMax - panel.yMin + Gap;
        if (chat.yMin - down >= 0f)
        {
            return new Vector3(0f, -down, 0f);
        }
        var left = Mathf.Min(chat.xMax - panel.xMin + Gap, chat.xMin);
        var moved = chat;
        moved.x -= left;
        var up = build is { } below && moved.Overlaps(below) ? below.yMax - moved.yMin + Gap : 0f;
        return new Vector3(-left, up, 0f);
    }

    // SeneaL UI's build panel while it shows, where the build hud is SeneaL UI's.
    private static Rect? BuildPanel()
    {
        var view = SeneaLBuildView?.GetValue(null);
        return view != null && SeneaLBuildRoot?.GetValue(view) is RectTransform root && root != null && root.gameObject.activeInHierarchy ? ScreenRect(root) : null;
    }

    private static readonly Vector3[] Corners = new Vector3[4];

    private static Rect ScreenRect(RectTransform rect)
    {
        var camera = CameraOf(rect);
        rect.GetWorldCorners(Corners);
        var min = RectTransformUtility.WorldToScreenPoint(camera, Corners[0]);
        var max = min;
        for (var i = 1; i < 4; i++)
        {
            var point = RectTransformUtility.WorldToScreenPoint(camera, Corners[i]);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }
}
