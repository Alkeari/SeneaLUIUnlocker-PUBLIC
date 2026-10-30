using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Pin Looked-At Key drops a map pin on whatever is under the crosshair, named after it (an ore vein, a tree, a chest, a building), so marking a find never needs the map opened. A second press on the same thing removes that pin.
internal sealed class PinLookedAt
{
    private static readonly int Mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle", "item");
    private static readonly Regex Tags = new("<[^>]*>");

    private readonly ConfigEntry<bool> _on;
    private readonly ConfigEntry<KeyboardShortcut> _key;
    private readonly ConfigEntry<Minimap.PinType> _icon;

    public PinLookedAt(ConfigFile cfg)
    {
        _on = cfg.Bind("Map", "Pin Looked-At", true,
            "Pin Looked-At Key drops a map pin named after what is under the crosshair; pressed again on the same thing it removes the pin.");
        _key = cfg.Bind("Map", "Pin Looked-At Key", new KeyboardShortcut(KeyCode.P),
            "Pins what is under the crosshair, while Pin Looked-At is on and no building tool is in hand (building mods use the same keys).");
        _icon = cfg.Bind("Map", "Pin Looked-At Icon", Minimap.PinType.Icon3,
            "The map icon of pins dropped with Pin Looked-At Key.");
    }

    public void Tick()
    {
        var player = Player.m_localPlayer;
        if (!_on.Value || player == null || player.IsDead() || player.InPlaceMode() || Minimap.instance == null || GameCamera.instance == null || InventoryFeatures.Typing()
            || !KeyInput.Down(_key.Value) || !ServerRules.Allows("PinLookedAt"))
        {
            return;
        }
        var camera = GameCamera.instance.transform;
        if (!Physics.Raycast(camera.position, camera.forward, out var hit, 200f, Mask))
        {
            player.Message(MessageHud.MessageType.Center, Lang.T("PinLookedAt.NothingToPinThere", "Nothing to pin there"));
            return;
        }
        var point = hit.point;
        var name = Name(hit.collider);
        if (Minimap.instance.RemovePin(point, 3f))
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.T("PinLookedAt.PinRemoved", "Pin removed"));
            return;
        }
        Minimap.instance.AddPin(point, _icon.Value, name, true, false);
        player.Message(MessageHud.MessageType.TopLeft, Lang.F("PinLookedAt.Pinned", "Pinned {0}", name));
        if (DebugLog.On)
        {
            DebugLog.Write("pins", $"pinned {name} at {point}");
        }
    }

    private static string Name(Collider collider)
    {
        var hoverable = collider.GetComponentInParent<Hoverable>();
        var name = hoverable == null ? "" : Localization.instance.Localize(hoverable.GetHoverName());
        if (string.IsNullOrWhiteSpace(name) && collider.GetComponentInParent<Piece>() is { } piece)
        {
            name = Localization.instance.Localize(piece.m_name);
        }
        name = Tags.Replace(name ?? "", "").Trim();
        return name.Length == 0 ? "Marked" : name;
    }
}
