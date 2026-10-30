using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Party members as live player marks on the map and the minimap. The game already marks players who share their position, so only the others get a mark; marks are never saved and leave with the member.
internal sealed class PartyMap
{
    private readonly Party _party;
    private readonly ConfigEntry<bool> _enabled;
    private readonly Dictionary<long, Minimap.PinData> _pins = [];

    public PartyMap(ConfigFile cfg, Party party)
    {
        _party = party;
        _enabled = cfg.Bind("Party", "Show Party On Map", true,
            "Mark party members on the map and the minimap in real time, while they are near enough for the game to know where they are, or share their position.");
    }

    public void Tick()
    {
        var map = Minimap.instance;
        var net = ZNet.instance;
        if (map == null || net == null)
        {
            _pins.Clear();
            return;
        }
        var wanted = new Dictionary<long, (Vector3 Pos, string Name)>();
        if (_enabled.Value && _party.Enabled && Player.m_localPlayer != null)
        {
            var players = Player.GetAllPlayers().Where(p => p != null && p != Player.m_localPlayer).ToDictionary(Party.PeerOf, p => p);
            foreach (var info in net.GetPlayerList())
            {
                var peer = info.m_characterID.UserID;
                if (!_party.Members.ContainsKey(peer) || info.m_publicPosition || !players.TryGetValue(peer, out var player))
                {
                    continue;
                }
                wanted[peer] = (player.transform.position, info.m_name);
            }
        }
        foreach (var peer in _pins.Keys.Where(peer => !wanted.ContainsKey(peer)).ToList())
        {
            map.RemovePin(_pins[peer]);
            _pins.Remove(peer);
        }
        foreach (var (peer, (pos, name)) in wanted.Select(pair => (pair.Key, pair.Value)))
        {
            if (!_pins.TryGetValue(peer, out var pin) || !map.m_pins.Contains(pin))
            {
                _pins[peer] = map.AddPin(pos, Minimap.PinType.Player, name, false, false);
            }
            else
            {
                pin.m_pos = pos;
            }
        }
    }
}
