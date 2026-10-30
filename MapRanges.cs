using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using SeneaLUI.Patches;
using SeneaLUI.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

internal interface IMapRanges
{
    void Tick();
}

// Rings on SeneaL UI's world map around the player for the ranges SeneaL UI works within at the moment: ore detection, SeneaL Radar, quick stack and building from chests. Each reads SeneaL UI's own value for the current server, so it shows what SeneaL UI actually uses, raised past its maximums where this plugin does that.
internal sealed class MapRanges : IMapRanges
{
    private const string Section = "Map";

    private sealed class Ring
    {
        public ConfigEntry<bool> Shown;
        public System.Func<float> Radius;
        public Color Color;
        public RingGraphic Graphic;
    }

    private readonly List<Ring> _rings = [];
    private RectTransform _map;
    private readonly ConfigEntry<float> _thickness;

    private MapRanges(ConfigFile cfg)
    {
        // Read through reflection so BeyondMaximums' raised values are what comes back, never an inlined copy of SeneaL UI's getter.
        var policy = typeof(SeneaLUI.Net.Policy);
        var ore = AccessTools.Property(policy, nameof(SeneaLUI.Net.Policy.OreRadius));
        var stack = AccessTools.Property(policy, nameof(SeneaLUI.Net.Policy.StackRange));
        var build = AccessTools.Property(policy, nameof(SeneaLUI.Net.Policy.BuildRange));
        var radar = Quiet.Property(Quiet.Type("SeneaLUI.Radar.Blips"), "Range");
        // SeneaL UI 1.1.7 has no ore detection, so its ring is offered only where the range exists.
        if (ore != null)
        {
            Add(cfg, "Ore Radius On Map", "Draw a ring on the world map around you showing how close SeneaL UI must be to an ore deposit to find it.",
                () => (float)ore.GetValue(null), Palette.Gold);
        }
        if (radar != null)
        {
            Add(cfg, "Radar Range On Map", "Draw a ring on the world map around you showing how far SeneaL Radar sees.",
                () => (float)radar.GetValue(null), Palette.Teal);
        }
        Add(cfg, "Quick Stack Range On Map", "Draw a ring on the world map around you showing how far quick stack, restock and consolidate reach for chests.",
            () => (float)stack.GetValue(null), Palette.Brass);
        Add(cfg, "Build Chest Range On Map", "Draw a ring on the world map around you showing how far building takes materials from chests.",
            () => (float)build.GetValue(null), Palette.Ash);
        _thickness = cfg.Bind(Section, "Range Ring Thickness", 2f, new ConfigDescription(
            "How thick the range rings on the world map are, in pixels.", new AcceptableValueRange<float>(1f, 8f)));
        Canvas.willRenderCanvases += Place;
    }

    public static IMapRanges Create(ConfigFile cfg) => new MapRanges(cfg);

    private void Add(ConfigFile cfg, string key, string description, System.Func<float> radius, Color color)
    {
        _rings.Add(new Ring { Shown = cfg.Bind(Section, key, true, description), Radius = radius, Color = color });
    }

    public void Tick()
    {
        var view = MapPatches.View;
        var map = view == null ? null : view.MapRect;
        if (map == null || map == _map)
        {
            return;
        }
        _map = map;
        foreach (var ring in _rings)
        {
            var go = new GameObject("SeneaLUIUnlocker_Range", typeof(RectTransform));
            go.transform.SetParent(map, false);
            ring.Graphic = go.AddComponent<RingGraphic>();
            ring.Graphic.color = ring.Color;
            ring.Graphic.raycastTarget = false;
            var rt = ring.Graphic.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        }
    }

    // After SeneaL UI has moved and zoomed the map for the frame.
    private void Place()
    {
        var view = MapPatches.View;
        var player = Player.m_localPlayer;
        var open = view != null && _map != null && player != null && Minimap.IsOpen();
        foreach (var ring in _rings)
        {
            if (ring.Graphic == null)
            {
                continue;
            }
            float radius = 0f;
            if (open && ring.Shown.Value && ServerRules.Allows("RangeRings"))
            {
                try
                {
                    radius = ring.Radius();
                }
                catch (System.Exception)
                {
                    radius = 0f;
                }
            }
            var shown = radius > 0f;
            if (ring.Graphic.gameObject.activeSelf != shown)
            {
                ring.Graphic.gameObject.SetActive(shown);
            }
            if (!shown)
            {
                continue;
            }
            var center = player.transform.position;
            var local = view.WorldToLocal(center);
            var edge = view.WorldToLocal(center + new Vector3(radius, 0f, 0f));
            var size = Mathf.Abs(edge.x - local.x) * 2f;
            var rt = ring.Graphic.rectTransform;
            rt.localPosition = new Vector3(local.x, local.y, 0f);
            rt.sizeDelta = new Vector2(size, size);
            if (ring.Graphic.Thickness != _thickness.Value)
            {
                ring.Graphic.Thickness = _thickness.Value;
                ring.Graphic.SetVerticesDirty();
            }
        }
    }

    // A circle outline the size of its rect.
    internal sealed class RingGraphic : MaskableGraphic
    {
        private const int Segments = 96;
        public float Thickness = 2f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var outer = rectTransform.rect.width * 0.5f;
            if (outer < 2f)
            {
                return;
            }
            var inner = outer - Thickness;
            for (var i = 0; i <= Segments; i++)
            {
                var angle = i * Mathf.PI * 2f / Segments;
                var dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                vh.AddVert(dir * outer, color, Vector2.zero);
                vh.AddVert(dir * inner, color, Vector2.zero);
                if (i > 0)
                {
                    var at = i * 2;
                    vh.AddTriangle(at - 2, at - 1, at);
                    vh.AddTriangle(at - 1, at + 1, at);
                }
            }
        }
    }
}
