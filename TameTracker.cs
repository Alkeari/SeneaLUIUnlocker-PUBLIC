using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Tamed animals, and with Taming On Map the ones being tamed with their progress, as live pins of their own kind (PinKinds "Tames") on the map and minimap: the name given to them or their kind, with their health, moving with them while the game has them loaded and left where they were last seen otherwise. The pins are never saved; their chip lists them.
internal sealed class TameTracker
{
    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<bool> _taming;
    private readonly Dictionary<ZDOID, Minimap.PinData> _pins = [];
    private float _next;

    public TameTracker(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Tames", "Tames On Map", true,
            "Mark your tamed animals on the map and minimap with their name and health, live while they are near enough for the game to have them, and where you last saw them otherwise.");
        _taming = cfg.Bind("Tames", "Taming On Map", true,
            "Also mark creatures you are taming, with how far along the taming is, while Tames on Map is on.");
    }

    // How far along taming is, as the game shows it on hover: the share of the taming time already spent.
    private static int Taming(Character character)
    {
        var tame = character.GetComponent<Tameable>();
        if (tame == null || character.m_nview == null || !character.m_nview.IsValid() || tame.m_tamingTime <= 0f)
        {
            return 0;
        }
        var left = character.m_nview.GetZDO().GetFloat(ZDOVars.s_tameTimeLeft, tame.m_tamingTime);
        return (int)((1f - Mathf.Clamp01(left / tame.m_tamingTime)) * 100f);
    }

    public void Tick(float now)
    {
        var map = Minimap.instance;
        if (map == null || Player.m_localPlayer == null)
        {
            _pins.Clear();
            return;
        }
        if (!_enabled.Value || !ServerRules.Allows("Tames"))
        {
            foreach (var pin in _pins.Values)
            {
                map.RemovePin(pin);
            }
            _pins.Clear();
            return;
        }
        if (now < _next)
        {
            return;
        }
        _next = now + 1f;
        var type = PinKinds.Of("Tames");
        var seen = new HashSet<ZDOID>();
        foreach (var character in Character.GetAllCharacters().Where(c => c != null && !c.IsDead() && (c.IsTamed() || _taming.Value && Taming(c) > 0)))
        {
            var view = character.m_nview;
            if (view == null || !view.IsValid())
            {
                continue;
            }
            var id = view.GetZDO().m_uid;
            seen.Add(id);
            var given = view.GetZDO().GetString(ZDOVars.s_tamedName, "");
            var kind = Localization.instance.Localize(character.m_name);
            var name = character.IsTamed()
                ? (given.Length > 0 ? $"{given} ({kind})" : kind) + $" {Mathf.RoundToInt(character.GetHealthPercentage() * 100f)}%"
                : $"{kind} taming {Taming(character)}%";
            if (!_pins.TryGetValue(id, out var pin) || !map.m_pins.Contains(pin))
            {
                _pins[id] = map.AddPin(character.transform.position, type, name, false, false);
                continue;
            }
            pin.m_pos = character.transform.position;
            pin.m_name = name;
        }
        foreach (var (id, pin) in _pins.Select(pair => (pair.Key, pair.Value)))
        {
            if (!seen.Contains(id) && !pin.m_name.EndsWith(" (last seen)", System.StringComparison.Ordinal))
            {
                pin.m_name += " (last seen)";
            }
        }
    }
}
