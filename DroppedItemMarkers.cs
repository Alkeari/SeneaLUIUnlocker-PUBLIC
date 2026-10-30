using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Dropped Item Markers gives what the local player drops from an inventory a map pin named after it, and with Dropped Item Glow a glow, until anyone picks it up, it despawns or Dropped Item Marker Minutes pass. The pin is never saved and nothing is written to the world, so no other player sees any of it.
internal static class DroppedItemMarkers
{
    private const string Section = "Map";
    private const string Rule = "DroppedItemMarkers";
    private const Minimap.PinType Icon = Minimap.PinType.Icon3;
    private static readonly Color Glow = new(0.98f, 0.8f, 0.25f);

    private sealed class Marker
    {
        public ItemDrop Item;
        public ZDOID Id;
        public Minimap.PinData Pin;
        public string Name;
        public float Until;
        public bool Lit;
    }

    private static ConfigEntry<bool> _on;
    private static ConfigEntry<int> _minutes;
    private static ConfigEntry<bool> _glow;
    private static readonly List<Marker> Markers = [];
    private static Minimap _map;
    private static bool _dropping;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind(Section, "Dropped Item Markers", true,
            "Items you drop from an inventory get a map pin named after them, seen only by you and never saved, until anyone picks them up, they despawn or Dropped Item Marker Minutes pass.");
        _minutes = cfg.Bind(Section, "Dropped Item Marker Minutes", 30, new ConfigDescription(
            "How many minutes an item you dropped keeps its Dropped Item Markers pin and glow.", new AcceptableValueRange<int>(1, 240)));
        _glow = cfg.Bind(Section, "Dropped Item Glow", true,
            "Items with a Dropped Item Markers pin also glow while it lasts, so they stand out on the ground.");
    }

    private static bool On => _on != null && _on.Value && ServerRules.Allows(Rule);

    public static void Tick()
    {
        var map = Minimap.instance;
        if (map != _map)
        {
            // The pins went with the map of the world just left.
            Markers.Clear();
            _map = map;
        }
        if (Markers.Count == 0)
        {
            return;
        }
        var active = On && map != null && ZDOMan.instance != null && ZNetScene.instance != null;
        var now = Time.time;
        var glow = _glow.Value && MaterialMan.instance != null;
        var pulse = 0.55f + 0.25f * Mathf.Sin(now * 5f);
        for (var i = Markers.Count - 1; i >= 0; i--)
        {
            var marker = Markers[i];
            if (!active)
            {
                Remove(i, "turned off");
                continue;
            }
            if (now >= marker.Until)
            {
                Remove(i, "time up");
                continue;
            }
            if (marker.Item == null)
            {
                // Gone from this client either because its area unloaded, which keeps the ZDO, or because it was picked up or despawned, which destroys it everywhere.
                var zdo = ZDOMan.instance.GetZDO(marker.Id);
                if (zdo == null)
                {
                    Remove(i, "picked up or despawned");
                    continue;
                }
                var instance = ZNetScene.instance.FindInstance(marker.Id);
                if (instance == null)
                {
                    continue;
                }
                marker.Item = instance.GetComponent<ItemDrop>();
                marker.Lit = false;
                if (marker.Item == null)
                {
                    continue;
                }
            }
            marker.Pin.m_pos = marker.Item.transform.position;
            if (glow)
            {
                MaterialMan.instance.SetValue(marker.Item.gameObject, ShaderProps._EmissionColor, Glow * pulse);
                marker.Lit = true;
            }
            else if (marker.Lit)
            {
                Unlight(marker);
            }
        }
    }

    private static void Add(ItemDrop item)
    {
        var map = Minimap.instance;
        if (!On || map == null || item == null || item.m_nview == null || !item.m_nview.IsValid())
        {
            return;
        }
        if (map != _map)
        {
            Markers.Clear();
            _map = map;
        }
        var data = item.m_itemData;
        var name = Localization.instance.Localize(data.m_shared.m_name);
        var label = data.m_stack > 1 ? $"Dropped: {name} x{data.m_stack}" : $"Dropped: {name}";
        var pin = map.AddPin(item.transform.position, Icon, label, false, false);
        Markers.Add(new Marker { Item = item, Id = item.m_nview.GetZDO().m_uid, Pin = pin, Name = label, Until = Time.time + _minutes.Value * 60f });
        if (DebugLog.On)
        {
            DebugLog.Write("pins", $"dropped item marker \"{label}\" at {item.transform.position} for {_minutes.Value} min");
        }
    }

    private static void Remove(int index, string why)
    {
        var marker = Markers[index];
        if (marker.Lit)
        {
            Unlight(marker);
        }
        if (_map != null)
        {
            _map.RemovePin(marker.Pin);
        }
        Markers.RemoveAt(index);
        if (DebugLog.On)
        {
            DebugLog.Write("pins", $"dropped item marker \"{marker.Name}\" removed: {why}");
        }
    }

    private static void Unlight(Marker marker)
    {
        if (marker.Item != null && MaterialMan.instance != null)
        {
            MaterialMan.instance.ResetValue(marker.Item.gameObject, ShaderProps._EmissionColor);
        }
        marker.Lit = false;
    }

    // Humanoid.DropItem is the game's drop from an inventory by hand; other drops (loot, crafting, SeneaL UI's spills) call ItemDrop.DropItem without it.
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem))]
    internal static class DropPatch
    {
        private static void Prefix(Humanoid __instance) => _dropping = Player.m_localPlayer != null && __instance == Player.m_localPlayer;

        private static void Finalizer() => _dropping = false;
    }

    // ItemDrop.OnPlayerDrop is small enough to be inlined into Humanoid.DropItem, where a patch on it never runs, so the item is taken from the ItemDrop.DropItem call inside instead.
    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.DropItem))]
    internal static class PlayerDropPatch
    {
        private static void Postfix(ItemDrop __result)
        {
            if (_dropping && __result != null)
            {
                Add(__result);
            }
        }
    }
}
