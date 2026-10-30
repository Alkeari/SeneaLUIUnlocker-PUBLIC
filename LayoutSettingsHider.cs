using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// The type name is what matters: SeneaL UI's mod settings window and Configuration Manager both look for a tag of a type named ConfigurationManagerAttributes and drop an entry whose Browsable is false.
internal sealed class ConfigurationManagerAttributes
{
    public bool? Browsable;
}

// Keeps the HUD editor's own settings out of SeneaL UI's page in the mod settings window, where they number over a thousand. The HUD editor and interface presets still read and write them.
internal sealed class LayoutSettingsHider
{
    private static readonly string[] EditorSections = ["Layout", "Elements", "Sizes", "TimeWind"];
    private static readonly FieldInfo TagsField = AccessTools.Field(typeof(ConfigDescription), "<Tags>k__BackingField");
    private static readonly ConfigurationManagerAttributes Hidden = new() { Browsable = false };

    private readonly ConfigEntry<bool> _enabled;
    private ConfigFile _seneaL;
    private int _seen = -1;
    private bool _applied;

    public LayoutSettingsHider(ConfigEntry<bool> enabled)
    {
        _enabled = enabled;
        _enabled.SettingChanged += (_, _) => _seen = -1;
    }

    // The HUD editor binds entries for other mods' HUD elements as they appear, so new entries are picked up as the count grows.
    public void Tick()
    {
        if (TagsField == null)
        {
            return;
        }
        _seneaL ??= SeneaLConfig.File;
        if (_seneaL == null || _seneaL.Count == _seen)
        {
            return;
        }
        _seen = _seneaL.Count;
        var hide = _enabled.Value;
        if (!hide && !_applied)
        {
            return;
        }
        foreach (var entry in SeneaLConfig.Entries(_seneaL).Where(entry => EditorSections.Contains(entry.Definition.Section)))
        {
            Mark(entry.Description, hide);
        }
        _applied = hide;
        ModSettingsWindow.Refresh();
    }

    private static void Mark(ConfigDescription description, bool hide)
    {
        if (description == null)
        {
            return;
        }
        var tags = description.Tags ?? [];
        var marked = tags.Contains(Hidden);
        if (hide == marked)
        {
            return;
        }
        TagsField.SetValue(description, hide ? tags.Append(Hidden).ToArray() : tags.Where(tag => !ReferenceEquals(tag, Hidden)).ToArray());
    }
}

// SeneaL UI's mod settings window lists entries once and lists them again only when the number of plugins changes.
internal static class ModSettingsWindow
{
    private const string ModsViewType = "SeneaLUI.Hud.ModsView";

    public static void Refresh()
    {
        var type = TypeLookup.Find(ModsViewType);
        if (type == null)
        {
            return;
        }
        var view = AccessTools.Field(type, nameof(SeneaLUI.Hud.ModsView._i))?.GetValue(null);
        var count = AccessTools.Field(type, nameof(SeneaLUI.Hud.ModsView._pluginCount));
        if (view != null && count != null)
        {
            count.SetValue(view, -1);
        }
    }
}
