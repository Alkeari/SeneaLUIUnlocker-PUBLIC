using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Name Vehicle Key, pressed while looking at a ship or a cart (its hull, rudder or storage), asks for a name kept on the vehicle itself in the world's save, the way a portal keeps its tag: the rudder's and the cart's hover text, the vehicle's storage window title, this plugin's Live Pins and SeneaL UI's live ship and cart marks show it, for every player with this mod. An empty name removes it.
internal static class VehicleNames
{
    private const string Key = "alkeari_vehicle_name";
    private const float FeedSeconds = 1f;

    private static readonly Type LiveMarks = TypeLookup.Find("SeneaLUI.Map.LiveMarks");
    private static readonly FieldInfo TracksField = LiveMarks == null ? null : AccessTools.Field(LiveMarks, "Tracks");
    private static readonly FieldInfo PinShipField = LiveMarks == null ? null : AccessTools.Field(LiveMarks, "PinShip");
    private static readonly FieldInfo PinCartField = LiveMarks == null ? null : AccessTools.Field(LiveMarks, "PinCart");
    private static readonly Type TrackType = LiveMarks == null ? null : AccessTools.Inner(LiveMarks, "Track");
    private static readonly FieldInfo TrackZdo = TrackType == null ? null : AccessTools.Field(TrackType, "Zdo");
    private static readonly FieldInfo TrackPin = TrackType == null ? null : AccessTools.Field(TrackType, "Pin");

    private static readonly Dictionary<Minimap.PinData, string> Renamed = [];

