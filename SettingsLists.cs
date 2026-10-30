using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// The settings that hold lists, each drawn as a list a player edits directly instead of one line of text separated by semicolons.
internal sealed partial class SettingsPage
{
    private const string BuiltinPrefix = "builtin:";
    private const float LineH = 40f;

    private static readonly BoxStyle Solid = new(10f, Palette.Rgba(29, 24, 21, 1f), Palette.Rgba(19, 15, 12, 1f), Palette.BrassLine32);

    // Settings whose control is part of another setting's list.
    private static bool Folded(ConfigEntryBase entry) =>
        entry.Definition.Section == "Servers" && entry.Definition.Key is "Add Known Server" or "Add Current Server";

    private Row ListRow(Block block, ConfigEntryBase entry)
    {
        var section = entry.Definition.Section;
        var key = entry.Definition.Key;
        if (entry.SettingType != typeof(string))
        {
            return null;
        }
        return (section, key) switch
        {
            ("Auto Pins", "Resource Items") => new ToggleSetRow(this, block, entry, Lang.T("Settings.PinResources", "Pin these resources, one pin per patch."), () => Named(entry, ItemName)),
            ("Auto Pins", _) when key.EndsWith(" Locations", StringComparison.Ordinal) => new ToggleSetRow(this, block, entry, Lang.F("Settings.PinLocations", "Pin these {0} as you come near them.", key.Substring(0, key.Length - " Locations".Length).ToLowerInvariant()), () => Named(entry, Pretty)),
            ("Radar", "Radar Alert Creatures") => new ToggleSetRow(this, block, entry, Lang.T("Settings.RadarInclude", "Radar Alerts always include these creatures."), () => Shown(entry, Creatures())),
            ("Radar", "Radar Ignored Creatures") => new ToggleSetRow(this, block, entry, Lang.T("Settings.RadarIgnore", "SeneaL Radar never draws these creatures on the minimap or the world map."), () => Shown(entry, Creatures())),
            ("Reminders", "Ignored Effects") => new ToggleSetRow(this, block, entry, Lang.T("Settings.IgnoreEffects", "Buff Reminder never warns about these effects."), () => Shown(entry, Effects())),
            ("Combat HUD", "Combat Only Elements") => new MultiChoiceRow(this, block, entry),
            ("Servers", "Listed Servers") => new ServersRow(this, block, entry),
            ("Interface", "Server Interface Presets") => new PresetsRow(this, block, entry, "Server", Lang.T("Settings.AddressOrCode", "Address (host:port) or join code")),
            ("Interface", "World Interface Presets") => new PresetsRow(this, block, entry, "World", Lang.T("Settings.WorldName", "World name")),
            ("Inventory", "Restock Targets") => new RestockRow(this, block, entry),
            ("Food", "Ignored Foods") => new ToggleSetRow(this, block, entry, Lang.T("Settings.IgnoreFoods", "The Food window leaves these foods out."), () => FoodPlanner.All().Concat(Split((string)entry.BoxedValue).Select(name => (name, Pretty(name)))).GroupBy(item => item.Item1, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase).ToList()),
            ("Map", "Kinds Kept Off Cartography Tables") => new ToggleSetRow(this, block, entry, Lang.T("Settings.KeepOffTables", "These kinds of auto-pins stay on your own map and are not written to a cartography table."), () => PinKinds.Names.Select(name => (name, name)).ToList()),
            ("Auto Pickup", "Never Auto Pickup") => new ToggleSetRow(this, block, entry, Lang.T("Settings.NeverPickup", "Auto-pickup leaves these items on the ground; you can still pick them up by hand."), () => ItemsByName(entry)),
            _ => null
        };
    }

    private ConfigEntry<T> Setting<T>(string section, string key) => SeneaLConfig.Entry(_cfg, section, key) as ConfigEntry<T>;

    // Names stored as internal names (location and item prefabs): the defaults and whatever is set, each shown by a readable name.
    private static List<(string Value, string Label)> Named(ConfigEntryBase entry, Func<string, string> label) =>
        Split((string)entry.DefaultValue).Concat(Split((string)entry.BoxedValue)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (name, label(name))).ToList();

    // Names stored as the game shows them (creatures, effects): every one the game knows, and whatever is set.
    private static List<(string Value, string Label)> Shown(ConfigEntryBase entry, IEnumerable<string> known) =>
        known.Concat(Split((string)entry.DefaultValue)).Concat(Split((string)entry.BoxedValue)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => (name, name)).ToList();

    private static string ItemName(string prefab)
    {
        var drop = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>();
        return drop == null || Localization.instance == null ? Pretty(prefab) : Localization.instance.Localize(drop.m_itemData.m_shared.m_name);
    }

