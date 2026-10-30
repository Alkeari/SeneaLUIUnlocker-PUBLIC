using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Where an element appears in play, and so which HUD editor mode edits it: the open HUD, the inventory and crafting stations (Tab), or any other screen such as the Esc menu (Other).
internal enum ElementMode
{
    Hud,
    Tab,
    Other
}

// This plugin's on-screen elements join SeneaL UI's HUD editor through SeneaL API (HudElements.Add), so they are moved, sized, hidden and saved in interface presets exactly like SeneaL UI's own; without SeneaL API installed they keep their fallback place.
internal static class SeneaLHud
{
    private static readonly Type Elements = TypeLookup.Find("SeneaL.API.HudElements");
    private static readonly MethodInfo Add = Elements == null ? null : AccessTools.Method(Elements, "Add", [typeof(string), typeof(string), typeof(string), typeof(RectTransform)]);
    private static readonly Type Editor = TypeLookup.Find("SeneaLUI.Hud.HudEditor");
    private static readonly PropertyInfo Open = Editor == null ? null : AccessTools.Property(Editor, "Open");
    private static readonly Type Loc = TypeLookup.Find("SeneaLUI.Core.Loc");
    private static readonly FieldInfo Langs = Loc == null ? null : AccessTools.Field(Loc, "Langs");
    private static readonly FieldInfo English = Loc == null ? null : AccessTools.Field(Loc, "_en");

    private static readonly MethodInfo Remove = Elements == null ? null : AccessTools.Method(Elements, "Remove");
    private static readonly System.Collections.Generic.List<(string Id, string Title, RectTransform Rect)> Registered = [];
    private static readonly System.Collections.Generic.Dictionary<string, object> Live = [];
    private static bool _parked;

    private static readonly System.Collections.Generic.Dictionary<string, ElementMode> Modes = [];

    public static bool EditorOpen => Open != null && Open.GetValue(null, null) is true;

    // SeneaL API is missing (SeneaL UI 1.1.x), so this plugin places its elements itself.
    public static bool Fallback => Add == null;

    public static RectTransform RectOf(string id)
    {
        foreach (var (each, _, rect) in Registered)
        {
            if (each == id)
            {
                return rect;
            }
        }
        return null;
    }

    private static readonly PropertyInfo ModsOpen = TypeLookup.Find("SeneaLUI.Hud.ModsView") is { } mods ? AccessTools.Property(mods, "Open") : null;

    // SeneaL UI's mod settings window (opened from the Esc menu too), drawn below the Esc menu cards' canvas.
    public static bool SettingsOpen => ModsOpen != null && ModsOpen.GetValue(null, null) is true;

    private static readonly FieldInfo SettingsViewField = TypeLookup.Find("SeneaLUI.Patches.InventoryGuiPatches") is { } patches ? AccessTools.Field(patches, "Settings") : null;
    private static readonly PropertyInfo SettingsShown = SettingsViewField == null ? null : AccessTools.Property(SettingsViewField.FieldType, "Shown");
    private static readonly PropertyInfo SettingsStandalone = SettingsViewField == null ? null : AccessTools.Property(SettingsViewField.FieldType, "Standalone");

    // SeneaL UI's settings screen (Esc menu, SeneaL UI Settings) is drawn inside the inventory window, and opened alone it makes InventoryGui.IsVisible answer true.
    public static bool SettingsScreenOpen => SettingsViewField?.GetValue(null) is { } view && (SettingsShown?.GetValue(view, null) is true || SettingsStandalone?.GetValue(view, null) is true);

    // The player's own inventory open, without SeneaL UI's settings screen over it.
    public static bool InventoryOpen => InventoryGui.IsVisible() && !SettingsScreenOpen;

    // The HUD editor mode in use, kept by the editor additions; HUD whenever the editor is closed.
    public static ElementMode EditorMode { get; set; }

    // While the HUD editor is open in this element's mode, it shows sample content so it can be found and placed.
    public static bool Editing(ElementMode mode) => EditorOpen && EditorMode == mode;

    public static ElementMode ModeOf(string id) => Modes.TryGetValue(id, out var mode) ? mode : ElementMode.Hud;

    private static readonly System.Collections.Generic.Dictionary<string, RectTransform> LookRoots = [];

    // Where an element's look settings (background, color, text size) apply when that is not the rectangle the editor places, as for the chat, placed through a stand-in.
    public static void LookRoot(string id, RectTransform rect) => LookRoots[id] = rect;

