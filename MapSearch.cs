using SeneaLUI.Components;
using SeneaLUI.Map;
using SeneaLUI.Patches;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal interface IMapSearch
{
    void Tick();
}

// A search box on SeneaL UI's world map, at its lower left: pins whose name does not hold the text fade almost out, so the ones that do stand out. While it has focus, SeneaL UI's map keys stay quiet (MapView.Typing).
internal sealed class MapSearch : IMapSearch
{
    private const float Width = 260f;

    private MapView _view;
    private SearchBox _box;
    private string _term = "";

    // What the search box holds, lowercased, for the bulk recolor and remove of MapPinExtras.
    public static string Term { get; private set; } = "";
    private bool _filtered;
    private bool _typing;

    private readonly BepInEx.Configuration.ConfigEntry<float> _faded;

    private MapSearch(BepInEx.Configuration.ConfigFile cfg)
    {
        _faded = cfg.Bind("Map", "Search Faded Pin Opacity", 0.12f, new BepInEx.Configuration.ConfigDescription(
            "How visible the pins that do not match the map search stay, from 0 (hidden) to 1 (unchanged).", new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f)));
        Canvas.willRenderCanvases += Fade;
    }

    public static IMapSearch Create(BepInEx.Configuration.ConfigFile cfg) => new MapSearch(cfg);

    public void Tick()
    {
        var view = MapPatches.View;
        if (view == null || view._overlay == null)
        {
            return;
        }
        if (view != _view || _box == null || _box.Root == null)
        {
            _view = view;
            _box = new SearchBox(view._overlay, "unlocker.mapsearch", Width);
            _box.SetPlaceholder("Search pins");
            // Above SeneaL UI's key hint row, which sits in the bottom left corner 16 units in, 40 high.
            Ui.Place(_box.Root, 0f, 0f, 16f, 64f, Width, 32f);
            _box.Changed += text => Term = _term = (text ?? "").Trim().ToLowerInvariant();
        }
        var allowed = ServerRules.Allows("MapSearch");
        if (_box.Root.gameObject.activeSelf != allowed)
        {
            _box.Root.gameObject.SetActive(allowed);
            if (!allowed)
            {
                _box.Clear();
                Term = _term = "";
            }
        }
        if (!allowed)
        {
            return;
        }
        _box.Tick(dismiss: true);
        if (_box.Focused != _typing)
        {
            _typing = _box.Focused;
            MapView.Typing = _typing;
        }
    }

    // After SeneaL UI has drawn its pins for the frame; its own alpha for each pin is put back once the search is cleared.
    private void Fade()
    {
        if (_view == null || Minimap.instance == null)
        {
            return;
        }
        if (_term.Length == 0)
        {
            if (_filtered)
            {
                foreach (var pair in MapPins.Uis)
                {
                    if (pair.Value.Group != null)
                    {
                        pair.Value.Group.alpha = pair.Value.Alpha;
                    }
                }
                _filtered = false;
            }
            return;
        }
        _filtered = true;
        foreach (var pair in MapPins.Uis)
        {
            var ui = pair.Value;
            if (ui.Group == null)
            {
                continue;
            }
            var match = (pair.Key.m_name ?? "").ToLowerInvariant().Contains(_term);
            ui.Group.alpha = match ? ui.Alpha : _faded.Value;
        }
    }
}
