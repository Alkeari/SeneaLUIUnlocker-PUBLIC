using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using TMPro;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The look settings SeneaL UI's HUD editor gives its own elements, applied to this plugin's: the background's opacity, color and transparency through SeneaL UI's own BackdropSync over the panels drawn with SeneaL UI's Ui.Panel, the caption through SeneaL UI's own ElemCaption, and the text size. Each element's settings are the Prefs.Elem SeneaL UI keeps for an element added through SeneaL API (ExtElements, keyed x.<owner>.<id>).
internal sealed class ElementLooks : IElementLooks
{
    private const float TextCheckSeconds = 0.5f;
    private const float KeepSeconds = 1f;
    private const float MirrorSeconds = 0.2f;

    private static readonly FieldInfo[] Settings = typeof(Prefs.Elem).GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(field => typeof(ConfigEntryBase).IsAssignableFrom(field.FieldType))
        .ToArray();

    private static readonly ConfigFile Unsaved = AccessTools.Field(typeof(Prefs), "Quiet")?.GetValue(null) as ConfigFile;

    private readonly Dictionary<string, State> _states = [];
    private readonly HashSet<string> _restored = new(StringComparer.Ordinal);
    private Dictionary<string, string> _kept;
    private float _nextKeep;
    private float _nextMirror;
    private readonly Dictionary<ConfigEntryBase, Mirror> _mirrors = [];

    private sealed class Mirror
    {
        public ConfigEntryBase Entry;
        public string Last;
    }

    private static readonly MethodInfo BindDefinition = typeof(ConfigFile).GetMethods()
        .FirstOrDefault(m => m.Name == nameof(ConfigFile.Bind) && m.IsGenericMethodDefinition && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(ConfigDefinition) && m.GetParameters()[2].ParameterType == typeof(ConfigDescription));

    private sealed class State
    {
        public readonly BackdropSync Backdrop = new();
        public ElemCaption Caption;
        public RectTransform CaptionSpace;
        public readonly Dictionary<TMP_Text, float> BaseSizes = [];
        public float Scale = 1f;
        public float NextTextCheck;
    }

    public static IElementLooks Create()
    {
        _ = typeof(BackdropSync);
        _ = typeof(ElemCaption);
        _ = ExtElements.Elems;
        return new ElementLooks();
    }

    private static Prefs.Elem Elem(string id) => ExtElements.Elems.TryGetValue($"x.{Plugin.Guid}.{id}", out var e) ? e : null;

    public float TextScale(string id)
    {
        var e = Elem(id);
        return e == null || e.TextK == null ? 1f : e.TextK.Value;
    }

    public void Tick(ICollection<string> selfScaled)
    {
        foreach (var (id, rect) in SeneaLHud.Registrations)
        {
            if (rect == null)
            {
                continue;
            }
            var e = Elem(id);
            if (e == null)
            {
                continue;
            }
            if (_restored.Add(id))
            {
                Restore(e);
            }
            if (!_states.TryGetValue(id, out var state))
            {
                _states[id] = state = new State();
            }
            var look = SeneaLHud.LookRootOf(id, rect);
            state.Backdrop.Tick(e, look);
            var space = rect.root as RectTransform;
            if (state.Caption == null || state.CaptionSpace != space)
            {
                state.CaptionSpace = space;
                state.Caption = space == null ? null : new ElemCaption(e, space);
            }
            state.Caption?.Tick(rect.gameObject.activeInHierarchy && e.LiveFrame != null ? e.LiveFrame() : Rect.zero, null);
            if (!selfScaled.Contains(id))
            {
                ScaleText(look, state, e.TextK == null ? 1f : e.TextK.Value);
            }
        }
        if (Time.unscaledTime >= _nextMirror)
        {
            _nextMirror = Time.unscaledTime + MirrorSeconds;
            SyncMirrors();
        }
        if (Time.unscaledTime >= _nextKeep)
        {
            _nextKeep = Time.unscaledTime + KeepSeconds;
            Keep();
        }
    }

    public void Tip(RectTransform rect, string title, string body) =>
        SeneaLUI.Components.Tooltip.Bind(rect, () => SeneaLUI.Inv.Tips.Text(new SeneaLUI.Components.Tip(), title, body));

    public void SetGrow(string id, int grow)
    {
        var e = Elem(id);
        if (e?.Grow == null)
        {
            return;
        }
        _restored.Add(id);
        e.Grow.Value = grow;
        Keep();
    }