    public static RectTransform LookRootOf(string id, RectTransform placed) => LookRoots.TryGetValue(id, out var rect) && rect != null ? rect : placed;

    public static System.Collections.Generic.IEnumerable<(string Id, RectTransform Rect)> Registrations
    {
        get
        {
            foreach (var (id, _, rect) in Registered)
            {
                yield return (id, rect);
            }
        }
    }

    private static bool _keeping;
    private static readonly Vector3[] Corners = new Vector3[4];

    // An element placed by its grow point near a screen edge reaches past the edge once its content is larger than in the editor (the Food Planner's cards beside the inventory): right before drawing, each element that would reach past the screen is moved back inside it, outside the editor, where SeneaL UI keeps it within its own limits.
    private static void KeepOnScreen()
    {
        if (EditorOpen)
        {
            return;
        }
        FallbackColumn.Place();
        foreach (var (_, _, rect) in Registered)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy)
            {
                continue;
            }
            var canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                continue;
            }
            rect.GetWorldCorners(Corners);
            var min = Vector2.Min(Corners[0], Corners[2]);
            var max = Vector2.Max(Corners[0], Corners[2]);
            var dx = min.x < 0f ? -min.x : max.x > Screen.width ? Screen.width - max.x : 0f;
            var dy = min.y < 0f ? -min.y : max.y > Screen.height ? Screen.height - max.y : 0f;
            if (dx != 0f || dy != 0f)
            {
                rect.position += new Vector3(dx, dy, 0f);
            }
        }
    }

    // Null only when SeneaL API is missing, so the caller keeps its own fallback placement; the element is still recorded for what keeps it on screen and clear of other panels.
    public static object Register(string id, string title, RectTransform rect, ElementMode mode = ElementMode.Hud)
    {
        if (!_keeping)
        {
            _keeping = true;
            Canvas.willRenderCanvases += KeepOnScreen;
        }
        Modes[id] = mode;
        Registered.RemoveAll(entry => entry.Id == id);
        Registered.Add((id, title, rect));
        if (Add == null)
        {
            return null;
        }
        Name(id, title);
        return _parked ? Registered : Put(id, title, rect);
    }

    // Elements that outlive a world (their canvas survives scene changes) leave SeneaL UI's HUD while no world is loaded and come back with the next one: SeneaL UI keeps placing every element it holds, and one attached to the minimap reads the minimap's destroyed frame on the main menu, an exception every frame.
    public static void Tick()
    {
        Wake();
        var inWorld = Hud.instance != null;
        if (!inWorld && !_parked)
        {
            _parked = true;
            foreach (var element in Live.Values)
            {
                try
                {
                    Remove?.Invoke(null, [element]);
                }
                catch (Exception)
                {
                }
            }
            Live.Clear();
        }
        else if (inWorld && _parked)
        {
            _parked = false;
            Registered.RemoveAll(entry => entry.Rect == null);
            foreach (var (id, title, rect) in Registered)
            {
                Put(id, title, rect);
            }
        }
        if (_saveOwed)
        {
            _saveOwed = false;
            SeneaLConfig.File?.Save();
        }
    }

    private static bool _saveOwed;

    private const float BusyFor = 0.25f;
    private static readonly System.Collections.Generic.Dictionary<string, int> Shown = [];
    private static readonly System.Collections.Generic.Dictionary<string, float> BusyUntil = [];
    private static readonly System.Collections.Generic.List<TMPro.TMP_Text> Texts = [];
    private static PropertyInfo _busy;

    // SeneaL UI's "hide when idle" option fades an element once SeneaL API has not seen it busy for the set seconds, and brings it back only when it is busy again: each element is busy for a moment whenever what it shows changes (it appears, or its text changes), and the chat while the player types in it.
    private static void Wake()
    {
        if (Live.Count == 0)
        {
            return;
        }
        var now = Time.unscaledTime;
        foreach (var (id, _, rect) in Registered)
        {
            if (!Live.TryGetValue(id, out var element) || element == null)
            {
                continue;
            }
            var shown = Signature(LookRootOf(id, rect));
            if (!Shown.TryGetValue(id, out var before) || before != shown || id == "Chat" && Chat.instance != null && Chat.instance.HasFocus())
            {
                BusyUntil[id] = now + BusyFor;
            }
            Shown[id] = shown;
            _busy ??= AccessTools.Property(element.GetType(), "Busy");
            var busy = BusyUntil.TryGetValue(id, out var until) && now < until;
            if (_busy != null && _busy.GetValue(element, null) is bool was && was != busy)
            {
                _busy.SetValue(element, busy, null);
            }
        }
    }

    private static int Signature(RectTransform rect)
    {
        if (rect == null || !rect.gameObject.activeInHierarchy)
        {
            return 0;
        }
        var hash = 17;
        rect.GetComponentsInChildren(false, Texts);
        foreach (var text in Texts)
        {
            if (text.enabled)
            {
                hash = hash * 31 + (text.text?.GetHashCode() ?? 0);
            }
        }
        Texts.Clear();
        return hash == 0 ? 1 : hash;
    }

    private static object Put(string id, string title, RectTransform rect)
    {
        if (Add == null)
        {
            return null;
        }
        // SeneaL UI binds about ten settings for an element it has not held before, and each bind rewrites its whole file: every element joining a world in one frame cost 2.5 s, so the file is written once, at the next Tick.
        var cfg = SeneaLConfig.File;
        var save = cfg != null && cfg.SaveOnConfigSet;
        if (save)
        {
            cfg.SaveOnConfigSet = false;
        }
        try
        {
            var element = Add.Invoke(null, [Plugin.Guid, id, title, rect]);
            Live[id] = element;
            return element;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (save)
            {
                cfg.SaveOnConfigSet = true;
                _saveOwed = true;
            }
        }
    }

    // SeneaL UI's settings window (F7) names each element by its text key Elem.<element key>, which SeneaL API does not fill for an added element, so it would show the key itself: the title goes into each of SeneaL UI's languages under that key.
    private static void Name(string id, string title)
    {
        var key = $"Elem.x.{Plugin.Guid}.{id}";
        var langs = Langs == null ? null : Langs.GetValue(null) as System.Collections.IEnumerable;
        if (langs != null)
        {
            foreach (var lang in langs)
            {
                PutName(lang, key, title);
            }
        }
        PutName(English == null ? null : English.GetValue(null), key, title);
    }

    private static void PutName(object lang, string key, string title)
    {
        if (lang != null && AccessTools.Field(lang.GetType(), "Map")?.GetValue(lang) is System.Collections.Generic.Dictionary<string, string> map && !map.ContainsKey(key))
        {
            map[key] = title;
        }
    }
}

