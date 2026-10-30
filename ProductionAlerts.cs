using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// A message when production near you finishes, so nobody walks back to check: a smelter, kiln, furnace, windmill, spinning wheel or refinery emptying its queue, a fermenter turning ready, and food done on a cooking station or oven (and burnt, where it can burn). Only changes seen while the station was in range count, so loading into a base full of finished work stays quiet.
internal sealed class ProductionAlerts
{
    private const string Section = "Alerts";

    private readonly ConfigEntry<bool> _smelting;
    private readonly ConfigEntry<bool> _fermenting;
    private readonly ConfigEntry<bool> _cooking;
    private readonly ConfigEntry<float> _range;
    private readonly Dictionary<ZDOID, string> _seen = [];
    private float _next;

    public ProductionAlerts(ConfigFile cfg)
    {
        _smelting = cfg.Bind(Section, "Smelting Done Alert", true,
            "A message when a smelter, kiln, furnace, windmill, spinning wheel or refinery within range finishes everything queued in it.");
        _fermenting = cfg.Bind(Section, "Fermenting Done Alert", true,
            "A message when a fermenter within range turns ready to tap.");
        _cooking = cfg.Bind(Section, "Cooking Done Alert", true,
            "A message when food on a cooking station or oven within range is done, and again if it burns.");
        _range = cfg.Bind(Section, "Production Alert Range", 60f, new ConfigDescription(
            "How far from you, in meters, a station's production is watched.", new AcceptableValueRange<float>(10f, 150f)));
    }

    private static IEnumerable<Component> Stations()
    {
        foreach (var smelter in SceneObjects.Live(SceneObjects.Smelters))
        {
            yield return smelter;
        }
        foreach (var fermenter in SceneObjects.Live(SceneObjects.Fermenters))
        {
            yield return fermenter;
        }
        foreach (var cooking in SceneObjects.Live(SceneObjects.CookingStations))
        {
            yield return cooking;
        }
    }

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        if (player == null || now < _next || !ServerRules.Allows("ProductionAlerts") || !_smelting.Value && !_fermenting.Value && !_cooking.Value)
        {
            if (player == null)
            {
                _seen.Clear();
            }
            return;
        }
        _next = now + 1f;
        var at = player.transform.position;
        var range = _range.Value * _range.Value;
        var present = new HashSet<ZDOID>();
        foreach (var piece in Stations())
        {
            var view = piece == null ? null : piece.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || (piece.transform.position - at).sqrMagnitude > range)
            {
                continue;
            }
            var zdo = view.GetZDO();
            string state = null;
            string done = null;
            if (_smelting.Value && piece is Smelter smelter)
            {
                state = zdo.GetInt(ZDOVars.s_queued) > 0 ? "busy" : "idle";
                done = state == "idle" ? $"{Name(smelter.m_name)} finished its queue" : null;
            }
            else if (_fermenting.Value && piece is Fermenter fermenter)
            {
                state = fermenter.GetStatus().ToString();
                done = state == "Ready" ? $"{Name(fermenter.m_name)} is ready" : null;
            }
            else if (_cooking.Value && piece is CookingStation cooking)
            {
                var ready = 0;
                var burnt = 0;
                for (var slot = 0; slot < cooking.m_slots.Length; slot++)
                {
                    cooking.GetSlot(slot, out var item, out _, out var status, out _);
                    if (string.IsNullOrEmpty(item))
                    {
                        continue;
                    }
                    if (status == CookingStation.Status.Burnt)
                    {
                        burnt++;
                    }
                    else if (status == CookingStation.Status.Done)
                    {
                        ready++;
                    }
                }
                state = $"{ready}/{burnt}";
                done = burnt > 0 ? Lang.F("ProductionAlerts.FoodBurnt", "Food burnt on the {0}", Name(cooking.m_name)) : ready > 0 ? Lang.F("ProductionAlerts.FoodDone", "Food done on the {0}", Name(cooking.m_name)) : null;
            }
            if (state == null)
            {
                continue;
            }
            var id = zdo.m_uid;
            present.Add(id);
            if (_seen.TryGetValue(id, out var before) && before != state && done != null && Grew(before, state))
            {
                player.Message(MessageHud.MessageType.TopLeft, done);
                if (DebugLog.On)
                {
                    DebugLog.Write("alerts", $"{done} ({before} -> {state}) at {piece.transform.position}");
                }
            }
            _seen[id] = state;
        }
        // Stations out of range are forgotten, so coming back to finished work is not announced.
        var gone = new List<ZDOID>();
        foreach (var id in _seen.Keys)
        {
            if (!present.Contains(id))
            {
                gone.Add(id);
            }
        }
        foreach (var id in gone)
        {
            _seen.Remove(id);
        }
    }

    // A cooking station counts as newly done when more of its food is done or burnt than before; a smelter or fermenter on any change into its done state.
    private static bool Grew(string before, string after)
    {
        var a = before.Split('/');
        var b = after.Split('/');
        if (a.Length == 2 && b.Length == 2)
        {
            return int.Parse(b[0]) > int.Parse(a[0]) || int.Parse(b[1]) > int.Parse(a[1]);
        }
        return true;
    }

    private static string Name(string token) => Localization.instance.Localize(token);
}
