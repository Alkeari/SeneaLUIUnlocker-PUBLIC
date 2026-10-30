using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

internal interface ISettingsPage
{
    void Tick();
}

// This plugin's page in SeneaL UI's mod settings window. SeneaL UI draws every plugin's settings as one flat list with a fixed set of controls (a switch, left and right arrows, a slider, a key box or a text box); while this plugin is the one selected, that list is hidden and this page is drawn in its place with SeneaL UI's own components:
// collapsible sections, every key setting gathered in Hotkeys at the end, dropdowns for every choice, sliders with an editable value, one switch per entry for every list of names, and line lists for servers, presets and restock targets. The settings themselves stay ordinary BepInEx config entries.
internal sealed partial class SettingsPage : ISettingsPage
{
    private const string HotkeysTitle = "Hotkeys";
    // A config section named "<Section>: <Part>" shows as a subsection of <Section>: every cheat sits in Cheats this way, and each kind of area in Previews.
    private const string CheatsTitle = "Cheats";
    private const string PartMark = ": ";
    private const float EditorW = 300f;
    private const float ResetW = 26f;
    private const float HeadH = 44f;
    private const float MinRowH = 46f;
    private const float MenuPpu = 50f;

    private static readonly KeyCode[] Mods = [KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftAlt, KeyCode.RightAlt];

    // Sections in the order a player looks for them; any section not named here follows in alphabetical order, and Hotkeys comes last.
    private static readonly string[] SectionOrder =
    [
        "General", "Servers", "Unlocked Features", "Unlocked Limits", "Interface", "HUD Editor", "Combat HUD", "Status Cards",
        "Inventory", "Trash", "Safety", "Find In Chests", "Building", "Previews", "Doors", "Reminders", "Alerts", "Loadouts", "Tames",
        "Party", "Party HUD", "Map", "Live Pins", "Auto Pins", "Radar", "Chat", "Crafting", "Recipes", "Accessibility", "Compatibility",
        "SeneaL UI Keys"
    ];

    private readonly ConfigFile _cfg;
    private readonly ManualLogSource _log;
    private readonly ConfigEntry<string> _expanded;
    private readonly List<Block> _blocks = [];

    private ModsView _view;
    private bool _menu;
    private int _entryCount = -1;
    private ScrollBox _scroll;
    private RectTransform _host;
    private float _rowW;
    private bool _layout;
    private string _term = "";
    private float _syncIn;
    private bool _hidList;
    private KeyRow _capture;