// SeneaL UI's per-element look settings for this plugin's elements (ElementLooks), reached only through this facade so a SeneaL UI build without them leaves every element at its own look.
internal static class ElementLook
{
    private static readonly System.Collections.Generic.HashSet<string> SelfScaled = new(System.StringComparer.Ordinal);
    private static IElementLooks _looks;
    private static bool _tried;

    // The look settings SeneaL UI keeps for elements added through SeneaL API only in memory (grow direction, background, caption, colors, text size), kept here so they last.
    public static BepInEx.Configuration.ConfigEntry<string> Kept { get; private set; }

    public static void Bind(BepInEx.Configuration.ConfigFile cfg) =>
        Kept = cfg.Bind("HUD Editor", "Element Look Settings", "", new BepInEx.Configuration.ConfigDescription(
            "Look settings of this mod's HUD editor elements that SeneaL UI does not save itself, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));

    public static void SetGrow(string id, int grow) => Looks()?.SetGrow(id, grow);

    public static void SetLooks(System.Collections.Generic.IReadOnlyDictionary<string, string> looks) => Looks()?.SetLooks(looks);

    // A tooltip in SeneaL UI's own look on a control this plugin draws outside SeneaL UI's windows.
    public static void Tip(UnityEngine.RectTransform rect, string title, string body) => Looks()?.Tip(rect, title, body);

    // An element that sizes its own text from TextScale (a card that measures itself), left out of the generic text scaling.
    public static void Own(string id) => SelfScaled.Add(id);

    public static float TextScale(string id) => Looks() == null ? 1f : _looks.TextScale(id);

    public static void Tick() => Looks()?.Tick(SelfScaled);

    private static IElementLooks Looks()
    {
        if (!_tried)
        {
            _tried = true;
            try
            {
                _looks = CreateLooks();
            }
            catch (System.Exception)
            {
                _looks = null;
            }
        }
        return _looks;
    }

    private static IElementLooks CreateLooks() => (IElementLooks)WindowAdditions.Create("ElementLooks");
}