    private static IEnumerable<string> Creatures()
    {
        if (ZNetScene.instance == null || Localization.instance == null)
        {
            return [];
        }
        return ZNetScene.instance.m_prefabs.Where(prefab => prefab != null)
            .Select(prefab => prefab.GetComponent<Character>())
            .Where(character => character != null && character is not Player && !string.IsNullOrEmpty(character.m_name))
            .Select(character => Localization.instance.Localize(character.m_name))
            .Where(name => name.Length > 0 && !name.StartsWith("[", StringComparison.Ordinal) && !name.StartsWith("$", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Effects()
    {
        if (ObjectDB.instance == null || Localization.instance == null)
        {
            return [];
        }
        return ObjectDB.instance.m_StatusEffects.Where(effect => effect != null && !string.IsNullOrEmpty(effect.m_name))
            .Select(effect => Localization.instance.Localize(effect.m_name))
            .Where(name => name.Length > 0 && !name.StartsWith("[", StringComparison.Ordinal) && !name.StartsWith("$", StringComparison.Ordinal));
    }

    // Every item the game has, stored by prefab name and shown by the name the game shows, with any already chosen.
    // Every item once by the name the game shows, stored as its name token ($item_bow), since the game keeps many prefabs under one name (variants, upgrades, other mods' copies).
    private static List<(string Value, string Label)> ItemsByName(ConfigEntryBase entry)
    {
        var list = new List<(string Value, string Label)>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ObjectDB.instance != null && Localization.instance != null)
        {
            foreach (var prefab in ObjectDB.instance.m_items)
            {
                var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
                var token = drop == null ? null : drop.m_itemData.m_shared.m_name;
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }
                var name = Localization.instance.Localize(token);
                if (name.Length > 0 && !name.StartsWith("[", StringComparison.Ordinal) && !name.StartsWith("$", StringComparison.Ordinal) && labels.Add(name))
                {
                    list.Add((token, name));
                }
            }
        }
        foreach (var chosen in Split((string)entry.BoxedValue).Where(value => list.All(item => !item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))))
        {
            var label = chosen.StartsWith("$", StringComparison.Ordinal) && Localization.instance != null ? Localization.instance.Localize(chosen) : ItemName(chosen);
            if (labels.Add(label))
            {
                list.Add((chosen, label));
            }
        }
        return list.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ItemNames()
    {
        if (ObjectDB.instance == null || Localization.instance == null)
        {
            return [];
        }
        return ObjectDB.instance.m_items.Where(prefab => prefab != null).Select(prefab => prefab.GetComponent<ItemDrop>())
            .Where(drop => drop != null && !string.IsNullOrEmpty(drop.m_itemData.m_shared.m_name))
            .Select(drop => Localization.instance.Localize(drop.m_itemData.m_shared.m_name))
            .Where(name => name.Length > 0 && !name.StartsWith("[", StringComparison.Ordinal) && !name.StartsWith("$", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly bool HasPresets = TypeLookup.Find("SeneaLUI.Core.Profiles") != null;

    // SeneaL UI's packaged interface presets and the player's own, as (stored value, shown name); a packaged one is stored with a builtin: prefix. SeneaL UI 1.1.x has no interface presets, so the list is empty there.
    internal static List<(string Value, string Label)> Presets() => HasPresets ? PresetList() : [];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<(string Value, string Label)> PresetList() =>
        Profiles.PresetIds.Select(id => (BuiltinPrefix + id, Pretty(char.ToUpperInvariant(id[0]) + id.Substring(1)) + " (SeneaL UI)"))
            .Concat(Profiles.Saved().Select(name => (name, name))).ToList();

    private static Dictionary<string, string> PairMap(string text, char separator)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? "").Split(';'))
        {
            var at = part.LastIndexOf(separator);
            if (at > 0)
            {
                map[part.Substring(0, at).Trim()] = part.Substring(at + 1).Trim();
            }
        }
        return map;
    }

    private static void Remove(RectTransform parent, float x, float y, Action click)
    {
        var button = Ui.TL(parent, "x", x, y, 26f, 26f);
        var icon = Ui.Icon(button, "i", Icons.Get("x"), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 14f);
        icon.preserveAspect = true;
        Ui.Img(button, null, Palette.Clear).raycastTarget = true;
        var input = button.gameObject.AddComponent<UIInputHandler>();
        input.m_onPointerEnter = _ => icon.color = Palette.Gold;
        input.m_onPointerExit = _ => icon.color = Palette.Brass;
        input.m_onLeftClick = _ =>
        {
            Click.Play();
            click();
        };
    }

    private static TextMeshProUGUI Caption(RectTransform parent, string text, float x, float y, float w, TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        var style = TextStyle.SemiBold(12.5f, Palette.Brass, 1.5f);
        var label = Ui.Label(parent, "c", style, 0f, x, y + style.Baseline(1.3f), w, align);
        label.text = text;
        label.raycastTarget = false;
        return label;
    }

    // A text box that commits when Enter is pressed or when it loses focus.
    private sealed class Field
    {
        public readonly SearchBox Box;
        private bool _focused;

        public Field(RectTransform parent, float x, float y, float w, string placeholder, Action<string> commit)
        {
            Box = new SearchBox(parent, "f", w, null);
            Box.Root.anchoredPosition = new Vector2(x, -y);
            if (placeholder != null)
            {
                Box.SetPlaceholder(placeholder);
            }
            Commit = commit;
            Box.Submitted += text => Commit(text);
        }

        private Action<string> Commit { get; }

        public void Tick()
        {
            Box.Tick(dismiss: true);
            if (_focused && !Box.Focused)
            {
                Commit(Box.Text);
            }
            _focused = Box.Focused;
        }

        public void Show(string text)
        {
            if (!Box.Focused && Box.Text != text)
            {
                Box.SetText(text);
            }
        }
    }

    // One switch per name, in two columns, for a setting that stores names separated by semicolons.
    internal sealed class ToggleSetRow : WideRow
    {
        private const float CellH = 30f;
        private readonly Func<List<(string Value, string Label)>> _universe;
        private readonly List<SwitchBox> _switches = [];
        private List<(string Value, string Label)> _items = [];
        private RectTransform _grid;
        private bool _setting;

        public ToggleSetRow(SettingsPage page, Block block, ConfigEntryBase entry, string description, Func<List<(string Value, string Label)>> universe)
            : base(page, block, entry, Label(entry.Definition.Key), description)
        {
            _universe = universe;
        }

        private void Rebuild()
        {
            if (_grid != null)
            {
                UnityEngine.Object.Destroy(_grid.gameObject);
            }
            _switches.Clear();
            Page.Redraw(() =>
            {
                _grid = Ui.TL(Body, "grid", 0f, 0f, RowW, 10f);
                var colW = (RowW - 12f) * 0.5f;
                for (var i = 0; i < _items.Count; i++)
                {
                    var value = _items[i].Value;
                    var cell = Ui.TL(_grid, "c" + i, i % 2 * (colW + 12f), i / 2 * CellH, colW, CellH);
                    var box = new SwitchBox(cell, "sw", on =>
                    {
                        if (!_setting)
                        {
                            Write(value, on);
                        }
                    });
                    Ui.Place(box.Root, 0f, 1f, 0f, -4f, 40f, 22f);
                    var style = TextStyle.Medium(16f, Palette.Linen);
                    var label = Ui.Label(cell, "l", style, 0f, 50f, (CellH - style.Size * 1.3f) * 0.5f + style.Baseline(1.3f), colW - 54f, TextAlignmentOptions.TopLeft);
                    label.overflowMode = TextOverflowModes.Ellipsis;
                    label.text = Label(_items[i].Label);
                    _switches.Add(box);
                }
            });
            var rows = (_items.Count + 1) / 2;
            _grid.sizeDelta = new Vector2(RowW, rows * CellH);
            Fit(Mathf.Max(rows * CellH, CellH));
            Search = Search.Split('\n')[0] + "\n" + string.Join(" ", _items.Select(item => item.Label)).ToLowerInvariant();
        }

        private void Write(string value, bool on)
        {
            var set = Split((string)Entry.BoxedValue);
            if (on)
            {
                set.Add(value);
            }
            else
            {
                set.Remove(value);
            }
            var ordered = _items.Select(item => item.Value).Where(set.Contains).Concat(set.Where(name => _items.All(item => !item.Value.Equals(name, StringComparison.OrdinalIgnoreCase))));
            Entry.BoxedValue = string.Join("; ", ordered);
        }

        public override void Tick(Component owner)
        {
            foreach (var box in _switches)
            {
                box.Tick();
            }
        }

        public override bool Sync()
        {
            var items = _universe();
            var changed = items.Count != _items.Count || items.Where((item, i) => item.Value != _items[i].Value || item.Label != _items[i].Label).Any();
            if (changed)
            {
                _items = items;
                Rebuild();
            }
            var set = Split((string)Entry.BoxedValue);
            _setting = true;
            for (var i = 0; i < _switches.Count; i++)
            {
                _switches[i].Set(set.Contains(_items[i].Value));
            }
            _setting = false;
            return changed;
        }
    }

    // A dropdown that keeps several choices: it stays open while choices are clicked and closes on its X, a click outside it or Escape.
    internal sealed class MultiChoiceRow : EntryRow
    {
        private const float PopRowH = 30f;
        private readonly RectTransform _box;
        private readonly TextMeshProUGUI _label;
        private readonly Image _chev;
        private RectTransform _pop;
        private ScrollBox _list;
        private readonly List<(string Value, Image Check, TextMeshProUGUI Text)> _rows = [];
        private List<(string Value, string Label)> _items = [];
        private bool _open;

        public MultiChoiceRow(SettingsPage page, Block block, ConfigEntryBase entry) : base(page, block, entry)
        {
            _box = Ui.TL(Root, "select", EditorX, Mid - 17f, EditorW, 34f);
            Ui.Skinned(_box, Skin.Sliced(34, Styles.Search), Styles.Search.Pad, sliced: true);
            Ui.Img(_box, null, Palette.Clear).raycastTarget = true;
            var style = TextStyle.SemiBold(15f, Palette.Parchment);
            _label = Ui.Label(_box, "t", style, 0f, 14f, (34f - style.Size * 1.3f) * 0.5f + style.Baseline(1.3f), EditorW - 50f, TextAlignmentOptions.TopLeft);
            _label.overflowMode = TextOverflowModes.Ellipsis;
            _chev = Ui.Icon(_box, "c", Icons.Get("down"), Palette.Ash, 1f, 0.5f, -21f, 0f, 14f);
            _chev.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _chev.preserveAspect = true;
            _box.gameObject.AddComponent<UIInputHandler>().m_onLeftClick = _ =>
            {
                Click.Play();
                if (_open)
                {
                    Close();
                }
                else
                {
                    Open();
                }
            };
        }

        // SeneaL UI's HUD elements by the names its HUD editor shows, stored by their keys.
        private static readonly bool HasEditor = TypeLookup.Find("SeneaLUI.Hud.HudEditor") != null;

        // The HUD editor's own names for its elements; SeneaL UI 1.1.x has no HUD editor, so there the elements keep their setting names.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void EditorNames(Dictionary<string, string> names)
        {
            var editor = HudEditor._i;
            if (editor != null && editor._targets != null)
            {
                foreach (var target in editor._targets)
                {
                    if (target?.E?.Key != null && !names.ContainsKey(target.E.Key))
                    {
                        names[target.E.Key] = target.Name;
                    }
                }
            }
        }

        private static List<(string Value, string Label)> Elements(ConfigEntryBase entry)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (HasEditor)
            {
                EditorNames(names);
            }
            foreach (var field in typeof(Prefs).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(field => field.FieldType == typeof(Prefs.Elem)))
            {
                if (field.GetValue(null) is Prefs.Elem elem && elem.Key != null && !names.ContainsKey(elem.Key))
                {
                    names[elem.Key] = Pretty(elem.Key);
                }
            }
            foreach (var key in Split((string)entry.BoxedValue).Where(key => !names.ContainsKey(key)))
            {
                names[key] = Pretty(key);
            }
            return names.Select(pair => (pair.Key, pair.Value)).OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void Open()
        {
            _items = Elements(Entry);
            if (_pop == null)
            {
                Page.Redraw(Build);
            }
            Fill();
            _pop.gameObject.SetActive(true);
            _pop.SetAsLastSibling();
            _open = true;
            _chev.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 180f);
            Dock();
        }

        private void Build()
        {
            _pop = Ui.TL(Page.PopHost, "unlocker.multi", 0f, 0f, EditorW, 100f);
            Ui.Panel(_pop, Skin.Sliced(48, Solid), Solid, solid: true);
            Caption(_pop, Lang.T("Settings.ChooseAnyNumber", "Choose Any Number"), 12f, 12f, EditorW - 60f);
            var close = Ui.TL(_pop, "x", EditorW - 34f, 6f, 26f, 26f);
            var icon = Ui.Icon(close, "i", Icons.Get("x"), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 14f);
            icon.preserveAspect = true;
            Ui.Img(close, null, Palette.Clear).raycastTarget = true;
            var input = close.gameObject.AddComponent<UIInputHandler>();
            input.m_onPointerEnter = _ => icon.color = Palette.Gold;
            input.m_onPointerExit = _ => icon.color = Palette.Brass;
            input.m_onLeftClick = _ =>
            {
                Click.Play();
                Close();
            };
            _list = new ScrollBox(_pop, "list", EditorW - 16f, 30f, 60f);
        }

        private void Fill()
        {
            foreach (var row in _rows)
            {
                UnityEngine.Object.Destroy(row.Check.transform.parent.gameObject);
            }
            _rows.Clear();
            Page.Redraw(() =>
            {
                var style = TextStyle.Medium(14f, Palette.Linen);
                for (var i = 0; i < _items.Count; i++)
                {
                    var value = _items[i].Value;
                    var row = Ui.TL(_list.Content, "r" + i, 0f, i * PopRowH, EditorW - 16f, PopRowH);
                    Ui.Img(row, null, Palette.Clear).raycastTarget = true;
                    var check = Ui.Icon(row, "k", Icons.Get("check"), Palette.Gold, 0f, 0.5f, 18f, 0f, 14f);
                    check.preserveAspect = true;
                    var text = Ui.Label(row, "t", style, 0f, 36f, (PopRowH - style.Size * 1.3f) * 0.5f + style.Baseline(1.3f), EditorW - 60f, TextAlignmentOptions.TopLeft);
                    text.text = _items[i].Label;
                    row.gameObject.AddComponent<UIInputHandler>().m_onLeftClick = _ =>
                    {
                        Click.Play();
                        var set = Split((string)Entry.BoxedValue);
                        if (!set.Remove(value))
                        {
                            set.Add(value);
                        }
                        Entry.BoxedValue = string.Join("; ", _items.Select(item => item.Value).Where(set.Contains));
                        Paint();
                    };
                    _rows.Add((value, check, text));
                }
            });
            var listH = Mathf.Clamp(_items.Count * PopRowH, PopRowH, 300f);
            _list.Place(8f, 40f, EditorW - 16f, listH, 0f);
            _list.SetContentHeight(_items.Count * PopRowH);
            _pop.sizeDelta = new Vector2(EditorW, 40f + listH + 8f);
            Paint();
        }

        private void Paint()
        {
            var set = Split((string)Entry.BoxedValue);
            foreach (var (value, check, text) in _rows)
            {
                var on = set.Contains(value);
                check.enabled = on;
                text.color = on ? Palette.Gold : Palette.Linen;
            }
            var chosen = _items.Count > 0 ? _items.Where(item => set.Contains(item.Value)).Select(item => item.Label).ToList() : set.Select(Pretty).ToList();
            _label.text = chosen.Count == 0 ? "None" : string.Join(", ", chosen);
        }

        private void Dock()
        {
            var corners = new Vector3[4];
            _box.GetWorldCorners(corners);
            var host = Page.PopHost;
            Vector2 at = host.InverseTransformPoint(corners[0]);
            var rect = host.rect;
            _pop.anchoredPosition = at - new Vector2(rect.xMin, rect.yMax) + new Vector2(0f, -4f);
        }

        public override void Tick(Component owner)
        {
            if (!_open)
            {
                return;
            }
            Dock();
            _list.Tick(owner);
            if (RawKeys.Down(KeyCode.Escape))
            {
                Close();
                return;
            }
            if (ZInput.GetMouseButtonDown(0))
            {
                Vector2 pointer = ZInput.pointerPosition;
                if (!RectTransformUtility.RectangleContainsScreenPoint(_pop, pointer, null) && !RectTransformUtility.RectangleContainsScreenPoint(_box, pointer, null))
                {
                    Close();
                }
            }
        }

        public override bool Sync()
        {
            if (_items.Count == 0)
            {
                _items = Elements(Entry);
            }
            Paint();
            return false;
        }

        public override void Close()
        {
            _open = false;
            if (_pop != null)
            {
                _pop.gameObject.SetActive(false);
            }
            _chev.rectTransform.localRotation = Quaternion.identity;
        }
    }

