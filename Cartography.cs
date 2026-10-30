using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// What reading a cartography table brings in. The game adds every other player's shared pins (Minimap.AddSharedMapData); afterwards the ones not wanted are taken off again: all of them, or those farther from you than a chosen distance. The explored map always comes in as the game has it.
internal static class Cartography
{
    private const string Section = "Cartography";

    private static ConfigEntry<bool> _receive;
    private static ConfigEntry<float> _within;

    public static void Bind(ConfigFile cfg)
    {
        _receive = cfg.Bind(Section, "Take Pins From Tables", true, "Reading a cartography table adds the pins other players shared there, as the game does.");
        _within = cfg.Bind(Section, "Take Pins Within Meters", 0f, new ConfigDescription(
            "Only shared pins within this distance of you are added; 0 = every one.", new AcceptableValueRange<float>(0f, 10000f)));
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddSharedMapData))]
    internal static class ReadPatch
    {
        private static void Postfix(Minimap __instance)
        {
            var player = Player.m_localPlayer;
            if (player == null || !ServerRules.Allows("Cartography") || (_receive.Value && _within.Value <= 0f))
            {
                return;
            }
            var me = player.GetPlayerID();
            var at = player.transform.position;
            foreach (var pin in __instance.m_pins.Where(pin => pin.m_ownerID != 0L && pin.m_ownerID != me).ToList())
            {
                if (!_receive.Value || Vector3.Distance(pin.m_pos, at) > _within.Value)
                {
                    __instance.RemovePin(pin);
                }
            }
        }
    }
}