    private static ConfigEntry<bool> _on;
    private static ConfigEntry<KeyboardShortcut> _key;
    private static float _nextFeed;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Inventory", "Vehicle Names", true,
            "Name Vehicle Key names the ship or cart you look at; the name shows on hover, in its storage window's title and on its map pin, for players with this mod.");
        _key = cfg.Bind("Inventory", "Name Vehicle Key", new KeyboardShortcut(KeyCode.L),
            "Asks for a name for the ship or cart you look at, while Vehicle Names is on. An empty name removes it.");
    }

    private static bool On => _on != null && _on.Value && ServerRules.Allows("VehicleNames");

    private static ZNetView ViewOf(Component part)
    {
        if (part == null)
        {
            return null;
        }
        var ship = part.GetComponentInParent<Ship>();
        if (ship != null)
        {
            return ship.m_nview;
        }
        var cart = part.GetComponentInParent<Vagon>();
        return cart == null ? null : cart.m_nview;
    }

    internal static bool Handles(Container chest) => On && ViewOf(chest) != null;

    internal static string NameOf(Component part)
    {
        var view = ViewOf(part);
        return view == null || !view.IsValid() ? "" : view.GetZDO().GetString(Key);
    }

    internal static string Label(Component vehicle, string fallback)
    {
        var name = On ? NameOf(vehicle) : "";
        return name.Length > 0 ? name : fallback;
    }

    public static void Tick()
    {
        Feed();
        var player = Player.m_localPlayer;
        if (!On || player == null || TextInput.IsVisible() || InventoryFeatures.Typing() || !KeyInput.Down(_key.Value))
        {
            return;
        }
        var hover = player.GetHoverObject();
        var view = hover == null ? null : ViewOf(hover.transform);
        if (view == null || !view.IsValid() || !PrivateArea.CheckAccess(view.transform.position))
        {
            return;
        }
        if (!view.IsOwner() && InUse(view))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_inuse");
            return;
        }
        TextInput.instance.RequestText(new Receiver(view), "Vehicle name", 32);
    }

    // Taking ownership moves the vehicle's physics to this client, which would wrench a ship or cart out of another player's hands.
    private static bool InUse(ZNetView view)
    {
        var ship = view.GetComponent<Ship>();
        if (ship != null)
        {
            return ship.m_shipControlls != null && ship.HaveControllingPlayer();
        }
        var cart = view.GetComponent<Vagon>();
        return cart != null && cart.InUse();
    }

    private sealed class Receiver(ZNetView view) : TextReceiver
    {
        public string GetText() => view == null || !view.IsValid() ? "" : view.GetZDO().GetString(Key);

        public void SetText(string text)
        {
            if (view == null || !view.IsValid())
            {
                return;
            }
            view.ClaimOwnership();
            view.GetZDO().Set(Key, (text ?? "").Trim());
            _nextFeed = 0f;
            if (DebugLog.On)
            {
                DebugLog.Write("vehicles", $"named {view.name} at {view.transform.position} \"{text}\"");
            }
        }
    }

    // SeneaL UI names a live ship or cart mark once, after the piece, and never again, so the vehicle's name is written onto that pin here and the piece name put back when the name goes.
    private static void Feed()
    {
        var now = Time.unscaledTime;
        if (now < _nextFeed || TracksField == null || TrackZdo == null || TrackPin == null || PinShipField == null || PinCartField == null)
        {
            return;
        }
        _nextFeed = now + FeedSeconds;
        var tracks = TracksField.GetValue(null) as IDictionary;
        if (tracks == null)
        {
            return;
        }
        var ship = (Minimap.PinType)PinShipField.GetValue(null);
        var cart = (Minimap.PinType)PinCartField.GetValue(null);
        var on = On;
        var live = new HashSet<Minimap.PinData>();
        foreach (var track in tracks.Values)
        {
            var pin = TrackPin.GetValue(track) as Minimap.PinData;
            var zdo = TrackZdo.GetValue(track) as ZDO;
            if (pin == null || zdo == null || pin.m_type != ship && pin.m_type != cart)
            {
                continue;
            }
            live.Add(pin);
            var name = on && zdo.IsValid() ? zdo.GetString(Key) : "";
            if (name.Length > 0)
            {
                if (!Renamed.ContainsKey(pin))
                {
                    Renamed[pin] = pin.m_name;
                }
                SetPinName(pin, name);
            }
            else if (Renamed.TryGetValue(pin, out var original))
            {
                SetPinName(pin, original);
                Renamed.Remove(pin);
            }
        }
        foreach (var pin in new List<Minimap.PinData>(Renamed.Keys))
        {
            if (!live.Contains(pin))
            {
                Renamed.Remove(pin);
            }
        }
    }

    private static void SetPinName(Minimap.PinData pin, string name)
    {
        if (pin.m_name == name)
        {
            return;
        }
        pin.m_name = name;
        if (pin.m_NamePinData != null && pin.m_NamePinData.PinNameText != null)
        {
            pin.m_NamePinData.PinNameText.text = Localization.instance.Localize(name);
        }
        if (DebugLog.On)
        {
            DebugLog.Write("vehicles", $"live mark at {pin.m_pos} labeled \"{name}\"");
        }
    }

    [HarmonyPatch(typeof(ShipControlls), nameof(ShipControlls.GetHoverText))]
    internal static class RudderHoverPatch
    {
        private static void Postfix(ShipControlls __instance, ref string __result)
        {
            var name = On ? NameOf(__instance) : "";
            if (name.Length > 0)
            {
                __result = $"<b>{name}</b>\n{__result}";
            }
        }
    }

    [HarmonyPatch(typeof(Vagon), nameof(Vagon.GetHoverText))]
    internal static class CartHoverPatch
    {
        private static void Postfix(Vagon __instance, ref string __result)
        {
            var name = On ? NameOf(__instance) : "";
            if (name.Length > 0)
            {
                __result = $"<b>{name}</b>\n{__result}";
            }
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    internal static class StorageHoverPatch
    {
        private static void Postfix(Container __instance, ref string __result)
        {
            var name = Handles(__instance) ? NameOf(__instance) : "";
            if (name.Length > 0)
            {
                __result = $"<b>{name}</b>\n{__result}";
            }
        }
    }

    // The window takes its title from the inventory's name, which the game never saves, so it is set from the vehicle just before it opens, and put back to the piece's own name once the vehicle has none.
    [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
    internal static class OpenPatch
    {
        private static void Prefix(Container __instance)
        {
            if (!Handles(__instance) || __instance.GetInventory() == null)
            {
                return;
            }
            var name = NameOf(__instance);
            __instance.GetInventory().m_name = name.Length > 0 ? name : __instance.m_name;
        }
    }
}