    // Listed Servers as lines, one per server with its own switches and interface preset, and a fresh line below the last to add another.
    internal sealed class ServersRow : WideRow
    {
        private const float SwitchCol = 104f;
        private const float PresetW = 150f;
        private readonly List<Line> _lines = [];
        private readonly IChoice _known;
        private readonly SearchBox _other;
        private readonly GoldButton _current;
        private bool _typing;
        private bool _focused;
        private Field _fresh;
        private RectTransform _area;
        private string _shown;
        private List<(string Label, string Key)> _knownServers = [];

        private sealed class Line
        {
            public RectTransform Root;
            public Field Name;
            public SwitchBox[] Switches;
            public IChoice Preset;
            public List<(string Value, string Label)> Presets;
        }

        public ServersRow(SettingsPage page, Block block, ConfigEntryBase entry)
            : base(page, block, entry, "Listed Servers", (entry.Description?.Description ?? "").Trim())
        {
            _known = Choice.Create(Body, 0f, 0f, EditorW, page.PopHost);
            _known.SetCurrent(Lang.T("Settings.AddKnownServer", "Add a Known Server"));
            _known.OnPick = i =>
            {
                if (i == _knownServers.Count)
                {
                    TypeOther(true);
                    return;
                }
                if (i >= 0 && i < _knownServers.Count)
                {
                    AddServer(_knownServers[i].Label, _knownServers[i].Key);
                    _known.SetCurrent(Lang.T("Settings.AddKnownServer", "Add a Known Server"));
                    Sync();
                    Page.LayoutAgain();
                }
            };
            _current = new GoldButton(Body, "cur", 220f, () =>
            {
                AddCurrentServer();
                Sync();
                Page.LayoutAgain();
            }, 34, dark: true, TextStyles.BtnDarkText);
            _current.SetLabel(Lang.T("Settings.AddCurrentServer", "Add the Current Server"));
            _other = new SearchBox(Body, "o", EditorW, null);
            _other.Submitted += SubmitOther;
            _other.Root.gameObject.SetActive(false);
        }