    private SettingsPage(ConfigFile cfg, ManualLogSource log)
    {
        _cfg = cfg;
        _log = log;
        _expanded = cfg.Bind("Settings Page", "Expanded Sections", "General", new ConfigDescription(
            "Sections of this page left open, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
        Canvas.willRenderCanvases += KeepListHidden;
    }

    public static ISettingsPage Create(ConfigFile cfg, ManualLogSource log)
    {
        _ = typeof(ModsView);
        return new SettingsPage(cfg, log);
    }

    private static ModsView View => ModsView._i;

    private bool Ours(ModsView view) =>
        view != null && view._built && view._open && view._sel != null && view._sel.Info != null && view._sel.Info.Metadata.GUID == Plugin.Guid;

    public void Tick()
    {
        var view = View;
        if (!Ours(view))
        {
            Leave(view);
            return;
        }
        var menu = FejdStartup.instance != null;
        if (view != _view || menu != _menu || _cfg.Count != _entryCount || _host == null)
        {
            Build(view, menu);
        }
        Place(view);
        _scroll.Root.gameObject.SetActive(true);
        var term = (view._term ?? "").Trim().ToLowerInvariant();
        if (term != _term)
        {
            _term = term;
            _layout = true;
        }
        if (_layout)
        {
            Layout();
        }
        if (!OverPopup())
        {
            _scroll.Tick(view);
        }
        foreach (var block in _blocks)
        {
            foreach (var row in block.Rows)
            {
                if (row.Root.gameObject.activeSelf)
                {
                    row.Tick(view);
                }
            }
        }
        TickCapture();
        _syncIn -= Time.unscaledDeltaTime;
        if (_syncIn <= 0f)
        {
            _syncIn = 0.4f;
            foreach (var row in _blocks.SelectMany(block => block.Rows))
            {
                if (row.Sync())
                {
                    _layout = true;
                }
            }
        }
    }

    private void Leave(ModsView view)
    {
        if (_scroll != null && _scroll.Root != null && _scroll.Root.gameObject.activeSelf)
        {
            _scroll.Root.gameObject.SetActive(false);
            foreach (var row in _blocks.SelectMany(block => block.Rows))
            {
                row.Close();
            }
        }
        if (_hidList && view != null && view._list != null && view._list.Root != null)
        {
            view._list.Root.gameObject.SetActive(true);
        }
        _hidList = false;
        _capture = null;
    }

    // SeneaL UI's own list is turned back on by SeneaL UI itself as it rebuilds, so it is turned off again right before every frame is drawn.
    private void KeepListHidden()
    {
        var view = View;
        if (!Ours(view) || _scroll == null || view._list == null || view._list.Root == null)
        {
            return;
        }
        if (view._list.Root.gameObject.activeSelf)
        {
            view._list.Root.gameObject.SetActive(false);
        }
        _hidList = true;
    }

    private void Place(ModsView view)
    {
        var list = view._list.Root;
        var at = list.anchoredPosition;
        var size = list.sizeDelta;
        if (_scroll.Root.anchoredPosition != at || _scroll.Root.sizeDelta != size)
        {
            _scroll.Place(at.x, -at.y, size.x, size.y, 6f);
            _layout = true;
        }
    }

    // On the main menu SeneaL UI has no HUD to take its drawing scale from, so drawings made here use the scale its mod settings window has there and are taken back out of SeneaL UI's drawing cache afterwards, as MainMenuModSettings does for the window itself.
    private void Draw(Action draw)
    {
        if (!_menu)
        {
            draw();
            return;
        }
        var cache = (IDictionary)AccessTools.Field(typeof(Skin), "Cache").GetValue(null);
        var before = new HashSet<object>(cache.Keys.Cast<object>());
        var ppu = SkinScale.Ppu;
        try
        {
            SkinScale.Ppu = MenuPpu;
            draw();
        }
        finally
        {
            SkinScale.Ppu = ppu;
            foreach (var key in cache.Keys.Cast<object>().Where(key => !before.Contains(key)).ToList())
            {
                cache.Remove(key);
            }
        }
    }

    private void Build(ModsView view, bool menu)
    {
        foreach (var row in _blocks.SelectMany(block => block.Rows))
        {
            row.Close();
        }
        if (_scroll != null && _scroll.Root != null)
        {
            UnityEngine.Object.Destroy(_scroll.Root.gameObject);
        }
        _blocks.Clear();
        _view = view;
        _menu = menu;
        _entryCount = _cfg.Count;
        _host = (RectTransform)view._list.Root.parent;
        _rowW = view._rowW;
        Draw(() =>
        {
            _scroll = new ScrollBox(_host, "unlocker.page", view._listW, view._list.Root.sizeDelta.y, 138f);
            var open = Split(_expanded.Value);
            foreach (var (title, entries) in Sections())
            {
                var block = new Block(this, title, open.Contains(title));
                foreach (var entry in entries)
                {
                    block.Rows.AddRange(MakeRows(block, entry));
                }
                if (block.Rows.Count > 0)
                {
                    _blocks.Add(block);
                }
                else
                {
                    UnityEngine.Object.Destroy(block.Head.gameObject);
                }
            }
        });
        _layout = true;
        _syncIn = 0f;
    }

    private IEnumerable<(string Title, List<ConfigEntryBase> Entries)> Sections()
    {
        var shown = SeneaLConfig.Entries(_cfg).Where(Browsable).ToList();
        // Every key setting, and whatever else a config section named Hotkeys holds, goes in the one Hotkeys block, so no second block of that name forms.
        var keys = shown.Where(entry => entry.SettingType == typeof(KeyboardShortcut) || entry.SettingType == typeof(KeyCode) || Title(entry.Definition.Section) == HotkeysTitle).ToList();
        var groups = shown.Except(keys).GroupBy(entry => Title(entry.Definition.Section))
            .OrderBy(group => group.Key == CheatsTitle ? int.MaxValue : Array.IndexOf(SectionOrder, group.Key) is var i && i >= 0 ? i : SectionOrder.Length)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var parts = group.Select(entry => entry.Definition.Section).Distinct().ToList();
            yield return (group.Key, group.OrderBy(entry => entry.Definition.Section == group.Key ? -1 : parts.IndexOf(entry.Definition.Section)).ToList());
        }
        if (keys.Count > 0)
        {
            yield return (HotkeysTitle, keys.OrderBy(entry => entry.Definition.Section == HotkeysTitle ? -1 : Array.IndexOf(SectionOrder, entry.Definition.Section) is var i && i >= 0 ? i : SectionOrder.Length)
                .ThenBy(entry => entry.Definition.Section, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    private static string Title(string section) => section.IndexOf(PartMark, StringComparison.Ordinal) is var at && at > 0 ? section.Substring(0, at) : section;

    private static string Part(string section) => section.IndexOf(PartMark, StringComparison.Ordinal) is var at && at > 0 ? section.Substring(at + PartMark.Length) : null;

    private static bool Browsable(ConfigEntryBase entry) =>
        entry.Description?.Tags?.OfType<ConfigurationManagerAttributes>().All(tag => tag.Browsable != false) ?? true;

    private IEnumerable<Row> MakeRows(Block block, ConfigEntryBase entry)
    {
        if (block.Title == HotkeysTitle && entry.Definition.Section != HotkeysTitle && block.Rows.OfType<SubHead>().LastOrDefault()?.Text != entry.Definition.Section)
        {
            yield return new SubHead(this, block, entry.Definition.Section);
        }
        var part = Part(entry.Definition.Section);
        if (block.Title != HotkeysTitle && part != null && block.Rows.OfType<SubHead>().LastOrDefault()?.Text != part)
        {
            yield return new SubHead(this, block, part);
        }
        if (Folded(entry))
        {
            yield break;
        }
        var special = ListRow(block, entry);
        if (special != null)
        {
            yield return special;
            yield break;
        }
        var type = entry.SettingType;
        var acceptable = entry.Description?.AcceptableValues;
        if (type == typeof(bool))
        {
            yield return new SwitchRow(this, block, entry);
        }
        else if (type == typeof(KeyboardShortcut) || type == typeof(KeyCode))
        {
            yield return new KeyRow(this, block, entry);
        }
        else if (type.IsEnum || acceptable != null && acceptable.GetType().Name.StartsWith("AcceptableValueList", StringComparison.Ordinal))
        {
            yield return new ChoiceRow(this, block, entry);
        }
        else if (acceptable != null && acceptable.GetType().Name.StartsWith("AcceptableValueRange", StringComparison.Ordinal))
        {
            yield return new RangeRow(this, block, entry);
        }
        else if (type == typeof(Color))
        {
            yield return new ColorRow(this, block, entry);
        }
        else
        {
            yield return new TextRow(this, block, entry);
        }
    }

    private void Layout()
    {
        _layout = false;
        var y = 0f;
        foreach (var block in _blocks)
        {
            var matches = block.Rows.Where(row => _term.Length == 0 || row.Search.Contains(_term) || block.Title.ToLowerInvariant().Contains(_term)).ToList();
            var visible = _term.Length == 0 || matches.Count > 0;
            block.Head.gameObject.SetActive(visible);
            if (!visible)
            {
                foreach (var row in block.Rows)
                {
                    row.Show(false);
                }
                continue;
            }
            block.Head.anchoredPosition = new Vector2(12f, -y);
            block.Paint(_term.Length > 0 || block.Open);
            y += HeadH;
            var open = _term.Length > 0 || block.Open;
            foreach (var row in block.Rows)
            {
                var show = open && (_term.Length == 0 || matches.Contains(row) || row is SubHead);
                row.Show(show);
                if (!show)
                {
                    continue;
                }
                row.Root.anchoredPosition = new Vector2(12f, -y);
                y += row.Height;
            }
            y += 6f;
        }
        _scroll.SetContentHeight(Mathf.Max(y + 12f, 60f));
    }

    private void Toggle(Block block)
    {
        block.Open = !block.Open;
        var open = Split(_expanded.Value);
        if (block.Open)
        {
            open.Add(block.Title);
        }
        else
        {
            open.Remove(block.Title);
        }
        _expanded.Value = string.Join("; ", open);
        _layout = true;
    }

    private void Relayout() => _layout = true;

    private void BeginCapture(KeyRow row)
    {
        _capture?.Paint(false);
        _capture = row;
        row.Paint(true);
    }

    private void TickCapture()
    {
        var row = _capture;
        if (row == null)
        {
            return;
        }
        if (RawKeys.Down(KeyCode.Escape))
        {
            _capture = null;
            row.Paint(false);
            return;
        }
        if (RawKeys.Down(KeyCode.Backspace) || RawKeys.Down(KeyCode.Delete))
        {
            _capture = null;
            row.Set(KeyboardShortcut.Empty);
            return;
        }
        foreach (var key in RawKeys.Capturable(Mods))
        {
            if (RawKeys.Down(key))
            {
                _capture = null;
                row.Set(new KeyboardShortcut(key, Mods.Where(RawKeys.Held).ToArray()));
                return;
            }
        }
        foreach (var mod in Mods)
        {
            if (RawKeys.Up(mod))
            {
                _capture = null;
                row.Set(new KeyboardShortcut(mod));
                return;
            }
        }
    }

    internal static HashSet<string> Split(string text) =>
        new((text ?? "").Split(';').Select(part => part.Trim()).Where(part => part.Length > 0), StringComparer.OrdinalIgnoreCase);

    // Shown names never carry the underscores or run-together words of code names: Nearby_Or_Key reads Nearby Or Key, SunkenCrypt reads Sunken Crypt.
    internal static string Pretty(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }
        var text = name.Replace('_', ' ');
        var spaced = new System.Text.StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0 && char.IsUpper(c) && text[i - 1] != ' ' && (char.IsLower(text[i - 1]) || i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1])))
            {
                spaced.Append(' ');
            }
            spaced.Append(c);
        }
        return spaced.ToString().Trim();
    }

