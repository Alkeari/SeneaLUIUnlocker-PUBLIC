using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Sign Pins puts a saved map pin at a sign, named after its text, when you write the sign (and, with Sign Pins From Hover, when you look at one), so labeled storage and waypoints show on the map. A changed text renames the pin and an emptied sign loses it.
internal static class SignPins
{
    private const float Match = 1f;
    private const int MaxName = 40;
    private static readonly Regex Tags = new("<[^>]*>");

    private static ConfigEntry<bool> _on;
    private static ConfigEntry<bool> _fromHover;
    private static ConfigEntry<Minimap.PinType> _icon;
    private static Sign _lastHovered;
    private static string _lastHoveredText;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Map", "Sign Pins", true,
            "Writing a sign puts a saved map pin at it named after its text, renamed when the text changes and removed when it is emptied.");
        _fromHover = cfg.Bind("Map", "Sign Pins From Hover", false,
            "Also pins every sign you look at, not only the ones you write; off by default because it fills the map with every sign you pass.");
        _icon = cfg.Bind("Map", "Sign Pin Icon", Minimap.PinType.Icon3,
            "The map icon of pins placed by Sign Pins.");
    }

    private static bool On => _on != null && _on.Value && ServerRules.Allows("SignPins");

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (!On || !_fromHover.Value || player == null || Minimap.instance == null)
        {
            _lastHovered = null;
            return;
        }
        var hover = player.GetHoverObject();
        var sign = hover == null ? null : hover.GetComponentInParent<Sign>();
        if (sign == null || !sign.m_isViewable || sign.m_nview == null || !sign.m_nview.IsValid())
        {
            _lastHovered = null;
            return;
        }
        var text = sign.m_nview.GetZDO().GetString(ZDOVars.s_text);
        if (sign == _lastHovered && text == _lastHoveredText)
        {
            return;
        }
        _lastHovered = sign;
        _lastHoveredText = text;
        Place(sign.transform.position, text, "hover");
    }

    private static void Place(Vector3 position, string text, string source)
    {
        var name = Tags.Replace(text ?? "", "").Trim();
        if (name.Length > MaxName)
        {
            name = name.Substring(0, MaxName).Trim();
        }
        var existing = Minimap.instance.m_pins.Find(pin => pin.m_save && pin.m_type == _icon.Value && Vector3.Distance(pin.m_pos, position) <= Match);
        if (existing != null && existing.m_name == name)
        {
            return;
        }
        if (existing != null)
        {
            Minimap.instance.RemovePin(existing);
        }
        if (name.Length > 0)
        {
            Minimap.instance.AddPin(position, _icon.Value, name, true, false);
        }
        if (DebugLog.On)
        {
            DebugLog.Write("pins", name.Length > 0
                ? $"sign pin \"{name}\" at {position} from {source}{(existing == null ? "" : $", replacing \"{existing.m_name}\"")}"
                : $"sign pin \"{existing?.m_name}\" removed at {position} from {source}");
        }
    }

    // Sign.SetText refuses silently without ward access, so the pin follows the text the sign actually holds afterwards rather than the text typed.
    [HarmonyPatch(typeof(Sign), nameof(Sign.SetText))]
    internal static class SetTextPatch
    {
        private static void Postfix(Sign __instance, string text)
        {
            if (!On || Minimap.instance == null || __instance == null || __instance.m_nview == null || !__instance.m_nview.IsValid())
            {
                return;
            }
            var held = __instance.m_nview.GetZDO().GetString(ZDOVars.s_text);
            if (held != text)
            {
                return;
            }
            if (__instance == _lastHovered)
            {
                _lastHoveredText = held;
            }
            Place(__instance.transform.position, held, "writing");
        }
    }
}