        // The known servers are only the game's Favorite and Recent lists (and QuickConnect's), so the list ends with Other, which takes a typed address or join code.
        private void TypeOther(bool on)
        {
            _typing = on;
            _other.Root.gameObject.SetActive(on);
            _known.Root.gameObject.SetActive(!on);
            _other.SetText("");
            if (on)
            {
                _other.Focus();
            }
            _focused = on;
        }

        private void SubmitOther(string text)
        {
            var typed = (text ?? "").Trim();
            if (typed.Length > 0)
            {
                AddServer(typed, typed);
            }
            TypeOther(false);
            _known.SetCurrent(Lang.T("Settings.AddKnownServer", "Add a Known Server"));
            Sync();
            Page.LayoutAgain();
        }

        private static Dictionary<string, string> PresetMap(SettingsPage page) => PairMap(page.Setting<string>("Interface", "Server Interface Presets")?.Value, '=');

        private static void SavePreset(SettingsPage page, string key, string preset)
        {
            var entry = page.Setting<string>("Interface", "Server Interface Presets");
            if (entry == null)
            {
                return;
            }
            var map = PairMap(entry.Value, '=');
            if (string.IsNullOrEmpty(preset))
            {
                map.Remove(key);
            }
            else
            {
                map[key] = preset;
            }
            entry.Value = string.Join("; ", map.Select(pair => $"{pair.Key} = {pair.Value}"));
        }