    // A setting's key stays as it is in the settings file, since renaming one would drop the value players saved under it; the page shows it as a label: Title Case with minor words lowercase up to five words, and sentence case from six, where only the names of mods and of other settings keep their capitals.
    private static readonly Dictionary<string, string> LongNames = new()
    {
        ["Back Up Character On New Servers"] = "Back up character on new servers",
        ["Hide HUD Key Hints With Inventory Open"] = "Hide HUD key hints with inventory open",
        ["Input Tweaks Ignores World Behind Panels"] = "Input Tweaks ignores world behind panels",
        ["Leave Split Wheel To Input Tweaks"] = "Leave split wheel to Input Tweaks",
        ["Make Room For Terrain Tools Panel"] = "Make room for Terrain Tools panel",
        ["Multi-Select Key Turns On Select Several Items"] = "Multi-Select Key turns on Select several items",
        ["Own Settings For Unstated Server Rules"] = "Own settings for unstated server rules",
        ["Split Arrow Keys Step By 1"] = "Split arrow keys step by 1",
        ["Terrain Tools Brush Costs In Build Panel"] = "Terrain Tools brush costs in build panel",
        ["Use SeneaL UI Unlocker Layout At First"] = "Use SeneaL UI Unlocker layout at first",
    };

    private static readonly HashSet<string> MinorWords = ["a", "an", "the", "of", "to", "for", "with", "and", "or", "in", "on", "at", "by", "from", "as", "per"];

