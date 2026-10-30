using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The Death Report: what killed you, how, where and when, how hard the last hit was, and which skills you lost, shown after you come back and on the Esc menu. The Esc menu also shows what dying now would cost: the skill loss the game takes (Skills.LowerAllSkills at m_DeathLowerFactor times the world's rate), or how long the grace after a recent death still spares your skills.
// The last report is kept with the character (m_customData) so it is still there after logging out.
internal static class DeathReport
{
    private const string Section = "Death";
    private const string DataKey = "alkeari.senealuiunlocker.lastdeath";

    private static ConfigEntry<bool> _report;
    private static ConfigEntry<bool> _preview;
    private static ConfigEntry<float> _seconds;
    private static ConfigEntry<int> _listed;
    private static ConfigEntry<bool> _levels;
    private const string Sample = "<b>Last death</b>  21:14\nKilled by Troll (Blunt)\nLast hit: 86 damage\nWhere: Black Forest, 412, -1180\nSkills lost:\nRun: level 32.4 to 30.8 (1.6 levels lost)\n\n<b>If you died now</b>, each skill would lose 5% of its level:\nRun: level 30.8 to 29.3 (1.5 levels lost)";
    private static Dictionary<Skills.SkillType, float> _before;
    private static string _pending;
    private static float _showUntil;
    private static readonly ThemedCard Card = new ThemedCard(new Vector2(0.28f, 0.75f), new Vector2(0f, 1f), Vector2.zero, 32005, 14f)
        .Element("DeathReport", "Death Report", Sample, ElementMode.Other);

    public static void Bind(ConfigFile cfg)
    {
        _report = cfg.Bind(Section, "Death Report", true,
            "After you die, show what killed you, how and where, and the skills you lost; it stays on the Esc menu until your next death.");
        _preview = cfg.Bind(Section, "Death Preview", true,
            "On the Esc menu, show the skills you would lose if you died now, or how long recent death still spares them.");
        _seconds = cfg.Bind(Section, "Death Report Seconds", 20f, new ConfigDescription(
            "How long the Death Report stays on screen after you come back; it is on the Esc menu either way.", new AcceptableValueRange<float>(0f, 120f)));
        _listed = cfg.Bind(Section, "Death Skills Listed", 6, new ConfigDescription(
            "Most skills the Death Report and If You Died Now list, the largest loss first; 0 lists none and gives only the share each skill loses.", new AcceptableValueRange<int>(0, 15)));
        _levels = cfg.Bind(Section, "Death Shows Levels", true,
            "List each skill with its level before and after the loss as well as the levels lost; off lists only the levels lost.");
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (player == null)
        {
            Card.Show(null);
            return;
        }
        if (_pending != null && !player.IsDead())
        {
            player.m_customData[DataKey] = _pending;
            _pending = null;
            _showUntil = Time.time + _seconds.Value;
        }
        var lines = new List<string>();
        // On the Esc menu, unless SeneaL UI's settings window is open over it.
        var menu = Menu.IsVisible() && !SeneaLHud.SettingsOpen;
        if (_report.Value && ServerRules.Allows("DeathReport") && (menu || Time.time < _showUntil) && player.m_customData.TryGetValue(DataKey, out var last))
        {
            lines.Add(last);
        }
        if (_preview.Value && ServerRules.Allows("DeathReport") && menu)
        {
            lines.Add(Preview(player));
        }
        Card.Show(lines.Count == 0 ? null : string.Join("\n\n", lines));
    }

    private static string Preview(Player player)
    {
        if (player.m_timeSinceDeath <= player.m_hardDeathCooldown)
        {
            var left = TimeSpan.FromSeconds(Mathf.Max(0f, player.m_hardDeathCooldown - player.m_timeSinceDeath));
            return $"<b>If you died now</b>, no skill would lose a level: a recent death spares them for another {(int)left.TotalMinutes}:{left.Seconds:00}";
        }
        var factor = player.GetSkills().m_DeathLowerFactor * Game.m_skillReductionRate;
        var losses = player.GetSkills().m_skillData.Values
            .Select(skill => (skill, after: DadsSkillsCompat.After(skill.m_level, skill.m_level * (1f - factor))))
            .Where(pair => pair.skill.m_level - pair.after >= 0.05f)
            .OrderByDescending(pair => pair.skill.m_level - pair.after)
            .Take(_listed.Value)
            .Select(pair => Loss(pair.skill.m_info.m_skill, pair.skill.m_level, pair.after))
            .ToList();
        var head = $"<b>If you died now</b>, each skill would lose {Mathf.RoundToInt(factor * 100f)}% of its level";
        if (_listed.Value == 0)
        {
            return head;
        }
        return head + ":\n" + (losses.Count > 0 ? string.Join("\n", losses) : "No skill is high enough to lose a level");
    }

    // One skill's loss in plain words: its level before and after and the levels lost, or the levels lost alone.
    private static string Loss(Skills.SkillType type, float before, float after)
    {
        var lost = (before - after).ToString("0.#", CultureInfo.InvariantCulture);
        var unit = lost == "1" ? "level" : "levels";
        return _levels.Value
            ? $"{SkillName(type)}: level {before.ToString("0.#", CultureInfo.InvariantCulture)} to {after.ToString("0.#", CultureInfo.InvariantCulture)} ({lost} {unit} lost)"
            : $"{SkillName(type)}: {lost} {unit} lost";
    }

    private static string SkillName(Skills.SkillType type) => Localization.instance.Localize("$skill_" + type.ToString().ToLowerInvariant());

    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class DeathPatch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || _report == null || !_report.Value || !ServerRules.Allows("DeathReport"))
            {
                return;
            }
            _before = __instance.GetSkills().m_skillData.ToDictionary(pair => pair.Key, pair => pair.Value.m_level);
        }

        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || _before == null)
            {
                return;
            }
            var hit = __instance.m_lastHit;
            var attacker = hit?.GetAttacker();
            var who = attacker != null ? Localization.instance.Localize(attacker.m_name) : null;
            var how = hit == null || hit.m_hitType == HitData.HitType.Undefined ? "Unknown" : SettingsPage.Pretty(hit.m_hitType.ToString());
            var pos = __instance.transform.position;
            var biome = Heightmap.FindBiome(pos);
            var lost = __instance.GetSkills().m_skillData
                .Select(pair => (Type: pair.Key, Was: _before.TryGetValue(pair.Key, out var was) ? was : pair.Value.m_level, Now: pair.Value.m_level))
                .Where(pair => pair.Was - pair.Now >= 0.05f)
                .OrderByDescending(pair => pair.Was - pair.Now)
                .Take(_listed.Value)
                .Select(pair => Loss(pair.Type, pair.Was, pair.Now))
                .ToList();
            var lines = new List<string> { "<b>Last death</b>  " + DateTime.Now.ToString("HH:mm") };
            lines.Add(who != null ? $"Killed by {who} ({how})" : $"Cause: {how}");
            if (hit != null)
            {
                lines.Add($"Last hit: {hit.GetTotalDamage().ToString("0", CultureInfo.InvariantCulture)} damage");
            }
            lines.Add($"Where: {SettingsPage.Pretty(biome.ToString())}, {Mathf.RoundToInt(pos.x)}, {Mathf.RoundToInt(pos.z)}");
            if (_listed.Value > 0)
            {
                lines.Add(lost.Count > 0 ? "Skills lost:\n" + string.Join("\n", lost) : "No skills lost");
            }
            _pending = string.Join("\n", lines);
            _before = null;
        }
    }
}
