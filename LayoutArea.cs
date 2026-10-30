using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI keeps each placed element's position as a share of the screen's width and height (its Layout X and Y settings), while sizes follow the screen's height, so on a screen wider or taller than 16:9 a layout spreads apart. With Keep Layout In 16:9 Area on, every placed position is kept here as a share of a centered 16:9 area, and SeneaL UI's settings get the matching share of the actual screen, so the layout looks the same on every screen shape.
// A position the HUD editor, a preset or anything else writes is read back into the 16:9 area as the screen it was set on shows it; the built-in preset's positions are 16:9 shares already and are taken as they are.
internal static class LayoutArea
{
    private const float Wide = 16f / 9f;
    private const float Close = 0.0005f;

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<string> _store;
    private static ConfigEntry<string> _shape;
    private static Dictionary<string, Vector2> _refs;
    private static float _next;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("HUD Editor", "Keep Layout In 16:9 Area", true,
            "On a screen wider than 16:9 (such as 3440 x 1440 or 5120 x 1440) or taller (such as 1920 x 1200), every element placed in SeneaL UI's HUD editor keeps its place within a centered 16:9 area, so the layout looks as it does on a 16:9 screen; off, positions spread over the whole screen.");
        var hidden = new ConfigurationManagerAttributes { Browsable = false };
        _store = cfg.Bind("HUD Editor", "Layout Area Positions", "", new ConfigDescription("Each placed element's position within the 16:9 area, kept by the mod.", null, hidden));
        _shape = cfg.Bind("HUD Editor", "Layout Area Shape", "", new ConfigDescription("The screen shape SeneaL UI's positions were last written for, kept by the mod.", null, hidden));
    }

    public static void Tick()
    {
        if (_enabled == null || Time.realtimeSinceStartup < _next)
        {
            return;
        }
        _next = Time.realtimeSinceStartup + 0.5f;
        Sync(false);
    }

    // The positions SeneaL UI's settings hold now are 16:9 shares (a preset written for 16:9): kept as they are and drawn for this screen.
    public static void Adopt() => Sync(true);

    private static Vector2 Shape()
    {
        if (!_enabled.Value || Screen.height <= 0)
        {
            return Vector2.one;
        }
        var aspect = (float)Screen.width / Screen.height;
        return new Vector2(aspect > Wide ? Wide / aspect : 1f, aspect < Wide ? aspect / Wide : 1f);
    }

    private static float Map(float share, float k) => 0.5f + (share - 0.5f) * k;

    private static float Unmap(float share, float k) => 0.5f + (share - 0.5f) / k;

    private static void Sync(bool adopt)
    {
        var cfg = SeneaLConfig.File;
        if (cfg == null)
        {
            return;
        }
        _refs ??= Load(_store.Value);
        var now = Shape();
        // With no record of the shape they were written for, SeneaL UI's positions are taken as made for this screen, so turning this on moves nothing until the screen changes or the built-in preset is applied.
        var before = _shape.Value.Length == 0 ? now : ParseShape(_shape.Value);
        var writes = new List<(ConfigEntryBase Entry, string Value)>();
        var changed = false;
        foreach (var (key, x, y) in Positions(cfg))
        {
            var at = new Vector2((float)x.BoxedValue, (float)y.BoxedValue);
            if (at.x < 0f || at.y < 0f)
            {
                changed |= _refs.Remove(key);
                continue;
            }
            Vector2 share;
            if (adopt)
            {
                share = at;
            }
            else if (!_refs.TryGetValue(key, out share) || Mathf.Abs(Map(share.x, before.x) - at.x) > Close || Mathf.Abs(Map(share.y, before.y) - at.y) > Close)
            {
                share = new Vector2(Unmap(at.x, before.x), Unmap(at.y, before.y));
            }
            if (!_refs.TryGetValue(key, out var kept) || kept != share)
            {
                _refs[key] = share;
                changed = true;
            }
            var wanted = new Vector2(Map(share.x, now.x), Map(share.y, now.y));
            if (Mathf.Abs(wanted.x - at.x) > Close)
            {
                writes.Add((x, wanted.x.ToString(CultureInfo.InvariantCulture)));
            }
            if (Mathf.Abs(wanted.y - at.y) > Close)
            {
                writes.Add((y, wanted.y.ToString(CultureInfo.InvariantCulture)));
            }
        }
        if (changed)
        {
            _store.Value = string.Join(";", _refs.Select(pair => $"{pair.Key}={pair.Value.x.ToString(CultureInfo.InvariantCulture)},{pair.Value.y.ToString(CultureInfo.InvariantCulture)}"));
        }
        var shape = $"{now.x.ToString(CultureInfo.InvariantCulture)},{now.y.ToString(CultureInfo.InvariantCulture)}";
        if (_shape.Value != shape)
        {
            _shape.Value = shape;
        }
        if (writes.Count > 0)
        {
            SeneaLConfig.Write(cfg, writes);
        }
    }

    // Every element position in SeneaL UI's Layout section: a float setting <Name>X beside <Name>Y, leaving out the offsets of attached elements (<Name>AttachX).
    private static IEnumerable<(string Key, ConfigEntryBase X, ConfigEntryBase Y)> Positions(ConfigFile cfg)
    {
        foreach (var entry in SeneaLConfig.Entries(cfg))
        {
            var key = entry.Definition.Key;
            if (entry.Definition.Section != "Layout" || entry.SettingType != typeof(float) || key.Length < 2 || key[key.Length - 1] != 'X' || key.EndsWith("AttachX"))
            {
                continue;
            }
            var name = key.Substring(0, key.Length - 1);
            var y = SeneaLConfig.Entry(cfg, "Layout", name + "Y");
            if (y != null && y.SettingType == typeof(float))
            {
                yield return (name, entry, y);
            }
        }
    }

    private static Dictionary<string, Vector2> Load(string saved)
    {
        var refs = new Dictionary<string, Vector2>();
        foreach (var part in saved.Split(';'))
        {
            var pair = part.Split('=');
            var xy = pair.Length == 2 ? pair[1].Split(',') : [];
            if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                refs[pair[0]] = new Vector2(x, y);
            }
        }
        return refs;
    }

    private static Vector2 ParseShape(string saved)
    {
        var xy = saved.Split(',');
        return xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x > 0f && y > 0f
            ? new Vector2(x, y)
            : Vector2.one;
    }
}
