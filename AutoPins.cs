using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Pins places and resources the player comes near, as ordinary saved map pins, so they stay on the map and go to a cartography table like the player's own. Each spot is pinned once: a pin the player removes is not put back.
// Ores are left to SeneaL UI's own ore detection, which already pins deposits and tracks how much is left.
internal enum DungeonDone
{
    Key_Pressed,
    All_Chests_Opened,
    Enemies_Cleared,
    Chests_And_Enemies
}

internal sealed class AutoPins
{
    private const string Section = "Auto Pins";
    private const string DataKey = "alkeari.senealuiunlocker.autopins";
    private const string DoneKey = "alkeari.senealuiunlocker.dungeonsdone";

    // SeneaL UI 1.2.0 marks dungeons itself, beside its ore marks, with pins it rebuilds every session (never saved), so while it does this plugin pins none of its own and crosses out those, keeping which are done here.
    private static readonly Type OreScout = TypeLookup.Find("SeneaLUI.Map.OreScout");
    private static readonly MethodInfo OreWanted = OreScout == null ? null : AccessTools.Method(OreScout, "Wanted", [typeof(string)]);
    private static readonly PropertyInfo OreDetect = AccessTools.Property(TypeLookup.Find("SeneaLUI.Plugin"), "OreDetect");
    private static readonly PropertyInfo CanOreDetect = AccessTools.Property(TypeLookup.Find("SeneaLUI.Net.Policy"), "CanOreDetect");
    private static readonly FieldInfo SeneaLDungeonType = AccessTools.Field(TypeLookup.Find("SeneaLUI.Map.MapPins"), "Dungeon");
    private const float CheckSeconds = 3f;
    private const float SamePlace = 20f;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<float> _radius;
    private readonly Category[] _categories;
    private readonly ConfigEntry<bool> _resources;
    private readonly ConfigEntry<string> _resourceItems;
    private readonly ConfigEntry<float> _resourceCluster;
    private readonly ConfigEntry<DungeonDone> _dungeonDone;
    private readonly ConfigEntry<KeyboardShortcut> _doneKey;
    private float _insideSince = -1f;
    private ZoneSystem _audited;
    private readonly BepInEx.Logging.ManualLogSource _log;

    private Player _for;
    private HashSet<string> _done = [];
    private HashSet<string> _dungeonsDone = [];
    private float _next;

    private sealed class Category
    {
        public ConfigEntry<bool> On;
        public ConfigEntry<string> Names;
        public Minimap.PinType Icon;
        public string Kind;
    }