    internal static string Label(string key)
    {
        if (LongNames.TryGetValue(key, out var name))
        {
            return name;
        }
        var words = key.Split(' ');
        var last = words.Length - 1;
        while (last > 0 && words[last].StartsWith("("))
        {
            last--;
        }
        for (var i = 1; i < last; i++)
        {
            if (MinorWords.Contains(words[i].ToLowerInvariant()))
            {
                words[i] = words[i].ToLowerInvariant();
            }
        }
        return string.Join(" ", words);
    }

    internal static string Display(ConfigEntryBase entry, object value) => value switch
    {
        null => "",
        KeyboardShortcut key => key.MainKey == KeyCode.None ? "None" : SeneaLUI.Plugin.KeyName(key),
        KeyCode code => code == KeyCode.None ? "None" : SeneaLUI.Plugin.KeyName(new KeyboardShortcut(code)),
        bool on => on ? "On" : "Off",
        float number => number.ToString("0.##", CultureInfo.InvariantCulture),
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        Enum choice => Pretty(choice.ToString()),
        Color color => "#" + ColorUtility.ToHtmlStringRGBA(color),
        string text => text.Length == 0 ? "empty" : Pretty(text),
        _ => Pretty(TomlTypeConverter.ConvertToString(value, entry.SettingType))
    };

    internal sealed class Block
    {
        public readonly string Title;
        public readonly RectTransform Head;
        public readonly List<Row> Rows = [];
        public bool Open;
        private readonly Image _chev;

        public Block(SettingsPage page, string title, bool open)
        {
            Title = title;
            Open = open;
            var w = page._rowW;
            Head = Ui.TL(page._scroll.Content, "h." + title, 12f, 0f, w, HeadH);
            Ui.Img(Head, null, Palette.Clear).raycastTarget = true;
            var style = TextStyle.SemiBold(13.5f, Palette.Brass, 3f);
            var label = Ui.Label(Head, "t", style, 0f, 26f, 30f - style.Size * 1.3f + style.Baseline(1.3f), w - 30f, TextAlignmentOptions.TopLeft);
            label.fontStyle = FontStyles.UpperCase;
            label.text = title;
            label.raycastTarget = false;
            Ui.Img(Ui.TL(Head, "rule", 0f, 37f, w * 0.5f, 1f), Skin.HGradient(128, Palette.Brass75, Palette.Clear), Color.white).raycastTarget = false;
            _chev = Ui.Icon(Head, "c", Icons.Get("chevr"), Palette.Brass, 0f, 1f, 10f, -21f, 14f);
            _chev.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _chev.preserveAspect = true;
            _chev.raycastTarget = false;
            var input = Head.gameObject.AddComponent<UIInputHandler>();
            input.m_onLeftClick = _ =>
            {
                Click.Play();
                page.Toggle(this);
            };
            input.m_onPointerEnter = _ => _chev.color = Palette.Gold;
            input.m_onPointerExit = _ => _chev.color = Palette.Brass;
        }

