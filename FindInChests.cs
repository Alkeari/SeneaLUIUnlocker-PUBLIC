using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The chat command /find <item> lists the nearby chests you may open that hold a matching item, nearest first with how many, and makes them glow for a while so you can walk to them. Chests you may not open (private ones, or inside a ward that does not admit you) are left out, since the game would not show you what they hold.
internal sealed class FindInChests
{
    private const string Section = "Find In Chests";
    private static readonly Collider[] Near = new Collider[2048];
    private static readonly int Mask = LayerMask.GetMask("piece", "piece_nonsolid");
    private static readonly Color Glow = new(0.98f, 0.8f, 0.25f);

    private readonly ConfigEntry<bool> _on;
    private readonly ConfigEntry<float> _range;
    private readonly ConfigEntry<float> _seconds;
    private readonly List<GameObject> _lit = [];
    private float _until;
    private bool _added;

    public FindInChests(ConfigFile cfg)
    {
        _on = cfg.Bind(Section, "Find Command", true,
            "Typing /find and an item name in the chat lists the nearby chests holding it and makes them glow.");
        _range = cfg.Bind(Section, "Find Range", 40f, new ConfigDescription(
            "How far from you, in meters, /find looks for chests.", new AcceptableValueRange<float>(5f, 100f)));
        _seconds = cfg.Bind(Section, "Find Glow Seconds", 15f, new ConfigDescription(
            "How long the chests /find names keep glowing.", new AcceptableValueRange<float>(2f, 120f)));
    }

    public void Tick(float now)
    {
        if (!_added && Chat.instance != null)
        {
            _added = true;
            _ = new Terminal.ConsoleCommand("find", "[item] List the nearby chests holding an item, and make them glow.", args => Find(args.ArgsAll));
        }
        if (_lit.Count == 0)
        {
            return;
        }
        var on = now < _until;
        foreach (var chest in _lit)
        {
            if (chest == null)
            {
                continue;
            }
            if (on)
            {
                // Pulses so a glowing chest reads as a marker rather than the support colors of the hammer.
                var pulse = 0.35f + 0.25f * Mathf.Sin(now * 5f);
                MaterialMan.instance.SetValue(chest, ShaderProps._EmissionColor, Glow * pulse);
            }
            else
            {
                MaterialMan.instance.ResetValue(chest, ShaderProps._EmissionColor);
            }
        }
        if (!on)
        {
            _lit.Clear();
        }
    }

    private void Find(string term)
    {
        var player = Player.m_localPlayer;
        term = (term ?? "").Trim();
        if (!_on.Value || !ServerRules.Allows("FindInChests") || player == null)
        {
            return;
        }
        if (term.Length == 0)
        {
            Line("Use /find and part of an item's name, for example /find iron");
            return;
        }
        var at = player.transform.position;
        var count = Physics.OverlapSphereNonAlloc(at, _range.Value, Near, Mask);
        var seen = new HashSet<Container>();
        var found = new List<(Container Chest, float Distance, int Amount, string Names)>();
        for (var i = 0; i < count; i++)
        {
            var chest = Near[i] == null ? null : Near[i].GetComponentInParent<Container>();
            if (chest == null || !seen.Add(chest) || chest.m_nview == null || !chest.m_nview.IsValid() || chest.GetComponent<TombStone>() != null
                || !chest.CheckAccess(player.GetPlayerID()) || chest.m_checkGuardStone && !PrivateArea.CheckAccess(chest.transform.position, 0f, false))
            {
                continue;
            }
            var items = chest.GetInventory().GetAllItems()
                .Where(item => Localization.instance.Localize(item.m_shared.m_name).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (items.Count == 0)
            {
                continue;
            }
            var names = string.Join(", ", items.GroupBy(item => Localization.instance.Localize(item.m_shared.m_name)).Select(group => $"{group.Key} {group.Sum(item => item.m_stack)}"));
            found.Add((chest, Vector3.Distance(at, chest.transform.position), items.Sum(item => item.m_stack), names));
        }
        foreach (var chest in _lit.Where(chest => chest != null))
        {
            MaterialMan.instance.ResetValue(chest, ShaderProps._EmissionColor);
        }
        _lit.Clear();
        if (found.Count == 0)
        {
            Line($"No chest within {_range.Value:0} m holds \"{term}\".");
            return;
        }
        Line($"{found.Count} chest{(found.Count == 1 ? "" : "s")} within {_range.Value:0} m hold \"{term}\", {found.Sum(entry => entry.Amount)} in all:");
        foreach (var (chest, distance, _, names) in found.OrderBy(entry => entry.Distance).Take(8))
        {
            Line($"  {Localization.instance.Localize(chest.m_name)} {distance:0} m: {names}");
            _lit.Add(chest.gameObject);
        }
        _lit.AddRange(found.OrderBy(entry => entry.Distance).Skip(8).Select(entry => entry.Chest.gameObject));
        _until = Time.time + _seconds.Value;
        if (DebugLog.On)
        {
            DebugLog.Write("find", $"/find {term}: {found.Count} chests, {found.Sum(entry => entry.Amount)} items");
        }
    }

    private static void Line(string text)
    {
        if (Chat.instance != null)
        {
            Chat.instance.AddString(text);
        }
    }
}