    public AutoPins(ConfigFile cfg, BepInEx.Logging.ManualLogSource log)
    {
        _log = log;
        _enabled = cfg.Bind(Section, "Auto Pins", true, "Pin useful places and resources on the map as you come near them.");
        _radius = cfg.Bind(Section, "Discover Radius", 60f, new ConfigDescription("Distance, meters, within which a place is pinned.", new AcceptableValueRange<float>(10f, 200f)));
        _categories =
        [
            Make(cfg, "Boss Altars", "Eikthyrnir; GDKing; Bonemass; Dragonqueen; GoblinKing; Mistlands_DvergrBossEntrance; FaderLocation", Minimap.PinType.Boss),
            Make(cfg, "Traders", "Vendor_BlackForest; Hildir_camp; BogWitch_Camp", PinKinds.Of("Traders")),
            Make(cfg, "Dungeons", "Crypt; SunkenCrypt; TrollCave; MountainCave; Mistlands_DvergrTownEntrance; Hildir_crypt; Hildir_cave; Hildir_plainsfortress; MorgenHole; PlaceofMystery", PinKinds.Of("Dungeons")),
            Make(cfg, "Villages And Camps", "WoodVillage; WoodFarm; GoblinCamp; Greydwarf_camp; StoneTowerRuins; Mistlands_Harbour; Mistlands_Excavation; Mistlands_Giant; CharredFortress; CharredRuins; FortressRuins", PinKinds.Of("Villages And Camps")),
            Make(cfg, "Runestones", "Runestone_; DrakeLorestone", PinKinds.Of("Runestones")),
            Make(cfg, "Nests And Pits", "DrakeNest; TarPit; VoltureNest; InfestedTree", PinKinds.Of("Nests And Pits"))
        ];
        _resources = cfg.Bind(Section, "Pin Resources", false, "Pin pickable resources listed in Resource Items, one pin per patch. Off by default, since it adds a great many pins.");
        _resourceItems = cfg.Bind(Section, "Resource Items",
            "Raspberry; Blueberries; Cloudberry; MushroomYellow; MushroomBlue; MushroomJotunPuffs; MushroomMagecap; Thistle; Flax; Barley; CarrotSeeds; TurnipSeeds; OnionSeeds; DragonEgg; SurtlingCore; Fiddleheadfern; Vineberry",
            "Pickable items that get a pin, by the item's internal name, separated by semicolons.");
        _dungeonDone = cfg.Bind(Section, "Dungeon Done When", DungeonDone.Key_Pressed,
            "When a pinned dungeon's pin is crossed out: only when you press Mark Dungeon Done Key, or also on its own once every chest inside is empty, once no living enemy is left inside, or once both are true.");
        _doneKey = cfg.Bind(Section, "Mark Dungeon Done Key", new KeyboardShortcut(KeyCode.K),
            "Crosses out the pin of the dungeon you are in or standing at, or brings it back if it is crossed out. Works whatever Dungeon Done When says. Empty = no key.");
        _resourceCluster = cfg.Bind(Section, "Resource Patch Radius", 40f, new ConfigDescription("Resources of one kind within this distance, meters, share one pin.", new AcceptableValueRange<float>(5f, 200f)));
    }

