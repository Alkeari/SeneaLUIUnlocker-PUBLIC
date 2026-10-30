using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using SeneaLUI.Theme;
using SeneaLUI.Components;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// The SeneaL UI Unlocker interface preset: a layout made for SeneaL UI with this plugin's elements placed, shipped inside the plugin (Presets\SeneaL UI Unlocker.slui). It is listed among the built-in presets of SeneaL UI's Interfaces menu, applied the way SeneaL UI applies its own (every layout setting back to its default, then the preset's), and once, on the first world after installing, it becomes the layout of a player whose layout is still SeneaL UI's default or one of SeneaL UI's built-in presets; a layout the player has changed or saved is never touched.
internal sealed class BuiltInPreset : IBuiltInPreset
{
    public const string Name = "SeneaL UI Unlocker";
    private const string Description = "SeneaL UI's layout with this mod's cards and windows placed";
    private const float SettleSeconds = 3f;

    private const string GrowCornersKey = "GrowCorners";

    private readonly ConfigEntry<bool> _applyFirst;
    private readonly ConfigFile _cfg;
    private readonly string _growCorners;
    private readonly ConfigEntry<bool> _checked;
    private readonly ManualLogSource _log;
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, string> _looks;
    private GameObject _row;
    private float _hudSince = -1f;

