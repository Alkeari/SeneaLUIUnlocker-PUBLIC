using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Inv;
using SeneaLUI.Map;
using SeneaLUI.Patches;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal interface IMapPinExtras
{
    void Tick();
}

// Additions to SeneaL UI's own pin editing on the world map, drawn with its components:
// - Under SeneaL UI's pin dialog, while a pin is edited: the pin's size, whether it goes to cartography tables, and buttons that give every pin of its type, or every pin, its color and size.
// - While the map's pin search holds text: a row of SeneaL UI's pin colors that recolors every matching pin of yours, and a button that removes them, clicked twice.
// - At a cartography table, each pin's SeneaL UI icon and color, its size and its auto-pin kind are written beside the game's own map data, under a key of their own that clients without this plugin never read, so pins arrive looking the same for everyone with it and as ordinary pins for everyone else.
internal sealed class MapPinExtras : IMapPinExtras
{
    private const string DataKey = "alkeari.senealuiunlocker.pinlooks";
    private const string TableKey = "alkeari.senealuiunlocker.looks";
    private const float CardW = 300f;
    private const int MaxTableEntries = 4000;

    private static MapPinExtras _current;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<float> _farther;
    private readonly ConfigEntry<float> _mergeWithin;
    private readonly HashSet<Minimap.PinData> _hidden = new();
    private GoldButton _merge;
    private readonly Dictionary<(int, int), (float Size, bool Private)> _looks = new();
    private readonly HashSet<Minimap.PinData> _scaled = new();
    private Player _for;
    private MapView _view;
    private RectTransform _card;
    private SliderBar _size;
    private SwitchBox _share;
    private Minimap.PinData _editing;
    private RectTransform _bulk;
    private GoldButton _remove;
    private float _removeArmed;
    private string _bulkTerm = "";