        public void Paint(bool open) => _chev.rectTransform.localRotation = Quaternion.Euler(0f, 0f, open ? -90f : 0f);
    }

    internal abstract class Row
    {
        protected readonly SettingsPage Page;
        public readonly RectTransform Root;
        public float Height = MinRowH;
        public string Search = "";

        protected Row(SettingsPage page, Block block, string name)
        {
            Page = page;
            Root = Ui.TL(page._scroll.Content, name, 12f, 0f, page._rowW, MinRowH);
        }

        protected float RowW => Page._rowW;

        protected float EditorX => RowW - ResetW - 8f - EditorW;

        public void Show(bool on)
        {
            if (Root.gameObject.activeSelf != on)
            {
                Root.gameObject.SetActive(on);
                if (!on)
                {
                    Close();
                }
            }
        }

        public virtual void Tick(Component owner)
        {
        }

        // Returns true when the row's height changed and the page must be laid out again.
        public virtual bool Sync() => false;

        public virtual void Close()
        {
        }
    }

    internal sealed class SubHead : Row
    {
        public readonly string Text;

        public SubHead(SettingsPage page, Block block, string text) : base(page, block, "s." + text)
        {
            Text = text;
            Height = 30f;
            Root.sizeDelta = new Vector2(RowW, Height);
            var style = TextStyle.SemiBold(15f, Palette.Gold);
            var label = Ui.Label(Root, "t", style, 0f, 0f, 8f + style.Baseline(1.3f), RowW, TextAlignmentOptions.TopLeft);
            label.text = Pretty(text);
            Search = text.ToLowerInvariant();
        }
    }

    // A labeled setting in SeneaL UI's own row look: name, description with the default, the control at the right and a reset button.
    internal abstract class EntryRow : Row
    {
        protected readonly ConfigEntryBase Entry;
        protected readonly float Mid;
        private readonly RectTransform _reset;

        protected EntryRow(SettingsPage page, Block block, ConfigEntryBase entry, string label = null) : base(page, block, "r." + entry.Definition.Key)
        {
            Entry = entry;
            var band = Ui.Img(Ui.Stretch(Root, "hl"), null, Palette.Clear);
            band.rectTransform.offsetMin = new Vector2(-12f, 1f);
            band.rectTransform.offsetMax = new Vector2(12f, -1f);
            band.raycastTarget = true;
            var hover = Root.gameObject.AddComponent<UIInputHandler>();
            hover.m_onPointerEnter = _ => band.color = Palette.Rgba(217, 183, 109, 0.06f);
            hover.m_onPointerExit = _ => band.color = Palette.Clear;
            var textW = RowW - EditorW - ResetW - 24f;
            var nameStyle = TextStyle.Medium(20f, Palette.Parchment);
            var name = Ui.Label(Root, "l", nameStyle, 0f, 0f, 10f + nameStyle.Baseline(1.3f), textW, TextAlignmentOptions.TopLeft);
            name.textWrappingMode = TextWrappingModes.Normal;
            name.text = label ?? Label(entry.Definition.Key);
            var nameH = Ui.Measure(name, name.text, textW, 0f).y;
            name.rectTransform.sizeDelta = new Vector2(textW, nameH + 4f);
            var desc = (entry.Description?.Description ?? "").Trim();
            var full = (desc.Length > 0 ? desc + "  " : "") + "Default: " + Display(entry, entry.DefaultValue);
            var descStyle = TextStyle.Body(14.5f, Palette.Ash);
            var descLabel = Ui.Label(Root, "d", descStyle, 0f, 0f, 10f + nameH + 3f + descStyle.Baseline(1.3f), textW, TextAlignmentOptions.TopLeft);
            descLabel.textWrappingMode = TextWrappingModes.Normal;
            descLabel.color = Palette.Rgba(169, 156, 134, 0.72f);
            descLabel.text = full;
            var descH = Ui.Measure(descLabel, full, textW, 0f).y;
            descLabel.rectTransform.sizeDelta = new Vector2(textW, descH + 4f);
            Height = Mathf.Max(MinRowH, Mathf.Ceil(10f + nameH + 3f + descH + 10f));
            Root.sizeDelta = new Vector2(RowW, Height);
            Mid = Mathf.Min(Height * 0.5f, 10f + nameStyle.Size * 0.65f + 12f);
            Search = (entry.Definition.Section + " " + name.text + " " + desc).ToLowerInvariant();
            _reset = Ui.TL(Root, "rs", RowW - ResetW, Mid - 13f, ResetW, ResetW);
            var icon = Ui.Icon(_reset, "i", Icons.Get("reset"), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 16f);
            icon.preserveAspect = true;
            Ui.Img(_reset, null, Palette.Clear).raycastTarget = true;
            var input = _reset.gameObject.AddComponent<UIInputHandler>();
            input.m_onPointerEnter = _ => icon.color = Palette.Gold;
            input.m_onPointerExit = _ => icon.color = Palette.Brass;
            input.m_onLeftClick = _ =>
            {
                Click.Play();
                Entry.BoxedValue = Entry.DefaultValue;
                Sync();
            };
        }