    private BuiltInPreset(ConfigFile cfg, ManualLogSource log, Dictionary<string, string> values, Dictionary<string, string> looks)
    {
        _cfg = cfg;
        _log = log;
        _values = values;
        // Elements that grow from a corner (Grow Corners) take their corners and reference widths with the preset; the freely placed ones store the middle of their edge, so they land in place even where Grow Corners cannot run.
        _growCorners = looks.TryGetValue(GrowCornersKey, out var corners) ? corners : null;
        looks.Remove(GrowCornersKey);
        _looks = looks;
        _applyFirst = cfg.Bind("Interface", "Use SeneaL UI Unlocker Layout At First", true,
            "On the first world after installing, give the SeneaL UI Unlocker interface preset to a layout that is still SeneaL UI's default or one of its built-in presets. A layout you changed or saved is never touched. The preset is also in the HUD editor's Interfaces menu.");
        _checked = cfg.Bind("Interface", "First Layout Checked", false, new ConfigDescription(
            "Whether the first-world layout check has run, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
    }

    public static IBuiltInPreset Create(ConfigFile cfg, ManualLogSource log)
    {
        _ = typeof(ProfileMenu);
        using var stream = typeof(BuiltInPreset).Assembly.GetManifestResourceStream("SeneaLUIUnlocker.Preset.slui");
        var text = stream == null ? null : new StreamReader(stream).ReadToEnd();
        if (text == null || !Profiles.Parse(text, out _, out Dictionary<string, string> values))
        {
            throw new InvalidDataException("The bundled interface preset could not be read.");
        }
        // SeneaL UI's interface files leave out how this mod's elements grow and what they are attached to, so the preset carries those beside it (Presets\SeneaL UI Unlocker Looks.txt, one key=value per line).
        using var looksStream = typeof(BuiltInPreset).Assembly.GetManifestResourceStream("SeneaLUIUnlocker.PresetLooks.txt");
        var looks = new Dictionary<string, string>();
        foreach (var line in (looksStream == null ? "" : new StreamReader(looksStream).ReadToEnd()).Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                looks[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }
        return new BuiltInPreset(cfg, log, values, looks);
    }

    public void Tick()
    {
        FirstLayout();
        var editor = HudEditor._i;
        var menu = editor == null || !editor._built ? null : editor._profiles;
        if (menu == null || !menu.Open || _row != null)
        {
            return;
        }
        Insert(menu);
    }

    private void Apply(ProfileMenu menu)
    {
        menu?.Remember();
        Profiles.Apply(_values, fromDefaults: true);
        ElementLook.SetLooks(_looks);
        if (_growCorners != null && _cfg.TryGetEntry<string>("HUD Editor", "Grow Corners", out var corners))
        {
            corners.Value = _growCorners;
        }
        CraftingPlacement.Current?.Forget();
        LayoutArea.Adopt();
        if (menu != null)
        {
            menu.Done(Name, Loc.HudProfApplied);
        }
        else
        {
            Prefs.ActiveProfile.Value = Name;
        }
    }

    // Once, after the HUD and every element added to SeneaL UI's editor have settled in the first world.
    private void FirstLayout()
    {
        if (_checked.Value || Prefs.Cfg == null || Hud.instance == null || !HudRoot.Built)
        {
            _hudSince = -1f;
            return;
        }
        if (_hudSince < 0f)
        {
            _hudSince = Time.unscaledTime;
        }
        if (Time.unscaledTime - _hudSince < SettleSeconds)
        {
            return;
        }
        _checked.Value = true;
        if (!_applyFirst.Value)
        {
            return;
        }
        var now = Profiles.Changed(Profiles.Capture());
        var untouched = now.Count == 0 || Profiles.PresetIds.Any(id => Same(now, Profiles.Changed(Profiles.Preset(id))));
        if (!untouched)
        {
            _log.LogInfo("Kept the player's own interface layout; the SeneaL UI Unlocker preset is in the Interfaces menu.");
            return;
        }
        Apply(null);
        _log.LogInfo("Gave the untouched interface layout the SeneaL UI Unlocker preset.");
    }

    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    // The row is a copy of the menu's first built-in row with this preset's name, placed after the last built-in one; the rows below move down by its height and the list grows to hold it. The menu rebuilds its rows on every change and destroys this one with them, and it is put back the next time.
    private void Insert(ProfileMenu menu)
    {
        var rows = menu._rows;
        var builtIns = Profiles.PresetIds.Length;
        if (rows == null || rows.Count < builtIns + 1 || rows[1] == null)
        {
            return;
        }
        var source = (RectTransform)rows[1].transform;
        var last = (RectTransform)rows[builtIns].transform;
        var height = source.sizeDelta.y;
        _row = Object.Instantiate(rows[1], source.parent, false);
        var rect = (RectTransform)_row.transform;
        rect.anchoredPosition = last.anchoredPosition - new Vector2(0f, height);
        rect.SetSiblingIndex(last.GetSiblingIndex() + 1);
        for (var i = builtIns + 1; i < rows.Count; i++)
        {
            if (rows[i] != null)
            {
                ((RectTransform)rows[i].transform).anchoredPosition -= new Vector2(0f, height);
            }
        }
        rows.Add(_row);
        var content = menu._scroll.Content;
        content.sizeDelta = new Vector2(content.sizeDelta.x, content.sizeDelta.y + height);
        var active = Prefs.ActiveProfile != null && Prefs.ActiveProfile.Value == Name;
        foreach (var label in _row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            switch (label.gameObject.name)
            {
                case "t":
                    label.text = Name;
                    label.color = active ? Palette.Gold : Palette.Linen;
                    break;
                case "d":
                    label.text = Description;
                    break;
                case "by":
                    label.text = string.Format(Loc.HudProfBy, "Alkeari");
                    break;
            }
        }
        var highlight = rect.Find("bg") is { } bg ? bg.GetComponent<Image>() : null;
        if (highlight != null)
        {
            highlight.sprite = Skin.Sliced(34, active ? Styles.RowOn : Styles.RowHover);
            highlight.enabled = active;
        }
        var input = _row.GetComponent<UIInputHandler>();
        input.m_onPointerEnter = _ =>
        {
            if (highlight != null && !active)
            {
                highlight.enabled = true;
            }
        };
        input.m_onPointerExit = _ =>
        {
            if (highlight != null && !active)
            {
                highlight.enabled = false;
            }
        };
        input.m_onLeftClick = _ =>
        {
            if (SeneaLUI.Plugin.Active)
            {
                Click.Play();
                Apply(menu);
            }
        };
    }
}