    private MapPinExtras(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Map", "Pin Extras", true,
            "Add to SeneaL UI's pin dialog a size and a switch for sharing at cartography tables, recolor or remove every pin matching the map's search, and carry pin icons, colors and sizes through cartography tables to players with this mod.");
        _farther = cfg.Bind("Map", "Hide Pins Farther Than", 0f, new ConfigDescription(
            "Hide your saved pins farther than this from you on the world map, meters; 0 shows every pin.", new AcceptableValueRange<float>(0f, 10000f)));
        _mergeWithin = cfg.Bind("Map", "Merge Duplicates Within", 20f, new ConfigDescription(
            "Merge Duplicates joins same-name pins this many meters apart or closer.", new AcceptableValueRange<float>(1f, 500f)));
        Canvas.willRenderCanvases += Scale;
        PinKinds.KeptOff = pin => PinExtrasOn && Look(pin.m_pos).Private;
        var harmony = new Harmony(Plugin.Guid + ".pinlooks");
        harmony.Patch(AccessTools.Method(typeof(MapTable), "OnWrite"), postfix: new HarmonyMethod(AccessTools.Method(typeof(MapPinExtras), nameof(AfterWrite))));
        harmony.Patch(AccessTools.Method(typeof(MapTable), "OnRead", [typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(bool)]), postfix: new HarmonyMethod(AccessTools.Method(typeof(MapPinExtras), nameof(AfterRead))));
    }

    public static IMapPinExtras Create(ConfigFile cfg) => _current = new MapPinExtras(cfg);

    private static (int, int) Key(Vector3 pos) => (Mathf.RoundToInt(pos.x * 2f), Mathf.RoundToInt(pos.z * 2f));

    private bool PinExtrasOn => _enabled.Value && ServerRules.Allows("MapSearch");

    private (float Size, bool Private) Look(Vector3 pos)
    {
        Load();
        return _looks.TryGetValue(Key(pos), out var look) ? look : (1f, false);
    }

    private void SetLook(Vector3 pos, float size, bool hidden)
    {
        Load();
        if (Mathf.Approximately(size, 1f) && !hidden)
        {
            _looks.Remove(Key(pos));
        }
        else
        {
            _looks[Key(pos)] = (size, hidden);
        }
        if (Player.m_localPlayer != null)
        {
            Player.m_localPlayer.m_customData[DataKey] = string.Join(";", _looks.Select(pair =>
                string.Format(CultureInfo.InvariantCulture, "{0},{1}={2:0.##},{3}", pair.Key.Item1, pair.Key.Item2, pair.Value.Size, pair.Value.Private ? 1 : 0)));
        }
    }

    private void Load()
    {
        var player = Player.m_localPlayer;
        if (player == null || player == _for)
        {
            return;
        }
        _for = player;
        _looks.Clear();
        if (!player.m_customData.TryGetValue(DataKey, out var saved))
        {
            return;
        }
        foreach (var part in saved.Split(';'))
        {
            var eq = part.IndexOf('=');
            var at = eq > 0 ? part.Substring(0, eq).Split(',') : [];
            var value = eq > 0 ? part.Substring(eq + 1).Split(',') : [];
            if (at.Length == 2 && value.Length == 2 && int.TryParse(at[0], out var x) && int.TryParse(at[1], out var z)
                && float.TryParse(value[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
            {
                _looks[(x, z)] = (size, value[1] == "1");
            }
        }
    }

    public void Tick()
    {
        var view = MapPatches.View;
        if (!PinExtrasOn || view == null || view._overlay == null || view._dialog == null)
        {
            Show(_card, false);
            Show(_bulk, false);
            return;
        }
        if (view != _view || _card == null)
        {
            _view = view;
            Build(view);
        }
        Dialog(view._dialog);
        Bulk();
    }

    private static void Show(RectTransform rt, bool on)
    {
        if (rt != null && rt.gameObject.activeSelf != on)
        {
            rt.gameObject.SetActive(on);
        }
    }

    private void Build(MapView view)
    {
        var dialog = view._dialog;
        _card = Ui.Node(dialog._root, "unlocker.pinextras");
        _card.anchorMin = _card.anchorMax = new Vector2(0.5f, 0.5f);
        _card.pivot = new Vector2(0.5f, 1f);
        _card.sizeDelta = new Vector2(CardW, 176f);
        Ui.Panel(_card, Skin.Sliced(48, Styles.Glass78), Styles.Glass78).raycastTarget = true;
        Caption(_card, Lang.T("MapPinExtras.PinSize", "Pin Size"), 14f);
        _size = new SliderBar(_card, "size", CardW - 28f, 0.5f, 2f, value =>
        {
            if (_editing != null)
            {
                SetLook(_editing.m_pos, Mathf.Round(value * 20f) / 20f, Look(_editing.m_pos).Private);
            }
        });
        Ui.Place(_size.Root, 0f, 1f, 14f, -36f, CardW - 28f, 22f);
        Caption(_card, Lang.T("MapPinExtras.ShareAtTables", "Share At Cartography Tables"), 72f);
        _share = new SwitchBox(_card, "share", on =>
        {
            if (_editing != null)
            {
                SetLook(_editing.m_pos, Look(_editing.m_pos).Size, !on);
            }
        });
        Ui.Place(_share.Root, 1f, 1f, -14f, -70f, 40f, 22f);
        var type = new GoldButton(_card, "type", CardW - 28f, () => Spread(sameType: true), 34, dark: true, TextStyles.BtnDarkText);
        type.SetLabel(Lang.T("MapPinExtras.ApplyToThisType", "Apply To This Type"));
        Ui.Place(type.Root, 0f, 1f, 14f, -100f, CardW - 28f, 34f);
        Tip(type.Root, Lang.T("MapPinExtras.ApplyToThisType", "Apply To This Type"), Lang.T("MapPinExtras.ApplyToThisTypeTip", "Give every pin of yours with this pin's type its color and size."));
        var all = new GoldButton(_card, "all", CardW - 28f, () => Spread(sameType: false), 34, dark: true, TextStyles.BtnDarkText);
        all.SetLabel(Lang.T("MapPinExtras.ApplyToAllPins", "Apply To All Pins"));
        Ui.Place(all.Root, 0f, 1f, 14f, -138f, CardW - 28f, 34f);
        Tip(all.Root, "Apply To All Pins", "Give every pin of yours this pin's color and size.");
        _card.gameObject.SetActive(false);

        _bulk = Ui.Node(view._overlay, "unlocker.bulk");
        _bulk.anchorMin = _bulk.anchorMax = _bulk.pivot = Vector2.zero;
        _bulk.anchoredPosition = new Vector2(16f, 104f);
        _bulk.sizeDelta = new Vector2(260f, 122f);
        Ui.Panel(_bulk, Skin.Sliced(48, Styles.Glass78), Styles.Glass78).raycastTarget = true;
        for (var i = 0; i < MapPins.PinColors.Length; i++)
        {
            var color = i;
            var swatch = Ui.TL(_bulk, "c" + i, 10f + i * 26f, 10f, 22f, 22f);
            Ui.Img(swatch, Skin.Circle(20, new BoxStyle(10f, Color.white, Palette.Clear, 0f)), i == 0 ? Palette.Ash : MapPins.PinColors[i]).raycastTarget = true;
            var input = swatch.gameObject.AddComponent<UIInputHandler>();
            input.m_onLeftClick = _ =>
            {
                Click.Play();
                Recolor(color);
            };
            Tip(swatch, i == 0 ? "Clear Color" : "Recolor Matches", i == 0 ? "Take the color off every pin of yours matching the search." : "Give every pin of yours matching the search this color.");
        }
        _remove = new GoldButton(_bulk, "remove", 240f, RemoveMatches, 34, dark: true, TextStyles.BtnDarkText);
        Ui.Place(_remove.Root, 0f, 1f, 10f, -42f, 240f, 34f);
        Tip(_remove.Root, "Remove Matches", "Remove every pin of yours matching the search. Click twice: the first click arms it.");
        _merge = new GoldButton(_bulk, "merge", 240f, MergeDuplicates, 34, dark: true, TextStyles.BtnDarkText);
        Ui.Place(_merge.Root, 0f, 1f, 10f, -80f, 240f, 34f);
        Tip(_merge.Root, "Merge Duplicates", "Of the pins matching the search, keep one of each name within the Merge Duplicates Within distance of another (20 meters unless changed) and remove the rest.");
        _bulk.gameObject.SetActive(false);
    }

    private static void Caption(RectTransform parent, string text, float top)
    {
        var label = Ui.Label(parent, "l" + top, TextStyles.CardLabel, 0f, 14f, top + TextStyles.CardLabel.Baseline(1.3f), CardW - 80f, TextAlignmentOptions.TopLeft);
        label.text = text;
        label.raycastTarget = false;
    }

    private static void Tip(RectTransform target, string title, string body)
    {
        var input = target.gameObject.GetComponent<UIInputHandler>() ?? target.gameObject.AddComponent<UIInputHandler>();
        var tip = new Tip();
        input.m_onPointerEnter = _ => Tooltip.Show(target, () => Tips.Text(tip, title, body));
        input.m_onPointerExit = _ => Tooltip.Hide();
    }

    // The card sits under SeneaL UI's dialog while it edits a pin.
    private void Dialog(PinDialog dialog)
    {
        var on = dialog._open && dialog._edit != null;
        Show(_card, on);
        if (!on)
        {
            _editing = null;
            return;
        }
        var panel = dialog._panel;
        _card.anchorMin = panel.anchorMin;
        _card.anchorMax = panel.anchorMax;
        var size = panel.rect.size;
        _card.anchoredPosition = new Vector2(panel.anchoredPosition.x + (0.5f - panel.pivot.x) * size.x, panel.anchoredPosition.y - size.y * panel.pivot.y - 8f);
        if (_editing != dialog._edit)
        {
            _editing = dialog._edit;
            var look = Look(_editing.m_pos);
            _size.Set(look.Size);
            _share.Set(!look.Private);
        }
        _share.Tick();
    }

    private static IEnumerable<Minimap.PinData> OwnPins() =>
        Minimap.instance == null ? [] : Minimap.instance.m_pins.Where(pin => pin.m_save && pin.m_ownerID == 0L);

    private void Spread(bool sameType)
    {
        if (_editing == null || Player.m_localPlayer == null)
        {
            return;
        }
        var color = MapPins.ColorOf(_editing.m_pos);
        var size = Look(_editing.m_pos).Size;
        foreach (var pin in OwnPins().Where(pin => !sameType || pin.m_type == _editing.m_type).ToList())
        {
            MapPins.SetColor(Player.m_localPlayer, pin.m_pos, color);
            SetLook(pin.m_pos, size, Look(pin.m_pos).Private);
            MapPins.Invalidate(pin);
        }
        Click.Play();
    }

    private List<Minimap.PinData> Matches() =>
        _bulkTerm.Length == 0 ? [] : OwnPins().Where(pin => (pin.m_name ?? "").ToLowerInvariant().Contains(_bulkTerm)).ToList();

    private void Bulk()
    {
        _bulkTerm = MapSearch.Term;
        var count = _bulkTerm.Length == 0 ? 0 : Matches().Count;
        Show(_bulk, count > 0);
        if (count == 0)
        {
            _removeArmed = 0f;
            return;
        }
        var armed = Time.unscaledTime < _removeArmed;
        _remove.SetLabel(armed ? Lang.F("MapPinExtras.ClickAgainToRemove", "Click Again To Remove {0}", count) : Lang.N("MapPinExtras.RemoveMatching", count, "Remove {0} Matching Pin", "Remove {0} Matching Pins"));
        var duplicates = Duplicates().Count;
        _merge.SetLabel(duplicates > 0 ? Lang.N("MapPinExtras.MergeDuplicates", duplicates, "Merge {0} Duplicate", "Merge {0} Duplicates") : Lang.T("MapPinExtras.NoDuplicates", "No Duplicates"));
        _merge.SetEnabled(duplicates > 0);
    }

    // Matching pins that repeat an earlier one's name within 20 meters.
    private List<Minimap.PinData> Duplicates()
    {
        var kept = new List<Minimap.PinData>();
        var extra = new List<Minimap.PinData>();
        foreach (var pin in Matches())
        {
            if (kept.Any(other => string.Equals(other.m_name, pin.m_name, StringComparison.OrdinalIgnoreCase) && Utils.DistanceXZ(other.m_pos, pin.m_pos) <= _mergeWithin.Value))
            {
                extra.Add(pin);
            }
            else
            {
                kept.Add(pin);
            }
        }
        return extra;
    }

    private void MergeDuplicates()
    {
        foreach (var pin in Duplicates())
        {
            Minimap.instance.RemovePin(pin);
        }
        if (_view != null && _view.Mm != null)
        {
            MapView.RequestPins(_view.Mm);
        }
    }

    private void Recolor(int color)
    {
        if (Player.m_localPlayer == null)
        {
            return;
        }
        foreach (var pin in Matches())
        {
            MapPins.SetColor(Player.m_localPlayer, pin.m_pos, color);
            MapPins.Invalidate(pin);
        }
    }

    private void RemoveMatches()
    {
        if (Time.unscaledTime >= _removeArmed)
        {
            _removeArmed = Time.unscaledTime + 4f;
            return;
        }
        _removeArmed = 0f;
        foreach (var pin in Matches())
        {
            Minimap.instance.RemovePin(pin);
        }
        if (_view != null && _view.Mm != null)
        {
            MapView.RequestPins(_view.Mm);
        }
    }

    // After SeneaL UI has drawn its pins (and the search has faded them): each pin's own size on its icon, and saved pins beyond Hide Pins Farther Than hidden.
    private void Scale()
    {
        if (Minimap.instance == null)
        {
            return;
        }
        var player = Player.m_localPlayer;
        var far = PinExtrasOn && player != null ? _farther.Value : 0f;
        foreach (var pair in MapPins.Uis)
        {
            var group = pair.Value.Group;
            if (group != null)
            {
                if (far > 0f && pair.Key.m_save && Utils.DistanceXZ(pair.Key.m_pos, player.transform.position) > far)
                {
                    group.alpha = 0f;
                    _hidden.Add(pair.Key);
                }
                else if (_hidden.Remove(pair.Key))
                {
                    group.alpha = pair.Value.Alpha;
                }
            }
            var icon = pair.Value.Icon;
            if (icon == null)
            {
                continue;
            }
            var size = PinExtrasOn ? Look(pair.Key.m_pos).Size : 1f;
            if (!Mathf.Approximately(size, 1f))
            {
                icon.rectTransform.localScale = Vector3.one * size;
                _scaled.Add(pair.Key);
            }
            else if (_scaled.Remove(pair.Key))
            {
                icon.rectTransform.localScale = Vector3.one;
            }
        }
    }

    // Writing at a table: the looks of the pins just shared join the looks already there.
    private static void AfterWrite(MapTable __instance, ItemDrop.ItemData item)
    {
        var self = _current;
        var player = Player.m_localPlayer;
        var nview = __instance.m_nview;
        if (self == null || !self.PinExtrasOn || item != null || player == null || nview == null || !nview.IsValid() || !PrivateArea.CheckAccess(__instance.transform.position, 0f, false))
        {
            return;
        }
        var looks = Parse(nview.GetZDO().GetString(TableKey));
        foreach (var pin in OwnPins())
        {
            var look = self.Look(pin.m_pos);
            if (look.Private)
            {
                continue;
            }
            var custom = MapPins.CustomOf(pin.m_pos);
            var color = MapPins.ColorOf(pin.m_pos);
            var kind = PinKinds.IndexOf(pin.m_type);
            if (custom == 0 && color == 0 && Mathf.Approximately(look.Size, 1f) && kind < 0)
            {
                continue;
            }
            looks[Key(pin.m_pos)] = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:0.##},{3}", custom, color, look.Size, kind);
        }
        if (looks.Count > MaxTableEntries)
        {
            return;
        }
        nview.ClaimOwnership();
        nview.GetZDO().Set(TableKey, string.Join(";", looks.Select(pair => $"{pair.Key.Item1},{pair.Key.Item2}={pair.Value}")));
    }

    // Reading at a table: pins that came from it, and have no look of their own here yet, take the look written with them.
    private static void AfterRead(MapTable __instance, ItemDrop.ItemData item)
    {
        var self = _current;
        var player = Player.m_localPlayer;
        var nview = __instance.m_nview;
        if (self == null || !self.PinExtrasOn || item != null || player == null || nview == null || !nview.IsValid() || Minimap.instance == null)
        {
            return;
        }
        var looks = Parse(nview.GetZDO().GetString(TableKey));
        if (looks.Count == 0)
        {
            return;
        }
        foreach (var pin in Minimap.instance.m_pins.Where(pin => pin.m_save))
        {
            if (!looks.TryGetValue(Key(pin.m_pos), out var value))
            {
                continue;
            }
            var parts = value.Split(',');
            if (parts.Length != 4)
            {
                continue;
            }
            if (int.TryParse(parts[0], out var custom) && custom > 0 && MapPins.CustomOf(pin.m_pos) == 0)
            {
                MapPins.SetCustom(player, pin.m_pos, custom);
            }
            if (int.TryParse(parts[1], out var color) && color > 0 && MapPins.ColorOf(pin.m_pos) == 0)
            {
                MapPins.SetColor(player, pin.m_pos, color);
            }
            if (float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && !Mathf.Approximately(size, 1f) && Mathf.Approximately(self.Look(pin.m_pos).Size, 1f))
            {
                self.SetLook(pin.m_pos, Mathf.Clamp(size, 0.5f, 2f), false);
            }
            if (int.TryParse(parts[3], out var kind) && kind >= 0 && kind < PinKinds.Names.Length && pin.m_type == Minimap.PinType.Icon3)
            {
                pin.m_type = PinKinds.Of(PinKinds.Names[kind]);
            }
            MapPins.Invalidate(pin);
        }
    }

    private static Dictionary<(int, int), string> Parse(string text)
    {
        var looks = new Dictionary<(int, int), string>();
        foreach (var part in (text ?? "").Split(';'))
        {
            var eq = part.IndexOf('=');
            var at = eq > 0 ? part.Substring(0, eq).Split(',') : [];
            if (at.Length == 2 && int.TryParse(at[0], out var x) && int.TryParse(at[1], out var z))
            {
                looks[(x, z)] = part.Substring(eq + 1);
            }
        }
        return looks;
    }
}