        protected void HideReset() => _reset.gameObject.SetActive(false);
    }

    internal sealed class SwitchRow : EntryRow
    {
        private readonly SwitchBox _switch;
        private bool _setting;

        public SwitchRow(SettingsPage page, Block block, ConfigEntryBase entry) : base(page, block, entry)
        {
            _switch = new SwitchBox(Root, "sw", on =>
            {
                if (!_setting)
                {
                    Entry.BoxedValue = on;
                }
            });
            Ui.Place(_switch.Root, 0f, 1f, EditorX + EditorW - 40f, -(Mid - 11f), 40f, 22f);
        }

        public override void Tick(Component owner) => _switch.Tick();

        public override bool Sync()
        {
            _setting = true;
            _switch.Set((bool)Entry.BoxedValue);
            _setting = false;
            return false;
        }
    }

    internal sealed class ChoiceRow : EntryRow
    {
        private readonly IChoice _select;
        private readonly SearchBox _other;
        private object[] _values = [];
        private bool _typing;
        private bool _focused;

        public ChoiceRow(SettingsPage page, Block block, ConfigEntryBase entry) : base(page, block, entry)
        {
            _select = Choice.Create(Root, EditorX, Mid - 17f, EditorW, page._host);
            _select.OnPick = i =>
            {
                if (i < 0 || i >= _values.Length)
                {
                    return;
                }
                if (OtherChoices.Is(Entry, _values[i]))
                {
                    Type(true);
                    return;
                }
                Entry.BoxedValue = _values[i];
                Sync();
            };
            if (OtherChoices.Has(entry))
            {
                _other = new SearchBox(Root, "o", EditorW, null);
                _other.Root.anchoredPosition = new Vector2(EditorX, -(Mid - 16f));
                _other.Submitted += Submit;
                _other.Root.gameObject.SetActive(false);
            }
        }

        private void Type(bool on)
        {
            _typing = on;
            _other.Root.gameObject.SetActive(on);
            _select.Root.gameObject.SetActive(!on);
            _other.SetText("");
            if (on)
            {
                _other.Focus();
            }
            _focused = on;
        }

        private void Submit(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                OtherChoices.Typed(Entry, text);
            }
            Type(false);
            Sync();
        }

        private object[] Values()
        {
            var acceptable = Entry.Description?.AcceptableValues;
            if (acceptable != null && AccessTools.Property(acceptable.GetType(), "AcceptableValues")?.GetValue(acceptable) is Array list)
            {
                return list.Cast<object>().ToArray();
            }
            return Entry.SettingType.IsEnum ? Enum.GetValues(Entry.SettingType).Cast<object>().ToArray() : [];
        }

        public override void Tick(Component owner)
        {
            if (!_typing)
            {
                _select.Tick(owner);
                return;
            }
            _other.Tick(dismiss: true);
            if (_focused && !_other.Focused)
            {
                Submit(_other.Text);
            }
            _focused = _other.Focused;
        }

        public override bool Sync()
        {
            _values = Values();
            var names = _values.Select(value => Display(Entry, value)).ToArray();
            var current = Array.FindIndex(_values, value => Equals(value, Entry.BoxedValue));
            _select.BindNames(names, current);
            _select.SetCurrent(Display(Entry, Entry.BoxedValue));
            return false;
        }

