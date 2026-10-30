using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// A ring on the ground around the player showing how far a range setting reaches, opened from the eye beside that setting in this plugin's settings page. It follows the ground's height and the setting's value as it changes, and closes on its own after a while.
internal static class AreaPreview
{
    private const int Segments = 96;
    private static ConfigEntry<float> _seconds;

    // Range settings measured in meters around the player.
    private static readonly HashSet<(string Section, string Key)> Areas =
    [
        ("Auto Pins", "Discover Radius"),
        ("Auto Pins", "Resource Patch Radius"),
        ("Combat HUD", "Combat Enemy Range"),
        ("Party", "Party Key Reach"),
        ("Party HUD", "Face Picture Distance"),
        ("Fuel Helper", "Fuel Radius"),
        ("Cheats: Inventory", "Cycle Chests Range"),
        ("Cheats: Map", "Ship Exploration Radius (Cheat)"),
        ("Cheats: Map", "Walking Exploration Radius (Cheat)"),
        ("Previews", "Area Preview Distance"),
        ("Unlocked Limits", "Max Craft Range"),
        ("Unlocked Limits", "Max Build Range"),
        ("Unlocked Limits", "Max Quick Stack Range"),
        ("Unlocked Limits", "Max Area Pickup Radius"),
        ("Unlocked Limits", "Max Ore Radius"),
        ("Unlocked Limits", "Max Radar Range"),
        ("Cheats: Beyond Maximums", "Max Chest Range Beyond"),
        ("Cheats: Beyond Maximums", "Max Area Pickup Beyond"),
        ("Cheats: Beyond Maximums", "Max Ore Radius Beyond"),
        ("Cheats: Beyond Maximums", "Max Radar Range Beyond"),
        ("Cheats: Portals", "Tames Through Portals Range"),
        ("Tames", "Tame Command Range"),
        ("Building", "Build Camera Range"),
        ("Building", "Repair Nearby Range"),
        ("Doors", "Door Open Distance"),
        ("Alerts", "Production Alert Range"),
        ("Find In Chests", "Find Range")
    ];

    private static LineRenderer _ring;
    private static ConfigEntryBase _entry;
    private static float _until;

    public static void Bind(ConfigFile cfg) =>
        _seconds = cfg.Bind("Previews", "Range Preview Seconds", 20f, new ConfigDescription(
            "How long a range drawn by an eye button on the settings page stays around you.", new AcceptableValueRange<float>(3f, 120f)));


    public static bool IsArea(ConfigEntryBase entry) => Areas.Contains((entry.Definition.Section, entry.Definition.Key));

    public static bool Showing(ConfigEntryBase entry) => _entry == entry && Time.time < _until;

    public static void Toggle(ConfigEntryBase entry)
    {
        if (Showing(entry))
        {
            _until = 0f;
            return;
        }
        _entry = entry;
        _until = Time.time + _seconds.Value;
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        var on = _entry != null && Time.time < _until && player != null && ServerRules.Allows("Previews");
        if (!on)
        {
            if (_ring != null && _ring.gameObject.activeSelf)
            {
                _ring.gameObject.SetActive(false);
            }
            return;
        }
        if (_ring == null)
        {
            Build();
        }
        _ring.gameObject.SetActive(true);
        var radius = System.Convert.ToSingle(_entry.BoxedValue);
        var center = player.transform.position;
        for (var i = 0; i < Segments; i++)
        {
            var angle = i * Mathf.PI * 2f / Segments;
            var point = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            point.y = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out var height) ? height + 0.3f : center.y + 0.3f;
            _ring.SetPosition(i, point);
        }
    }

    private static void Build()
    {
        var go = new GameObject("SeneaLUIUnlocker_AreaPreview");
        Object.DontDestroyOnLoad(go);
        _ring = go.AddComponent<LineRenderer>();
        _ring.loop = true;
        _ring.positionCount = Segments;
        _ring.useWorldSpace = true;
        _ring.widthMultiplier = 0.25f;
        _ring.material = new Material(Shader.Find("Sprites/Default"));
        _ring.startColor = _ring.endColor = new Color(0.85f, 0.72f, 0.43f, 0.9f);
        _ring.numCapVertices = 2;
    }
}