    // Every look and attachment setting SeneaL UI does not save for this plugin's elements, each set to the given value or back to its default.
    public void SetLooks(IReadOnlyDictionary<string, string> looks)
    {
        foreach (var (id, _) in SeneaLHud.Registrations)
        {
            var e = Elem(id);
            if (e == null)
            {
                continue;
            }
            _restored.Add(id);
            // A SeneaL UI build that saves an element's attachment itself (1.2.0-preview.10 on) was reset with the rest of the interface; the preset's value is set on it the same way.
            foreach (var entry in Saved(e))
            {
                if (looks.TryGetValue(entry.Definition.Key, out var value))
                {
                    try
                    {
                        entry.SetSerializedValue(value);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            foreach (var entry in Unkept(e))
            {
                try
                {
                    if (looks.TryGetValue(entry.Definition.Key, out var value))
                    {
                        entry.SetSerializedValue(value);
                    }
                    else
                    {
                        entry.BoxedValue = entry.DefaultValue;
                    }
                    if (_mirrors.TryGetValue(entry, out var mirror) && mirror != null)
                    {
                        mirror.Entry.SetSerializedValue(entry.GetSerializedValue());
                        mirror.Last = mirror.Entry.GetSerializedValue();
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        Keep();
    }

    // SeneaL UI saves an interface, and builds an export code, from its own settings file only, where the grow and attachment settings of an element added through SeneaL API are not kept. Each of them gets a copy there under the same section and key, hidden from settings lists, kept equal to it both ways: an interface applied sets the copy, an edit in the HUD editor sets the setting.
    private void SyncMirrors()
    {
        if (Prefs.Cfg == null || BindDefinition == null)
        {
            return;
        }
        // Every bind and set rewrites SeneaL UI's whole file; the first pass after a world loads binds every copy at once, which held one frame for seconds, so the file is written once after the pass.
        var save = Prefs.Cfg.SaveOnConfigSet;
        var written = false;
        Prefs.Cfg.SaveOnConfigSet = false;
        try
        {
            foreach (var (id, _) in SeneaLHud.Registrations)
            {
                var e = Elem(id);
                if (e == null || !_restored.Contains(id))
                {
                    continue;
                }
                foreach (var entry in Unkept(e))
                {
                    if (!_mirrors.TryGetValue(entry, out var mirror))
                    {
                        _mirrors[entry] = mirror = Bind(entry);
                        written = true;
                        if (mirror == null)
                        {
                            continue;
                        }
                    }
                    if (mirror == null)
                    {
                        continue;
                    }
                    var copy = mirror.Entry.GetSerializedValue();
                    var own = entry.GetSerializedValue();
                    try
                    {
                        // An interface saved before these copies existed records none of them, and applying it sets each copy back to its default; that clears the element's attachment and look, so the element keeps them instead.
                        if (copy != mirror.Last && copy == Default(mirror.Entry) && !SavedInActive(mirror.Entry.Definition))
                        {
                            mirror.Entry.SetSerializedValue(own);
                            mirror.Last = own;
                            written = true;
                        }
                        else if (copy != mirror.Last)
                        {
                            entry.SetSerializedValue(copy);
                            mirror.Last = copy;
                        }
                        else if (own != mirror.Last)
                        {
                            mirror.Entry.SetSerializedValue(own);
                            mirror.Last = own;
                            written = true;
                        }
                    }
                    catch (Exception)
                    {
                        mirror.Last = copy;
                    }
                }
            }
        }
        finally
        {
            Prefs.Cfg.SaveOnConfigSet = save;
            if (save && written)
            {
                Prefs.Cfg.Save();
            }
        }
    }

    private static string Default(ConfigEntryBase entry) => TomlTypeConverter.ConvertToString(entry.DefaultValue, entry.SettingType);

    private string _activePath;
    private DateTime _activeWritten;
    private HashSet<string> _activeKeys;

    // Whether the interface SeneaL UI applied last, as saved in its profiles folder, holds this setting; an interface with no file (SeneaL UI's built-in ones) counts as holding every setting.
    private bool SavedInActive(ConfigDefinition definition)
    {
        var name = SeneaLConfig.ActiveProfile;
        var path = name == null ? null : System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "SeneaL-UI", "profiles", PresetFix.Safe(name) + ".slui");
        if (path == null || !System.IO.File.Exists(path))
        {
            return true;
        }
        var written = System.IO.File.GetLastWriteTimeUtc(path);
        if (_activeKeys == null || _activePath != path || _activeWritten != written)
        {
            _activePath = path;
            _activeWritten = written;
            _activeKeys = new HashSet<string>(PresetFix.Read(path).Select(pair => pair.Key), StringComparer.Ordinal);
        }
        return _activeKeys.Contains(definition.Section + "/" + definition.Key);
    }

    private static Mirror Bind(ConfigEntryBase entry)
    {
        try
        {
            var description = new ConfigDescription(entry.Description?.Description ?? "", entry.Description?.AcceptableValues, new ConfigurationManagerAttributes { Browsable = false });
            var bound = (ConfigEntryBase)BindDefinition.MakeGenericMethod(entry.SettingType).Invoke(Prefs.Cfg, [entry.Definition, entry.DefaultValue, description]);
            var stored = bound.GetSerializedValue();
            var fallback = TomlTypeConverter.ConvertToString(entry.DefaultValue, entry.SettingType);
            // A value already in SeneaL UI's file came from a saved interface or an earlier session and wins; otherwise the copy starts from the setting.
            if (stored != fallback)
            {
                entry.SetSerializedValue(stored);
            }
            else
            {
                bound.SetSerializedValue(entry.GetSerializedValue());
            }
            return new Mirror { Entry = bound, Last = bound.GetSerializedValue() };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<ConfigEntryBase> Unkept(Prefs.Elem e) =>
        Unsaved == null ? [] : Settings.Select(field => field.GetValue(e) as ConfigEntryBase).Where(entry => entry != null && entry.ConfigFile == Unsaved);

    private Dictionary<string, string> Kept()
    {
        if (_kept != null)
        {
            return _kept;
        }
        _kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (ElementLook.Kept?.Value ?? "").Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                _kept[Uri.UnescapeDataString(part.Substring(0, eq))] = Uri.UnescapeDataString(part.Substring(eq + 1));
            }
        }
        return _kept;
    }

    private static IEnumerable<ConfigEntryBase> Saved(Prefs.Elem e) =>
        Unsaved == null ? [] : Settings.Select(field => field.GetValue(e) as ConfigEntryBase).Where(entry => entry != null && entry.ConfigFile != Unsaved);

    private void Restore(Prefs.Elem e)
    {
        var kept = Kept();
        // A setting this plugin kept while SeneaL UI did not save it, which a newer SeneaL UI now saves itself (attachments from 1.2.0-preview.10 on), is handed over once where SeneaL UI's own value is still its default, and is no longer kept here.
        var handed = false;
        foreach (var entry in Saved(e))
        {
            if (!kept.TryGetValue(entry.Definition.Key, out var value))
            {
                continue;
            }
            try
            {
                if (entry.GetSerializedValue() == Default(entry))
                {
                    entry.SetSerializedValue(value);
                }
            }
            catch (Exception)
            {
            }
            kept.Remove(entry.Definition.Key);
            handed = true;
        }
        if (handed && ElementLook.Kept != null)
        {
            ElementLook.Kept.Value = string.Join(";", kept.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        }
        foreach (var entry in Unkept(e))
        {
            if (kept.TryGetValue(entry.Definition.Key, out var value))
            {
                try
                {
                    entry.SetSerializedValue(value);
                }
                catch (Exception)
                {
                    kept.Remove(entry.Definition.Key);
                }
            }
        }
    }

    private void Keep()
    {
        if (ElementLook.Kept == null)
        {
            return;
        }
        var kept = Kept();
        var changed = false;
        foreach (var (id, _) in SeneaLHud.Registrations)
        {
            var e = Elem(id);
            if (e == null || !_restored.Contains(id))
            {
                continue;
            }
            foreach (var entry in Unkept(e))
            {
                var value = entry.GetSerializedValue();
                var key = entry.Definition.Key;
                var isDefault = value == TomlTypeConverter.ConvertToString(entry.DefaultValue, entry.SettingType);
                if (isDefault ? kept.Remove(key) : !kept.TryGetValue(key, out var old) || old != value)
                {
                    if (!isDefault)
                    {
                        kept[key] = value;
                    }
                    changed = true;
                }
            }
        }
        if (changed)
        {
            ElementLook.Kept.Value = string.Join(";", kept.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        }
    }

    // Every text in the element at its own design size times the element's text size, measured from the size each text had when it was first seen.
    private static void ScaleText(RectTransform rect, State state, float scale)
    {
        if (scale == state.Scale && Time.unscaledTime < state.NextTextCheck)
        {
            return;
        }
        state.NextTextCheck = Time.unscaledTime + TextCheckSeconds;
        var gone = new List<TMP_Text>();
        foreach (var text in state.BaseSizes.Keys)
        {
            if (text == null)
            {
                gone.Add(text);
            }
        }
        foreach (var text in gone)
        {
            state.BaseSizes.Remove(text);
        }
        var changed = scale != state.Scale;
        state.Scale = scale;
        foreach (var text in rect.GetComponentsInChildren<TMP_Text>(true))
        {
            if (!state.BaseSizes.TryGetValue(text, out var size))
            {
                state.BaseSizes[text] = size = text.fontSize;
                changed = true;
            }
            if (changed && !Mathf.Approximately(text.fontSize, size * scale))
            {
                text.fontSize = size * scale;
            }
        }
    }
}