    private static Category Make(ConfigFile cfg, string kind, string names, Minimap.PinType icon) => new()
    {
        Kind = kind,
        Icon = icon,
        On = cfg.Bind(Section, $"Pin {kind}", true, $"Pin {kind.ToLowerInvariant()} as you come near them."),
        Names = cfg.Bind(Section, $"{kind} Locations", names, $"Locations pinned as {kind.ToLowerInvariant()}, by the start of the location's internal name, separated by semicolons.")
    };

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        var map = Minimap.instance;
        if (_enabled.Value && ServerRules.Allows("AutoPins") && player != null && map != null && KeyInput.Pressed(_doneKey.Value) && !InventoryFeatures.Typing())
        {
            ToggleDone(map, player);
        }
        if (!_enabled.Value || !ServerRules.Allows("AutoPins") || player == null || map == null || now < _next)
        {
            return;
        }
        _next = now + CheckSeconds;
        Audit();
        if (player != _for)
        {
            _for = player;
            _done = player.m_customData.TryGetValue(DataKey, out var saved) ? saved.Split('|').Where(s => s.Length > 0).ToHashSet() : [];
            _dungeonsDone = player.m_customData.TryGetValue(DoneKey, out var done) ? done.Split('|').Where(s => s.Length > 0).ToHashSet() : [];
        }
        Retype(map);
        CrossOutSeneaLDungeons(map);
        var at = player.transform.position;
        Dungeons(map, player, now);
        var added = Locations(map, at) + Resources(map, at);
        if (added > 0)
        {
            player.m_customData[DataKey] = string.Join("|", _done);
        }
    }

    private int Locations(Minimap map, Vector3 at)
    {
        var added = 0;
        foreach (var location in Location.s_allLocations)
        {
            if (location == null || Vector3.Distance(location.transform.position, at) > _radius.Value)
            {
                continue;
            }
            var prefab = Prefab(location.gameObject);
            var category = _categories.FirstOrDefault(c => c.On.Value && Starts(prefab, c.Names.Value));
            if (category != null && category.Kind == "Dungeons" && SeneaLMarksDungeons())
            {
                continue;
            }
            if (category == null)
            {
                continue;
            }
            added += Pin(map, location.transform.position, category.Icon, Label(location, prefab), category.Kind) ? 1 : 0;
        }
        return added;
    }

    private int Resources(Minimap map, Vector3 at)
    {
        if (!_resources.Value)
        {
            return 0;
        }
        var wanted = Split(_resourceItems.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var pickable in SceneObjects.Live(SceneObjects.Pickables))
        {
            if (pickable == null || pickable.m_itemPrefab == null || !wanted.Contains(pickable.m_itemPrefab.name)
                || Vector3.Distance(pickable.transform.position, at) > _radius.Value)
            {
                continue;
            }
            var drop = pickable.m_itemPrefab.GetComponent<ItemDrop>();
            var name = drop == null ? pickable.m_itemPrefab.name : Localization.instance.Localize(drop.m_itemData.m_shared.m_name);
            added += Pin(map, pickable.transform.position, PinKinds.Of("Resources"), name, "res:" + pickable.m_itemPrefab.name, _resourceCluster.Value) ? 1 : 0;
        }
        return added;
    }

    // Pins saved as plain dots (PinKinds) get their kind back: a dot at a place this plugin pinned for a kind is that kind's pin.
    private int _retypedAt = -1;

    private void Retype(Minimap map)
    {
        if (map.m_pins.Count == _retypedAt)
        {
            return;
        }
        _retypedAt = map.m_pins.Count;
        foreach (var pin in map.m_pins.Where(pin => pin.m_type == Minimap.PinType.Icon3 && pin.m_ownerID == 0L))
        {
            foreach (var key in _done)
            {
                var at = key.LastIndexOf('@');
                var kind = at < 0 ? "" : key.Substring(0, at);
                var type = kind.StartsWith("res:", StringComparison.Ordinal) ? PinKinds.Of("Resources") : PinKinds.Has(kind) ? PinKinds.Of(kind) : Minimap.PinType.Icon3;
                if (type != Minimap.PinType.Icon3 && Near(key, kind, pin.m_pos, kind.StartsWith("res:", StringComparison.Ordinal) ? _resourceCluster.Value : SamePlace))
                {
                    pin.m_type = type;
                    break;
                }
            }
        }
    }

    private bool Pin(Minimap map, Vector3 pos, Minimap.PinType icon, string name, string kind, float spread = SamePlace)
    {
        if (_done.Any(key => Near(key, kind, pos, spread)))
        {
            return false;
        }
        _done.Add($"{kind}@{Mathf.RoundToInt(pos.x)},{Mathf.RoundToInt(pos.z)}");
        var existing = map.m_pins.FirstOrDefault(pin => pin.m_name == name && Vector3.Distance(pin.m_pos, pos) < spread);
        if (existing == null)
        {
            map.AddPin(pos, icon, name, true, false);
        }
        else if (existing.m_type == Minimap.PinType.Icon3)
        {
            existing.m_type = icon;
        }
        return true;
    }

    private static bool Near(string key, string kind, Vector3 pos, float spread)
    {
        var at = key.LastIndexOf('@');
        if (at < 0 || key.Substring(0, at) != kind)
        {
            return false;
        }
        var xz = key.Substring(at + 1).Split(',');
        return xz.Length == 2 && int.TryParse(xz[0], out var x) && int.TryParse(xz[1], out var z)
               && new Vector2(x - pos.x, z - pos.z).magnitude < spread;
    }

    // Dungeon interiors sit high above the world (the game builds them at about 5000 m), straight over their entrance.
    private const float InteriorHeight = 3000f;
    private const float InsideSettleSeconds = 10f;

    private Location CurrentDungeon(Vector3 at)
    {
        var dungeons = _categories.First(c => c.Kind == "Dungeons");
        return Location.s_allLocations
            .Where(l => l != null && Starts(Prefab(l.gameObject), dungeons.Names.Value))
            .Select(l => (Location: l, Distance: Flat(l.transform.position, at)))
            .Where(pair => pair.Distance <= Mathf.Max(pair.Location.m_exteriorRadius, pair.Location.m_interiorRadius, 30f))
            .OrderBy(pair => pair.Distance)
            .Select(pair => pair.Location)
            .FirstOrDefault();
    }

    private Minimap.PinData DungeonPin(Minimap map, Location dungeon)
    {
        var label = Label(dungeon, Prefab(dungeon.gameObject));
        var seneaL = SeneaLDungeonPinType();
        return map.m_pins.FirstOrDefault(pin => pin.m_name == label && Flat(pin.m_pos, dungeon.transform.position) < SamePlace)
               ?? map.m_pins.FirstOrDefault(pin => seneaL.HasValue && pin.m_type == seneaL.Value && Flat(pin.m_pos, dungeon.transform.position) < SamePlace);
    }

    private static Minimap.PinType? SeneaLDungeonPinType() => SeneaLDungeonType?.GetValue(null) is Minimap.PinType type ? type : null;

    private static bool SeneaLMarksDungeons()
    {
        try
        {
            return SeneaLDungeonType != null && OreWanted != null && OreDetect?.GetValue(null, null) is ConfigEntry<bool> { Value: true }
                   && CanOreDetect?.GetValue(null, null) is true && OreWanted.Invoke(null, ["dungeon"]) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DoneSpot(Vector3 pos) => $"{Mathf.RoundToInt(pos.x)},{Mathf.RoundToInt(pos.z)}";

    private void SetDone(Player player, Minimap.PinData pin, bool done)
    {
        pin.m_checked = done;
        if (pin.m_save)
        {
            return;
        }
        _dungeonsDone.RemoveWhere(spot => SameSpot(spot, pin.m_pos));
        if (done)
        {
            _dungeonsDone.Add(DoneSpot(pin.m_pos));
        }
        player.m_customData[DoneKey] = string.Join("|", _dungeonsDone);
    }

    private static bool SameSpot(string spot, Vector3 pos)
    {
        var xz = spot.Split(',');
        return xz.Length == 2 && int.TryParse(xz[0], out var x) && int.TryParse(xz[1], out var z) && new Vector2(x - pos.x, z - pos.z).magnitude < SamePlace;
    }

    private void CrossOutSeneaLDungeons(Minimap map)
    {
        var seneaL = SeneaLDungeonPinType();
        if (!seneaL.HasValue || _dungeonsDone.Count == 0)
        {
            return;
        }
        foreach (var pin in map.m_pins.Where(pin => pin.m_type == seneaL.Value && !pin.m_checked && _dungeonsDone.Any(spot => SameSpot(spot, pin.m_pos))))
        {
            pin.m_checked = true;
        }
    }

    private void ToggleDone(Minimap map, Player player)
    {
        var dungeon = CurrentDungeon(player.transform.position);
        var pin = dungeon == null ? null : DungeonPin(map, dungeon);
        if (pin == null)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.T("AutoPins.StandInOrAtA", "Stand in or at a pinned dungeon to mark it done"));
            return;
        }
        SetDone(player, pin, !pin.m_checked);
        player.Message(MessageHud.MessageType.TopLeft, pin.m_checked ? Lang.F("AutoPins.MarkedDone", "{0} marked done", pin.m_name) : Lang.F("AutoPins.MarkedNotDone", "{0} marked not done", pin.m_name));
    }

    private void Dungeons(Minimap map, Player player, float now)
    {
        var at = player.transform.position;
        if (_dungeonDone.Value == DungeonDone.Key_Pressed || at.y < InteriorHeight)
        {
            _insideSince = -1f;
            return;
        }
        if (_insideSince < 0f)
        {
            _insideSince = now;
        }
        var dungeon = CurrentDungeon(at);
        var pin = dungeon == null ? null : DungeonPin(map, dungeon);
        if (pin == null || pin.m_checked || now - _insideSince < InsideSettleSeconds)
        {
            return;
        }
        var radius = Mathf.Max(dungeon.m_interiorRadius, 64f);
        bool Inside(Vector3 p) => p.y > InteriorHeight && Flat(p, dungeon.transform.position) <= radius;
        var chestsEmpty = SceneObjects.Live(SceneObjects.Containers)
            .Where(c => c != null && Inside(c.transform.position) && c.GetInventory() != null)
            .All(c => c.GetInventory().NrOfItems() == 0);
        var enemiesGone = !Character.GetAllCharacters()
            .Any(c => c != null && !c.IsDead() && !c.IsPlayer() && !c.IsTamed() && Inside(c.transform.position) && BaseAI.IsEnemy(player, c));
        var done = _dungeonDone.Value switch
        {
            DungeonDone.All_Chests_Opened => chestsEmpty,
            DungeonDone.Enemies_Cleared => enemiesGone,
            _ => chestsEmpty && enemiesGone
        };
        if (done)
        {
            SetDone(player, pin, true);
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("AutoPins.MarkedDone", "{0} marked done", pin.m_name));
        }
    }

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    // The name players know a place by, for locations whose prefab name reads differently; matched by prefix, longest first.
    private static readonly (string Prefix, string Name)[] KnownNames =
    [
        ("Mistlands_DvergrBossEntrance", "The Queen"),
        ("Mistlands_DvergrTownEntrance", "Infested Mine"),
        ("Hildir_plainsfortress", "Sealed Tower"),
        ("Vendor_BlackForest", "Haldor"),
        ("FaderLocation", "Fader"),
        ("Hildir_crypt", "Smouldering Tomb"),
        ("BogWitch_Camp", "Bog Witch"),
        ("Hildir_cave", "Howling Cavern"),
        ("Hildir_camp", "Hildir"),
        ("SunkenCrypt", "Sunken Crypt"),
        ("MountainCave", "Frost Cave"),
        ("Dragonqueen", "Moder"),
        ("Eikthyrnir", "Eikthyr"),
        ("GoblinKing", "Yagluth"),
        ("GoblinCamp", "Fuling Village"),
        ("MorgenHole", "Putrid Hole"),
        ("Runestone_", "Runestone"),
        ("GDKing", "The Elder"),
        ("Crypt", "Burial Chambers")
    ];

    private static string Label(Location location, string prefab)
    {
        var label = string.IsNullOrEmpty(location.m_discoverLabel) ? "" : Localization.instance.Localize(location.m_discoverLabel);
        return label.Length > 0 && !label.StartsWith("[") ? label : Readable(prefab);
    }

    // A prefab name as words: a known place's own name, otherwise the name split at underscores and capitals, without its trailing number (StoneTowerRuins08 reads Stone Tower Ruins).
    internal static string Readable(string prefab)
    {
        var known = KnownNames.FirstOrDefault(pair => prefab.StartsWith(pair.Prefix, StringComparison.OrdinalIgnoreCase));
        if (known.Name != null)
        {
            return known.Name;
        }
        var words = System.Text.RegularExpressions.Regex.Replace(prefab.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
        return string.Join(" ", words.Split([' '], StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));
    }

    // Once per world, the names in each category's list are checked against the locations this world's game version actually has, and the log names any that match none and every location no category takes, so lists stay right as the game and other mods add places.
    private void Audit()
    {
        var zones = ZoneSystem.instance;
        if (zones == null || zones == _audited || zones.m_locations == null || zones.m_locations.Count == 0)
        {
            return;
        }
        _audited = zones;
        var known = zones.m_locations.Where(l => l != null && !string.IsNullOrEmpty(l.m_prefabName)).Select(l => l.m_prefabName).Distinct().ToList();
        foreach (var category in _categories)
        {
            var missing = Split(category.Names.Value).Where(name => !known.Any(k => k.StartsWith(name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
            {
                _log.LogInfo($"Auto pins: {category.Kind} Locations names no location of this world: {string.Join(", ", missing)}.");
            }
        }
        var untaken = known.Where(k => !_categories.Any(c => Starts(k, c.Names.Value))).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        _log.LogInfo($"Auto pins: locations no category pins ({untaken.Count}): {string.Join(", ", untaken)}.");
        var items = Split(_resourceItems.Value).Where(name => ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(name) == null).ToList();
        if (items.Count > 0)
        {
            _log.LogInfo($"Auto pins: Resource Items names no item: {string.Join(", ", items)}.");
        }
    }

    private static string Prefab(GameObject go)
    {
        var name = go.name;
        var clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return (clone >= 0 ? name.Substring(0, clone) : name).Trim();
    }

    private static bool Starts(string prefab, string names) => Split(names).Any(n => prefab.StartsWith(n, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Split(string text) => text.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0);
}
