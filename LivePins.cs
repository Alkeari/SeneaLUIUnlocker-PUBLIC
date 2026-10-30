using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Saved map pins for portals, ships and carts that follow the object: a portal pin takes the portal's new tag, a ship or cart pin moves with it, and a pin goes away once its object is gone. Being saved pins, they stay on the map while the object is far away and go to a cartography table.
// A pin the player deletes goes into SeneaL UI's own Hidden Marks list (LiveMarks.Hidden, keyed p, s or c plus the object's id, as SeneaL UI keys its hidden live marks), so it stays gone; taking it off that list brings the pin back.
// They replace SeneaL UI's live marks for the same objects ([Map] LivePortals, LiveShips, LiveCarts), which this turns off while on and gives back as they were when turned off.
internal sealed class LivePins
{
    private const string Section = "Live Pins";
    private const string DataKey = "alkeari.senealuiunlocker.livepins";
    private const float CheckSeconds = 2f;
    private const float GoneCheckRange = 40f;
    private const float SameSpot = 5f;

    private const float SettleSeconds = 15f;

    private static readonly Type SeneaLPlugin = TypeLookup.Find("SeneaLUI.Plugin");
    private static readonly Type LiveMarks = TypeLookup.Find("SeneaLUI.Map.LiveMarks");
    private static readonly FieldInfo HiddenField = LiveMarks == null ? null : AccessTools.Field(LiveMarks, "Hidden");
    private static readonly MethodInfo SaveHidden = LiveMarks == null ? null : AccessTools.Method(LiveMarks, "SaveHidden", [typeof(Player)]);
    private static readonly MethodInfo LoadHidden = LiveMarks == null ? null : AccessTools.Method(LiveMarks, "LoadHidden", [typeof(Player)]);
    private static readonly FieldInfo HiddenLoaded = LiveMarks == null ? null : AccessTools.Field(LiveMarks, "_loaded");
    private static readonly FieldInfo HiddenOf = LiveMarks == null ? null : AccessTools.Field(LiveMarks, "_hiddenOf");

    private static readonly FieldInfo TracksField = LiveMarks == null ? null : AccessTools.Field(LiveMarks, "Tracks");

    [HarmonyPatch(typeof(ZDOID), nameof(ZDOID.Reset))]
    // Joining a server resets the game's table of user ids, after which a ship or cart id SeneaL UI kept from the last
    // server no longer hashes (an index past the table), so its own track-by-track drop on the next spawn throws and the
    // spawn loops (SeneaL UI 1.2.8). The tracks are emptied first with Clear, which hashes nothing; their pins went with
    // the last world's map.
    internal static class ResetPatch
    {
        private static void Prefix()
        {
            if (TracksField?.GetValue(null) is System.Collections.IDictionary tracks)
            {
                tracks.Clear();
            }
        }
    }

    private readonly ConfigEntry<bool> _portals;
    private readonly ConfigEntry<bool> _ships;
    private readonly ConfigEntry<bool> _carts;
    private readonly ConfigEntry<string> _seneaLBefore;
    private readonly Dictionary<ZDOID, Record> _records = [];
    private readonly Dictionary<string, ConfigEntry<bool>> _seneaLEntries = [];

    private Player _for;
    private float _next;
    private float _loadedAt;

    private sealed class Record
    {
        public string Kind;
        public string Name;
        public Vector3 Pos;
        public bool Hidden;
    }

    public LivePins(ConfigFile cfg)
    {
        _portals = cfg.Bind(Section, "Live Portal Pins", true, "Pin every portal you come near with its tag as the name, renamed with the portal and removed with it. Turns off SeneaL UI's own live portal marks (and with them its portal link lines) while on.");
        _ships = cfg.Bind(Section, "Live Ship Pins", true, "Pin every ship you come near, moving with it and removed with it. Turns off SeneaL UI's own live ship marks while on.");
        _carts = cfg.Bind(Section, "Live Cart Pins", true, "Pin every cart you come near, moving with it and removed with it. Turns off SeneaL UI's own live cart marks while on.");
        _seneaLBefore = cfg.Bind(Section, "SeneaL UI Live Marks Before", "", new ConfigDescription(
            "SeneaL UI's live mark settings as they were before these pins replaced them, kept by the mod to give them back.", null, new ConfigurationManagerAttributes { Browsable = false }));
    }