        private bool Default(string option)
        {
            var filter = Page.Setting<ServerFilter>("Servers", "Unlock On");
            return option switch
            {
                ServerDirectory.UnlockOption => filter == null || filter.Value != ServerFilter.All_Except_Listed_Servers,
                ServerDirectory.PopupOption => Page.Setting<bool>("General", "Hide No SeneaL UI Popup")?.Value ?? true,
                _ => true
            };
        }

        private void Rebuild(List<ServerDirectory.ListedServer> servers)
        {
            foreach (var line in _lines)
            {
                line.Preset.Dispose();
            }
            _lines.Clear();
            if (_area != null)
            {
                UnityEngine.Object.Destroy(_area.gameObject);
            }
            var nameW = RowW - 26f - 10f - PresetW - 10f - 3 * SwitchCol;
            string[] options = [ServerDirectory.UnlockOption, ServerDirectory.PopupOption, ServerDirectory.BeyondOption];
            Page.Redraw(() =>
            {
                _area = Ui.TL(Body, "lines", 0f, 0f, RowW, 10f);
                Caption(_area, Lang.T("Settings.ColServer", "Server"), 0f, 0f, nameW);
                Caption(_area, Lang.T("Settings.ColUnlock", "Unlock"), nameW, 0f, SwitchCol, TextAlignmentOptions.Top);
                Caption(_area, Lang.T("Settings.HidePopup", "Hide Popup"), nameW + SwitchCol, 0f, SwitchCol, TextAlignmentOptions.Top);
                Caption(_area, Lang.T("Settings.BeyondMaximums", "Beyond Maximums"), nameW + 2 * SwitchCol - 8f, 0f, SwitchCol + 16f, TextAlignmentOptions.Top);
                Caption(_area, Lang.T("Settings.InterfacePreset", "Interface Preset"), nameW + 3 * SwitchCol + 10f, 0f, PresetW);
                var presets = PresetMap(Page);
                var choices = new List<(string Value, string Label)> { ("", "None") };
                choices.AddRange(Presets());
                for (var i = 0; i < servers.Count; i++)
                {
                    var index = i;
                    var server = servers[i];
                    var y = 22f + i * LineH;
                    var line = new Line { Root = Ui.TL(_area, "l" + i, 0f, y, RowW, LineH), Presets = choices };
                    line.Name = new Field(line.Root, 0f, 4f, nameW - 10f, null, text => Rename(index, text));
                    line.Name.Show(server.Label.Length > 0 && server.Label != server.Key ? $"{server.Label} = {server.Key}" : server.Key);
                    line.Switches = new SwitchBox[options.Length];
                    for (var k = 0; k < options.Length; k++)
                    {
                        var option = options[k];
                        var box = new SwitchBox(line.Root, "s" + k, on => SetOption(index, option, on));
                        Ui.Place(box.Root, 0f, 1f, nameW + k * SwitchCol + (SwitchCol - 40f) * 0.5f, -9f, 40f, 22f);
                        box.Set(server.Options.TryGetValue(option, out var text) ? text == "1" : Default(option));
                        line.Switches[k] = box;
                    }
                    line.Preset = Choice.Create(line.Root, nameW + 3 * SwitchCol + 10f, 3f, PresetW, Page.PopHost);
                    presets.TryGetValue(server.Key, out var preset);
                    var current = Math.Max(0, choices.FindIndex(choice => string.Equals(choice.Value, preset ?? "", StringComparison.OrdinalIgnoreCase)));
                    line.Preset.BindNames(choices.Select(choice => choice.Label).ToArray(), current);
                    line.Preset.SetCurrent(choices[current].Label);
                    line.Preset.OnPick = pick =>
                    {
                        SavePreset(Page, server.Key, choices[pick].Value);
                        line.Preset.SetCurrent(choices[pick].Label);
                    };
                    Remove(line.Root, RowW - 26f, 7f, () =>
                    {
                        var all = ServerDirectory.Entries();
                        all.RemoveAt(index);
                        ServerDirectory.Save(all);
                        Sync();
                        Page.LayoutAgain();
                    });
                    _lines.Add(line);
                }
                var freshY = 22f + servers.Count * LineH;
                _fresh = new Field(_area, 0f, freshY + 4f, nameW - 10f, "host:port or join code", text =>
                {
                    var (label, key) = Parse(text);
                    if (key.Length > 0)
                    {
                        AddServer(label, key);
                        Sync();
                        Page.LayoutAgain();
                    }
                });
            });
            var areaH = 22f + (servers.Count + 1) * LineH;
            _area.sizeDelta = new Vector2(RowW, areaH);
            _known.Close();
            _known.Root.anchoredPosition = new Vector2(0f, -(areaH + 8f));
            _other.Root.anchoredPosition = new Vector2(0f, -(areaH + 9f));
            Ui.Place(_current.Root, 0f, 1f, EditorW + 16f, -(areaH + 8f), 220f, 34f);
            Fit(areaH + 8f + 34f);
        }