        public override void Close()
        {
            if (_typing)
            {
                Type(false);
            }
            _select.Close();
        }
    }

    internal sealed class RangeRow : EntryRow
    {
        private readonly SliderBar _slider;
        private readonly SearchBox _box;
        private readonly float _min;
        private readonly float _max;
        private readonly bool _whole;
        private bool _setting;

        public RangeRow(SettingsPage page, Block block, ConfigEntryBase entry) : base(page, block, entry)
        {
            var range = entry.Description.AcceptableValues;
            _min = Convert.ToSingle(AccessTools.Property(range.GetType(), "MinValue").GetValue(range), CultureInfo.InvariantCulture);
            _max = Convert.ToSingle(AccessTools.Property(range.GetType(), "MaxValue").GetValue(range), CultureInfo.InvariantCulture);
            var type = entry.SettingType;
            _whole = type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte);
            const float BoxW = 80f;
            var eyeW = AreaPreview.IsArea(entry) ? 32f : 0f;
            _slider = new SliderBar(Root, "sl", EditorW - BoxW - 10f - eyeW, _min, _max, value =>
            {
                if (!_setting)
                {
                    Write(value);
                }
            });
            Ui.Place(_slider.Root, 0f, 1f, EditorX + eyeW, -(Mid - 11f), EditorW - BoxW - 10f - eyeW, 22f);
            _box = new SearchBox(Root, "v", BoxW, null);
            _box.Root.anchoredPosition = new Vector2(EditorX + EditorW - BoxW, -(Mid - 16f));
            _box.SetLimit(10);
            _box.Submitted += text => Commit(text);
            if (AreaPreview.IsArea(entry))
            {
                _eye = Ui.TL(Root, "eye", EditorX, Mid - 13f, 26f, 26f);
                _eyeIcon = Ui.Icon(_eye, "i", Icons.Get("eye"), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 16f);
                _eyeIcon.preserveAspect = true;
                Ui.Img(_eye, null, Palette.Clear).raycastTarget = true;
                var input = _eye.gameObject.AddComponent<UIInputHandler>();
                input.m_onLeftClick = _ =>
                {
                    Click.Play();
                    AreaPreview.Toggle(Entry);
                };
            }
        }

        private readonly RectTransform _eye;
        private readonly Image _eyeIcon;

        private void Write(float value)
        {
            value = Mathf.Clamp(value, _min, _max);
            Entry.BoxedValue = Convert.ChangeType(_whole ? Mathf.Round(value) : value, Entry.SettingType, CultureInfo.InvariantCulture);
            Sync();
        }

        private void Commit(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                Write(value);
            }
            else
            {
                Sync();
            }
        }

        private bool _focused;

        public override void Tick(Component owner)
        {
            _box.Tick(dismiss: true);
            if (_focused && !_box.Focused)
            {
                Commit(_box.Text);
            }
            _focused = _box.Focused;
            if (_eyeIcon != null)
            {
                _eyeIcon.color = AreaPreview.Showing(Entry) ? Palette.Gold : Palette.Brass;
            }
        }

        public override bool Sync()
        {
            var value = Convert.ToSingle(Entry.BoxedValue, CultureInfo.InvariantCulture);
            _setting = true;
            _slider.Set(value);
            _setting = false;
            if (!_box.Focused)
            {
                _box.SetText(Display(Entry, Entry.BoxedValue));
            }
            return false;
        }
    }

    internal sealed class TextRow : EntryRow
    {
        private readonly SearchBox _box;
        private bool _focused;

        public TextRow(SettingsPage page, Block block, ConfigEntryBase entry) : base(page, block, entry)
        {
            _box = new SearchBox(Root, "f", EditorW, null);
            _box.Root.anchoredPosition = new Vector2(EditorX, -(Mid - 16f));
            _box.Submitted += Commit;
        }

        private void Commit(string text)
        {
            try
            {
                Entry.BoxedValue = TomlTypeConverter.ConvertToValue(text ?? "", Entry.SettingType);
            }
            catch
            {
            }
            Sync();
        }

        public override void Tick(Component owner)
        {
            _box.Tick(dismiss: true);
            if (_focused && !_box.Focused)
            {
                Commit(_box.Text);
            }
            _focused = _box.Focused;
        }

        public override bool Sync()
        {
            if (!_box.Focused)
            {
                _box.SetText(TomlTypeConverter.ConvertToString(Entry.BoxedValue, Entry.SettingType));
            }
            return false;
        }
    }

    internal sealed class ColorRow : EntryRow
    {
        private readonly SearchBox _box;
        private readonly Image _swatch;
        private bool _focused;

        public ColorRow(SettingsPage page, Block block, ConfigEntryBase entry) : base(page, block, entry)
        {
            // The key cap drawing is dark, so a color tinted onto it shows only as a darker cap: the color fills a plain square inside the cap instead.
            var frame = Ui.Img(Ui.TL(Root, "sw", EditorX, Mid - 13f, 26f, 26f), Skin.Sliced(26, Styles.Key, Styles.Key.Pad), Color.white);
            frame.type = Image.Type.Sliced;
            var fill = Ui.Stretch(frame.rectTransform, "c");
            fill.offsetMin = new Vector2(4f, 4f);
            fill.offsetMax = new Vector2(-4f, -4f);
            _swatch = Ui.Img(fill, null, Color.white);
            _box = new SearchBox(Root, "f", EditorW - 36f, null);
            _box.Root.anchoredPosition = new Vector2(EditorX + 36f, -(Mid - 16f));
            _box.SetLimit(9);
            _box.Submitted += Commit;
        }

        private void Commit(string text)
        {
            var hex = (text ?? "").Trim();
            if (ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out var color))
            {
                Entry.BoxedValue = color;
            }
            Sync();
        }

        public override void Tick(Component owner)
        {
            _box.Tick(dismiss: true);
            if (_focused && !_box.Focused)
            {
                Commit(_box.Text);
            }
            _focused = _box.Focused;
        }

        public override bool Sync()
        {
            var color = (Color)Entry.BoxedValue;
            _swatch.color = color;
            if (!_box.Focused)
            {
                _box.SetText("#" + ColorUtility.ToHtmlStringRGBA(color));
            }
            return false;
        }
    }

    internal sealed class KeyRow : EntryRow
    {
        private readonly TextMeshProUGUI _text;

        public KeyRow(SettingsPage page, Block block, ConfigEntryBase entry) : base(page, block, entry)
        {
            var box = Ui.TL(Root, "key", EditorX, Mid - 16f, EditorW, 32f);
            Ui.Img(Ui.Stretch(box, "bg"), Skin.Sliced(32, Styles.Key, Styles.Key.Pad), Color.white).type = Image.Type.Sliced;
            var style = TextStyle.Medium(16.5f, Palette.Parchment);
            _text = Ui.Label(box, "t", style, 0.5f, 0f, (32f - style.Size * 1.3f) * 0.5f + style.Baseline(1.3f), EditorW - 20f, TextAlignmentOptions.Top);
            Ui.Img(box, null, Palette.Clear).raycastTarget = true;
            box.gameObject.AddComponent<UIInputHandler>().m_onLeftClick = _ =>
            {
                Click.Play();
                page.BeginCapture(this);
            };
        }

        public void Paint(bool capturing)
        {
            _text.text = capturing ? "Press a key or a combination; Backspace clears it" : Display(Entry, Entry.BoxedValue);
            _text.color = capturing ? Palette.Gold : Palette.Parchment;
        }

        public void Set(KeyboardShortcut key)
        {
            Entry.BoxedValue = Entry.SettingType == typeof(KeyCode) ? key.MainKey : key;
            Paint(false);
        }

        public override bool Sync()
        {
            if (Page._capture != this)
            {
                Paint(false);
            }
            return false;
        }

        public override void Close()
        {
            if (Page._capture == this)
            {
                Page._capture = null;
                Paint(false);
            }
        }
    }

    // A row whose control sits under its label at full width, for lists that need the room.
    internal abstract class WideRow : Row
    {
        protected readonly ConfigEntryBase Entry;
        protected readonly RectTransform Body;
        private readonly float _top;

        protected WideRow(SettingsPage page, Block block, ConfigEntryBase entry, string label, string description) : base(page, block, "w." + entry.Definition.Key)
        {
            Entry = entry;
            var nameStyle = TextStyle.Medium(20f, Palette.Parchment);
            var name = Ui.Label(Root, "l", nameStyle, 0f, 0f, 10f + nameStyle.Baseline(1.3f), RowW, TextAlignmentOptions.TopLeft);
            name.text = label;
            var descStyle = TextStyle.Body(14.5f, Palette.Ash);
            var desc = Ui.Label(Root, "d", descStyle, 0f, 0f, 10f + nameStyle.Size * 1.3f + 3f + descStyle.Baseline(1.3f), RowW, TextAlignmentOptions.TopLeft);
            desc.textWrappingMode = TextWrappingModes.Normal;
            desc.color = Palette.Rgba(169, 156, 134, 0.72f);
            desc.text = description;
            var descH = Ui.Measure(desc, description, RowW, 0f).y;
            desc.rectTransform.sizeDelta = new Vector2(RowW, descH + 4f);
            _top = Mathf.Ceil(10f + nameStyle.Size * 1.3f + 3f + descH + 10f);
            Body = Ui.TL(Root, "body", 0f, _top, RowW, 10f);
            Search = (entry.Definition.Section + " " + label + " " + description).ToLowerInvariant();
        }

        protected void Fit(float bodyH)
        {
            Body.sizeDelta = new Vector2(RowW, bodyH);
            Height = _top + bodyH + 12f;
            Root.sizeDelta = new Vector2(RowW, Height);
        }
    }

    internal RectTransform PopHost => _host;

    // An open dropdown under the cursor takes the mouse wheel alone: the page under it does not scroll with it.
    private bool OverPopup()
    {
        if (ZInput.GetMouseScrollWheel() == 0f || _host == null)
        {
            return false;
        }
        var pointer = (Vector2)ZInput.pointerPosition;
        for (var i = 0; i < _host.childCount; i++)
        {
            var child = _host.GetChild(i) as RectTransform;
            if (child != null && child.gameObject.activeInHierarchy && (child.name == "pop" || child.name == "unlocker.multi") && RectTransformUtility.RectangleContainsScreenPoint(child, pointer, null))
            {
                return true;
            }
        }
        return false;
    }

    internal float Width => _rowW;

    internal void Redraw(Action draw) => Draw(draw);

    internal void LayoutAgain() => Relayout();
}
