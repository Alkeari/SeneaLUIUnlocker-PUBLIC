using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Tame Command Key calls every tame near you to follow, or, when all of them already follow you, tells them all to stay, through the game's own command (the one a tame gets when you press Use on it).
// The hover text of a tame or a creature being tamed adds what the game tracks but never shows: how long it stays fed, how long taming has left while it is fed and calm, how long a young one takes to grow up, and its love points and pregnancy. Every value is read from the creature's own saved state, the same the game decides by.
internal static class TameInfo
{
    private static ConfigEntry<bool> _on;
    private static ConfigEntry<KeyboardShortcut> _commandKey;
    private static ConfigEntry<float> _commandRange;
    private static readonly List<Character> Near = [];

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Tames", "Tame Details", true,
            "Hovering a tame or a creature being tamed shows how long it stays fed, the taming time left, the time until a young one grows up, and its love points and pregnancy.");
        _commandKey = cfg.Bind("Tames", "Tame Command Key", new KeyboardShortcut(KeyCode.U, KeyCode.LeftAlt),
            "Calls every tame within Tame Command Range to follow you, or tells them all to stay when they all follow you already.");
        _commandRange = cfg.Bind("Tames", "Tame Command Range", 15f, new ConfigDescription(
            "How far from you, in meters, Tame Command Key reaches.", new AcceptableValueRange<float>(3f, 40f)));
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (player == null || player.IsDead() || InventoryFeatures.Typing() || !KeyInput.Pressed(_commandKey.Value) || !ServerRules.Allows("TameCommand"))
        {
            return;
        }
        Near.Clear();
        Character.GetCharactersInRange(player.transform.position, _commandRange.Value, Near);
        var tames = Near.Select(c => c.GetComponent<Tameable>()).Where(t => t != null && t.m_commandable && t.m_character != null && t.m_character.IsTamed() && t.m_nview != null && t.m_nview.IsValid()).ToList();
        if (tames.Count == 0)
        {
            player.Message(MessageHud.MessageType.Center, Lang.T("TameInfo.NoTameNearbyTakesCommands", "No tame nearby takes commands"));
            return;
        }
        bool Follows(Tameable t) => t.m_nview.GetZDO().GetString(ZDOVars.s_follow) == player.GetPlayerName();
        var follow = tames.Any(t => !Follows(t));
        foreach (var tame in tames.Where(t => Follows(t) != follow))
        {
            tame.Command(player, false);
        }
        player.Message(MessageHud.MessageType.Center, follow ? Lang.N("TameInfo.Follow", tames.Count, "{0} tame follows you", "{0} tames follow you") : Lang.N("TameInfo.Stay", tames.Count, "{0} tame stays", "{0} tames stay"));
        if (DebugLog.On)
        {
            DebugLog.Write("tames", $"{(follow ? "follow" : "stay")} for {tames.Count} tames");
        }
    }

    internal static string Time(double seconds)
    {
        seconds = Math.Max(0d, seconds);
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1d ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1d ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    internal static List<string> Lines(Tameable tame)
    {
        var lines = new List<string>();
        var zdo = tame.m_nview == null || !tame.m_nview.IsValid() ? null : tame.m_nview.GetZDO();
        if (zdo == null || ZNet.instance == null)
        {
            return lines;
        }
        var now = ZNet.instance.GetTime();
        var fedAt = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
        var fedLeft = fedAt == 0L ? 0d : tame.m_fedDuration - (now - new DateTime(fedAt)).TotalSeconds;
        lines.Add(fedLeft > 0d ? $"Fed for {Time(fedLeft)}" : "Hungry");
        if (!tame.IsTamed())
        {
            var left = zdo.GetFloat(ZDOVars.s_tameTimeLeft, tame.m_tamingTime);
            lines.Add(fedLeft > 0d ? $"Tamed in {Time(left)} while fed and calm" : $"Taming paused, {Time(left)} left once fed");
            return lines;
        }
        if (tame.GetComponent<Growup>() is { } grow && tame.GetComponent<BaseAI>() is { } ai)
        {
            lines.Add($"Grows up in {Time(grow.m_growTime - ai.GetTimeSinceSpawned().TotalSeconds)}");
        }
        if (tame.GetComponent<Procreation>() is { } procreation)
        {
            var pregnant = zdo.GetLong(ZDOVars.s_pregnant, 0L);
            lines.Add(pregnant != 0L
                ? $"Offspring in {Time(procreation.m_pregnancyDuration - (now - new DateTime(pregnant)).TotalSeconds)}"
                : $"Love {zdo.GetInt(ZDOVars.s_lovePoints)}/{procreation.m_requiredLovePoints}");
        }
        return lines;
    }

    [HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
    internal static class HoverPatch
    {
        private static void Postfix(Tameable __instance, ref string __result)
        {
            if (_on == null || !_on.Value || string.IsNullOrEmpty(__result) || !ServerRules.Allows("TameInfo"))
            {
                return;
            }
            var lines = Lines(__instance);
            if (lines.Count == 0)
            {
                return;
            }
            // After the name line, before the key hints.
            var cut = __result.IndexOf('\n');
            var info = "\n<size=85%>" + string.Join("\n", lines) + "</size>";
            __result = cut < 0 ? __result + info : __result.Substring(0, cut) + info + __result.Substring(cut);
        }
    }

    // Offspring such as a wolf cub or a piglet carry Growup but no Tameable, so the game gives them no hover text at all; a tamed one gets its name and how long until it grows up.
    [HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
    internal static class YoungHoverPatch
    {
        private static void Postfix(Character __instance, ref string __result)
        {
            if (_on == null || !_on.Value || !string.IsNullOrEmpty(__result) || !__instance.IsTamed() || __instance.GetComponent<Tameable>() != null || !ServerRules.Allows("TameInfo"))
            {
                return;
            }
            if (__instance.GetComponent<Growup>() is not { } grow || __instance.GetComponent<BaseAI>() is not { } ai)
            {
                return;
            }
            __result = Localization.instance.Localize(__instance.GetHoverName()) + $"\n<size=85%>Grows up in {Time(grow.m_growTime - ai.GetTimeSinceSpawned().TotalSeconds)}</size>";
        }
    }
}