        private static (string Label, string Key) Parse(string text)
        {
            var value = (text ?? "").Trim();
            var eq = value.LastIndexOf('=');
            return eq < 0 ? (value, value) : (value.Substring(0, eq).Trim(), value.Substring(eq + 1).Trim());
        }

        private void Rename(int index, string text)
        {
            var all = ServerDirectory.Entries();
            if (index >= all.Count)
            {
                return;
            }
            var (label, key) = Parse(text);
            if (key.Length == 0)
            {
                all.RemoveAt(index);
            }
            else
            {
                all[index].Label = label;
                all[index].Key = key;
            }
            ServerDirectory.Save(all);
            Sync();
            Page.LayoutAgain();
        }

        private void SetOption(int index, string option, bool on)
        {
            var all = ServerDirectory.Entries();
            if (index >= all.Count)
            {
                return;
            }
            all[index].Options[option] = on ? "1" : "0";
            ServerDirectory.Save(all);
        }

        public override void Tick(Component owner)
        {
            if (_typing)
            {
                _other.Tick(dismiss: true);
                if (_focused && !_other.Focused)
                {
                    SubmitOther(_other.Text);
                }
                else
                {
                    _focused = _other.Focused;
                }
            }
            else
            {
                _known.Tick(owner);
            }
            _fresh?.Tick();
            foreach (var line in _lines)
            {
                line.Name.Tick();
                foreach (var box in line.Switches)
                {
                    box.Tick();
                }
                line.Preset.Tick(owner);
            }
        }