    public void Tick(float now)
    {
        var allowed = ServerRules.Allows("LivePins");
        Replace("MapPortals", _portals.Value && allowed);
        Replace("MapShips", _ships.Value && allowed);
        Replace("MapCarts", _carts.Value && allowed);
        var player = Player.m_localPlayer;
        var map = Minimap.instance;
        if (player == null || map == null || now < _next)
        {
            return;
        }
        _next = now + CheckSeconds;
        if (player != _for)
        {
            _for = player;
            _loadedAt = now;
            Load(player);
        }
        var seen = new HashSet<ZDOID>();
        if (_portals.Value && allowed)
        {
            foreach (var portal in SceneObjects.Live(SceneObjects.Portals))
            {
                var zdo = Zdo(portal);
                if (zdo != null)
                {
                    var tag = zdo.GetString(ZDOVars.s_tag, "");
                    Track(map, zdo.m_uid, "portal", tag.Length > 0 ? tag : "Portal", portal.transform.position, seen, now);
                }
            }
        }
        if (_ships.Value && allowed)
        {
            foreach (var ship in SceneObjects.Live(SceneObjects.Ships))
            {
                var zdo = Zdo(ship);
                if (zdo != null)
                {
                    Track(map, zdo.m_uid, "ship", VehicleNames.Label(ship, PieceName(ship, "Ship")), ship.transform.position, seen, now);
                }
            }
        }
        if (_carts.Value && allowed)
        {
            foreach (var cart in SceneObjects.Carts)
            {
                var zdo = Zdo(cart);
                if (zdo != null)
                {
                    Track(map, zdo.m_uid, "cart", VehicleNames.Label(cart, PieceName(cart, "Cart")), cart.transform.position, seen, now);
                }
            }
        }
        DropGone(map, player.transform.position, seen);
        Save(player);
    }

    // SeneaL UI reads its hidden list from the character only while one of its own live marks is on, which these pins turn off, so it is read here when it has not been.
    private static HashSet<string> HiddenSet
    {
        get
        {
            var player = Player.m_localPlayer;
            if (HiddenField == null || LoadHidden == null || HiddenLoaded == null || HiddenOf == null || player == null)
            {
                return null;
            }
            if (!(bool)HiddenLoaded.GetValue(null) || !ReferenceEquals(HiddenOf.GetValue(null), player))
            {
                LoadHidden.Invoke(null, [player]);
            }
            return HiddenField.GetValue(null) as HashSet<string>;
        }
    }

    private static string HiddenKey(string kind, ZDOID id) => (kind == "portal" ? "p" : kind == "ship" ? "s" : "c") + id.UserID + ":" + id.ID;

    // Whether the pin of a tracked object stays off the map: it is on SeneaL UI's Hidden Marks list, or it was just deleted and goes onto it. Taken off that list, the pin is put back.
    private bool Hidden(Minimap map, ZDOID id, Record record, float now)
    {
        var hidden = HiddenSet;
        var key = HiddenKey(record.Kind, id);
        if (hidden != null && hidden.Contains(key))
        {
            record.Hidden = true;
            return true;
        }
        if (record.Hidden)
        {
            record.Hidden = false;
            if (Find(map, record) == null)
            {
                map.AddPin(record.Pos, Icon(record.Kind), record.Name, true, false);
            }
            return false;
        }
        // Only a pin missing well after the map's own pins were loaded counts as deleted by the player.
        if (Find(map, record) != null || now - _loadedAt < SettleSeconds)
        {
            return false;
        }
        if (hidden == null || SaveHidden == null)
        {
            map.AddPin(record.Pos, Icon(record.Kind), record.Name, true, false);
            return false;
        }
        hidden.Add(key);
        SaveHidden.Invoke(null, [Player.m_localPlayer]);
        record.Hidden = true;
        return true;
    }

    private void Track(Minimap map, ZDOID id, string kind, string name, Vector3 pos, HashSet<ZDOID> seen, float now)
    {
        seen.Add(id);
        if (!_records.TryGetValue(id, out var record))
        {
            record = new Record { Kind = kind, Name = name, Pos = pos };
            _records[id] = record;
            // A pin of the same name already at the spot (the player's own, or one this kept before its record was lost) becomes this one rather than getting a twin.
            if (Find(map, record) == null)
            {
                map.AddPin(pos, Icon(kind), name, true, false);
            }
            return;
        }
        if (Hidden(map, id, record, now))
        {
            record.Name = name;
            record.Pos = pos;
            return;
        }
        if (record.Name == name && Flat(record.Pos, pos) < 1f)
        {
            return;
        }
        var pin = Find(map, record);
        var oldName = record.Name;
        var oldPos = record.Pos;
        record.Name = name;
        record.Pos = pos;
        foreach (var twin in map.m_pins.Where(p => p != pin && p.m_name == oldName && Flat(p.m_pos, oldPos) < SameSpot).ToList())
        {
            map.RemovePin(twin);
        }
        if (pin == null)
        {
            map.AddPin(pos, Icon(kind), name, true, false);
            return;
        }
        pin.m_name = name;
        pin.m_pos = pos;
        if (pin.m_NamePinData != null && pin.m_NamePinData.PinNameText != null)
        {
            pin.m_NamePinData.PinNameText.text = Localization.instance.Localize(name);
        }
    }

