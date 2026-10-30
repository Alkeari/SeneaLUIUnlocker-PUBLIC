using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Map cheats, offered and off by default: a wider exploration radius on foot and while on a ship, and teleporting to the point under the cursor on the open map.
internal sealed class MapCheats
{
    private const string Section = "Cheats: Map";

    private readonly ConfigEntry<float> _boatRadius;
    private readonly ConfigEntry<float> _walkRadius;
    private readonly ConfigEntry<bool> _teleport;
    private readonly ConfigEntry<KeyboardShortcut> _teleportKey;
    private float _baseRadius = -1f;

    public MapCheats(ConfigFile cfg)
    {
        _boatRadius = cfg.Bind(Section, "Ship Exploration Radius (Cheat)", 100f, new ConfigDescription(
            "Cheat above 100. How far around you the map is revealed while you are on a ship, meters; the game's own is 100, which this keeps by default.", new AcceptableValueRange<float>(100f, 500f)));
        _walkRadius = cfg.Bind(Section, "Walking Exploration Radius (Cheat)", 100f, new ConfigDescription(
            "Cheat above 100. How far around you the map is revealed on foot, meters; the game's own is 100, which this keeps by default.", new AcceptableValueRange<float>(100f, 500f)));
        _teleport = cfg.Bind(Section, "Teleport From Map (Cheat)", false,
            "Cheat, off by default: with the large map open, Map Teleport Key takes you to the point under the cursor.");
        _teleportKey = cfg.Bind(Section, "Map Teleport Key", new KeyboardShortcut(KeyCode.Mouse2, KeyCode.LeftControl),
            "Pressed with the large map open to teleport to the point under the cursor, while Teleport from Map (Cheat) is on.");
    }

    public void Tick()
    {
        var map = Minimap.instance;
        var player = Player.m_localPlayer;
        if (map == null || player == null)
        {
            return;
        }
        if (_baseRadius < 0f)
        {
            _baseRadius = map.m_exploreRadius;
        }
        var onShip = Ship.GetLocalShip() != null;
        var wanted = ServerRules.Allows("ExplorationRadius") ? Mathf.Max(_baseRadius, onShip ? _boatRadius.Value : _walkRadius.Value) : _baseRadius;
        if (!Mathf.Approximately(map.m_exploreRadius, wanted))
        {
            map.m_exploreRadius = wanted;
        }
        if (_teleport.Value && ServerRules.Allows("TeleportFromMap") && map.m_mode == Minimap.MapMode.Large && KeyInput.Down(_teleportKey.Value))
        {
            var point = map.ScreenToWorldPoint(ZInput.pointerPosition);
            point.y = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out var height) ? height + 1f : player.transform.position.y;
            player.TeleportTo(point, player.transform.rotation, true);
            map.SetMapMode(Minimap.MapMode.Small);
        }
    }
}