        // The game's server lists are files, so they are read again only when the lines are rebuilt.
        public override bool Sync()
        {
            var listed = ServerDirectory.Entries();
            var state = (string)Entry.BoxedValue + "|" + PresetMap(Page).Count;
            if (state == _shown)
            {
                return false;
            }
            _shown = state;
            _knownServers = KnownServers().Where(server => listed.All(entry => !entry.Key.Equals(server.Key, StringComparison.OrdinalIgnoreCase))).ToList();
            _known.BindNames(_knownServers.Select(server => server.Label == server.Key ? server.Key : $"{server.Label} ({server.Key})").Append("Other").ToArray(), -1);
            Rebuild(listed);
            return true;
        }

        public override void Close()
        {
            if (_typing)
            {
                TypeOther(false);
            }
            _known.Close();
            foreach (var line in _lines)
            {
                line.Preset.Close();
            }
        }
    }

    // Server or World Interface Presets as lines of place and preset, with a fresh line below the last.
    internal sealed class PresetsRow : WideRow
    {
        private const float PresetW = 260f;
        private readonly string _placeholder;
        private readonly List<(Field Name, IChoice Preset)> _lines = [];
        private Field _fresh;
        private RectTransform _area;
        private string _shown;

        public PresetsRow(SettingsPage page, Block block, ConfigEntryBase entry, string place, string placeholder)
            : base(page, block, entry, entry.Definition.Key, Lang.F("Settings.PresetDescription", "The SeneaL UI interface preset applied when you enter each {0}, chosen from SeneaL UI's packaged presets and your own.", place.ToLowerInvariant()))
        {
            _placeholder = Lang.F("Settings.AddPlaceholder", "Add: {0}", placeholder);
        }

        private Dictionary<string, string> Map() => PairMap((string)Entry.BoxedValue, '=');

        private void Save(Dictionary<string, string> map) => Entry.BoxedValue = string.Join("; ", map.Where(pair => pair.Key.Length > 0 && pair.Value.Length > 0).Select(pair => $"{pair.Key} = {pair.Value}"));

        private void Rebuild()
        {
            foreach (var line in _lines)
            {
                line.Preset.Dispose();
            }
            _lines.Clear();
            if (_area != null)
            {
                UnityEngine.Object.Destroy(_area.gameObject);
            }
            var map = Map().ToList();
            var nameW = RowW - 26f - 10f - PresetW - 10f;
            var choices = Presets();
            Page.Redraw(() =>
            {
                _area = Ui.TL(Body, "lines", 0f, 0f, RowW, 10f);
                for (var i = 0; i < map.Count; i++)
                {
                    var place = map[i].Key;
                    var root = Ui.TL(_area, "l" + i, 0f, i * LineH, RowW, LineH);
                    var name = new Field(root, 0f, 4f, nameW, null, text =>
                    {
                        var all = Map();
                        all.TryGetValue(place, out var preset);
                        all.Remove(place);
                        if ((text ?? "").Trim().Length > 0)
                        {
                            all[text.Trim()] = preset ?? "";
                        }
                        Save(all);
                    });
                    name.Show(place);
                    var select = Choice.Create(root, nameW + 10f, 3f, PresetW, Page.PopHost);
                    var current = choices.FindIndex(choice => string.Equals(choice.Value, map[i].Value, StringComparison.OrdinalIgnoreCase));
                    select.BindNames(choices.Select(choice => choice.Label).ToArray(), current);
                    select.SetCurrent(current >= 0 ? choices[current].Label : Pretty(map[i].Value));
                    select.OnPick = pick =>
                    {
                        var all = Map();
                        all[place] = choices[pick].Value;
                        Save(all);
                    };
                    Remove(root, RowW - 26f, 7f, () =>
                    {
                        var all = Map();
                        all.Remove(place);
                        Save(all);
                    });
                    _lines.Add((name, select));
                }
                _fresh = new Field(_area, 0f, map.Count * LineH + 4f, nameW, _placeholder, text =>
                {
                    var value = (text ?? "").Trim();
                    if (value.Length == 0)
                    {
                        return;
                    }
                    var all = Map();
                    all[value] = choices.Count > 0 ? choices[0].Value : "";
                    Save(all);
                });
            });
            var areaH = (map.Count + 1) * LineH;
            _area.sizeDelta = new Vector2(RowW, areaH);
            Fit(areaH);
        }

        public override void Tick(Component owner)
        {
            _fresh?.Tick();
            foreach (var (name, preset) in _lines)
            {
                name.Tick();
                preset.Tick(owner);
            }
        }

        public override bool Sync()
        {
            var state = (string)Entry.BoxedValue;
            if (state == _shown)
            {
                return false;
            }
            _shown = state;
            Rebuild();
            return true;
        }