    // Gone only when the player stands close enough for the game to have the object loaded, and the game no longer knows it.
    private void DropGone(Minimap map, Vector3 at, HashSet<ZDOID> seen)
    {
        foreach (var pair in _records.ToList())
        {
            if (seen.Contains(pair.Key) || !Wanted(pair.Value.Kind) || Flat(pair.Value.Pos, at) > GoneCheckRange || ZDOMan.instance.GetZDO(pair.Key) != null)
            {
                continue;
            }
            var pin = Find(map, pair.Value);
            if (pin != null)
            {
                map.RemovePin(pin);
            }
            _records.Remove(pair.Key);
        }
    }

    private bool Wanted(string kind) => kind switch
    {
        "portal" => _portals.Value,
        "ship" => _ships.Value,
        _ => _carts.Value
    };

    private static Minimap.PinData Find(Minimap map, Record record) =>
        map.m_pins.FirstOrDefault(pin => pin.m_name == record.Name && Flat(pin.m_pos, record.Pos) < SameSpot);

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    private static Minimap.PinType Icon(string kind) => kind == "portal" ? Minimap.PinType.Icon4 : Minimap.PinType.Icon3;

    private static ZDO Zdo(Component c)
    {
        var view = c.GetComponent<ZNetView>();
        return view == null || !view.IsValid() ? null : view.GetZDO();
    }

    private static string PieceName(Component c, string fallback)
    {
        var piece = c.GetComponent<Piece>();
        return piece == null ? fallback : Localization.instance.Localize(piece.m_name);
    }

    private void Load(Player player)
    {
        _records.Clear();
        if (!player.m_customData.TryGetValue(DataKey, out var saved))
        {
            return;
        }
        foreach (var line in saved.Split('\n'))
        {
            var parts = line.Split('|');
            if (parts.Length == 6 && long.TryParse(parts[0], out var user) && uint.TryParse(parts[1], out var id)
                && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
            {
                _records[new ZDOID(user, id)] = new Record { Kind = parts[2], Pos = new Vector3(x, 0f, z), Name = parts[5] };
            }
        }
        // Positions are kept without height; a saved pin is matched by name and position on the map, which has none either.
        var map = Minimap.instance;
        foreach (var record in _records.Values)
        {
            var pin = map.m_pins.FirstOrDefault(p => p.m_name == record.Name && Flat(p.m_pos, record.Pos) < 2f);
            if (pin != null)
            {
                record.Pos = pin.m_pos;
            }
        }
    }

    private void Save(Player player)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        player.m_customData[DataKey] = string.Join("\n", _records.Select(pair =>
            $"{pair.Key.UserID}|{pair.Key.ID}|{pair.Value.Kind}|{pair.Value.Pos.x.ToString(inv)}|{pair.Value.Pos.z.ToString(inv)}|{pair.Value.Name.Replace("|", "/").Replace("\n", " ")}"));
    }

    // Turns SeneaL UI's matching live mark setting off while the pins are on, remembering its value, and gives it back when they are turned off.
    private void Replace(string property, bool ours)
    {
        if (!_seneaLEntries.TryGetValue(property, out var entry))
        {
            entry = SeneaLPlugin == null ? null : AccessTools.Property(SeneaLPlugin, property)?.GetValue(null, null) as ConfigEntry<bool>;
            if (entry == null)
            {
                return;
            }
            _seneaLEntries[property] = entry;
        }
        if (ours == !entry.Value && !_seneaLBefore.Value.Contains(property + "="))
        {
            return;
        }
        var before = _seneaLBefore.Value.Split(';').Where(s => s.Contains('=')).ToDictionary(s => s.Split('=')[0], s => s.Split('=')[1]);
        if (ours && entry.Value)
        {
            before[property] = "true";
            entry.Value = false;
        }
        else if (!ours && before.TryGetValue(property, out var was))
        {
            before.Remove(property);
            entry.Value = was == "true";
        }
        else
        {
            return;
        }
        _seneaLBefore.Value = string.Join(";", before.Select(pair => $"{pair.Key}={pair.Value}"));
    }
}