        public override void Close()
        {
            foreach (var (_, preset) in _lines)
            {
                preset.Close();
            }
        }
    }

    // Restock Targets as lines of item and amount, with a dropdown below the last to add another item.
    internal sealed class RestockRow : WideRow
    {
        private const float AmountW = 90f;
        private readonly List<(IChoice Item, Field Amount)> _lines = [];
        private IChoice _add;
        private RectTransform _area;
        private string _shown;
        private List<string> _items = [];

        public RestockRow(SettingsPage page, Block block, ConfigEntryBase entry)
            : base(page, block, entry, "Restock Targets", "Amounts Restock to Targets Key tops your inventory up to from nearby chests.")
        {
        }

        private List<(string Item, int Amount)> Targets() =>
            ((string)Entry.BoxedValue).Split(';').Select(part => part.Trim()).Where(part => part.Length > 0).Select(part =>
            {
                var colon = part.LastIndexOf(':');
                return colon <= 0 ? (part, 1) : (part.Substring(0, colon).Trim(), int.TryParse(part.Substring(colon + 1).Trim(), out var n) ? n : 1);
            }).ToList();

        private void Save(IEnumerable<(string Item, int Amount)> targets) =>
            Entry.BoxedValue = string.Join("; ", targets.Where(target => target.Item.Length > 0 && target.Amount > 0).Select(target => $"{target.Item}: {target.Amount}"));

        private void Rebuild()
        {
            foreach (var line in _lines)
            {
                line.Item.Dispose();
            }
            _add?.Dispose();
            _lines.Clear();
            if (_area != null)
            {
                UnityEngine.Object.Destroy(_area.gameObject);
            }
            var targets = Targets();
            _items = ItemNames();
            foreach (var target in targets.Where(target => !_items.Contains(target.Item, StringComparer.OrdinalIgnoreCase)))
            {
                _items.Add(target.Item);
            }
            var names = _items.ToArray();
            const float ItemW = 360f;
            Page.Redraw(() =>
            {
                _area = Ui.TL(Body, "lines", 0f, 0f, RowW, 10f);
                Caption(_area, Lang.T("Settings.ColItem", "Item"), 0f, 0f, ItemW);
                Caption(_area, Lang.T("Settings.ColKeep", "Keep"), ItemW + 10f, 0f, AmountW);
                for (var i = 0; i < targets.Count; i++)
                {
                    var index = i;
                    var root = Ui.TL(_area, "l" + i, 0f, 22f + i * LineH, RowW, LineH);
                    var select = Choice.Create(root, 0f, 3f, ItemW, Page.PopHost);
                    select.BindNames(names, Array.FindIndex(names, name => name.Equals(targets[i].Item, StringComparison.OrdinalIgnoreCase)));
                    select.SetCurrent(targets[i].Item);
                    select.OnPick = pick =>
                    {
                        var all = Targets();
                        all[index] = (names[pick], all[index].Amount);
                        Save(all);
                    };
                    var amount = new Field(root, ItemW + 10f, 4f, AmountW, null, text =>
                    {
                        var all = Targets();
                        if (index < all.Count && int.TryParse((text ?? "").Trim(), out var n))
                        {
                            all[index] = (all[index].Item, Math.Max(1, n));
                            Save(all);
                        }
                    });
                    amount.Box.SetLimit(6);
                    amount.Show(targets[i].Amount.ToString(CultureInfo.InvariantCulture));
                    Remove(root, ItemW + 10f + AmountW + 10f, 7f, () =>
                    {
                        var all = Targets();
                        all.RemoveAt(index);
                        Save(all);
                    });
                    _lines.Add((select, amount));
                }
                _add = Choice.Create(_area, 0f, 22f + targets.Count * LineH + 3f, ItemW, Page.PopHost);
                _add.BindNames(names, -1);
                _add.SetCurrent(names.Length > 0 ? Lang.T("Settings.AddItem", "Add an Item") : Lang.T("Settings.ItemsAfterWorld", "Items are listed once a world is loaded"));
                _add.OnPick = pick =>
                {
                    var all = Targets();
                    all.Add((names[pick], 10));
                    Save(all);
                };
            });
            var areaH = 22f + (targets.Count + 1) * LineH;
            _area.sizeDelta = new Vector2(RowW, areaH);
            Fit(areaH);
        }

        public override void Tick(Component owner)
        {
            _add?.Tick(owner);
            foreach (var (item, amount) in _lines)
            {
                item.Tick(owner);
                amount.Tick();
            }
        }

        public override bool Sync()
        {
            var state = (string)Entry.BoxedValue + "|" + (ObjectDB.instance == null ? 0 : ObjectDB.instance.m_items.Count);
            if (state == _shown)
            {
                return false;
            }
            _shown = state;
            Rebuild();
            return true;
        }

        public override void Close()
        {
            _add?.Close();
            foreach (var (item, _) in _lines)
            {
                item.Close();
            }
        }
    }

    private static List<(string Label, string Key)> KnownServers() => ServerDirectory.KnownServers();

    private static void AddServer(string label, string key) => ServerDirectory.AddServer(label, key);

    private static void AddCurrentServer() => ServerDirectory.AddCurrentServer();
}
