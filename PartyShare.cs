using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The map shared within a party, between members who run this plugin: where each member goes is revealed on the others' maps as they travel, and the pins each places can be sent to the others as shared pins. Messages travel over the game's routed RPC, which a vanilla server relays.
internal sealed class PartyShare
{
    private const string Section = "Party";
    private const string Rpc = "alkeari.senealuiunlocker.partyshare";
    private const float SendSeconds = 2f;
    private const float SendDistance = 15f;

    private enum Kind : byte
    {
        Explore = 1,
        Pin = 2
    }

    private readonly Party _party;
    private readonly ConfigEntry<bool> _explore;
    private readonly ConfigEntry<bool> _pins;
    private ZRoutedRpc _registered;
    private float _next;
    private Vector3 _sentAt = Vector3.positiveInfinity;
    private HashSet<Minimap.PinData> _known;
    private Minimap _knownFor;

    public PartyShare(ConfigFile cfg, Party party)
    {
        _party = party;
        _explore = cfg.Bind(Section, "Share Exploration With Party", true,
            "Reveal on your party members' maps where you travel, and on yours where they travel, as you go. Members need this mod.");
        _pins = cfg.Bind(Section, "Share New Pins With Party", false,
            "Send each pin you place to your party members as a shared pin. Off by default, since it puts your personal pins on everyone's map. Members need this mod.");
    }

    private IEnumerable<PartyMember> Peers => _party.Members.Values.Where(member => member.HasMod);

    public void Tick()
    {
        var rpc = ZRoutedRpc.instance;
        if (rpc != null && rpc != _registered)
        {
            _registered = rpc;
            rpc.Register<ZPackage>(Rpc, Receive);
        }
        var map = Minimap.instance;
        var player = Player.m_localPlayer;
        if (map == null || player == null || rpc == null || Time.time < _next)
        {
            return;
        }
        _next = Time.time + SendSeconds;
        if (map != _knownFor)
        {
            _knownFor = map;
            _known = null;
        }
        var peers = Peers.ToList();
        if (_explore.Value && peers.Count > 0 && Vector3.Distance(player.transform.position, _sentAt) >= SendDistance)
        {
            _sentAt = player.transform.position;
            var pkg = new ZPackage();
            pkg.Write((byte)Kind.Explore);
            pkg.Write(_sentAt);
            pkg.Write(map.m_exploreRadius);
            Send(peers, pkg);
        }
        var own = map.m_pins.Where(pin => pin.m_save && pin.m_ownerID == 0L && pin.m_type != Minimap.PinType.Death && pin.m_type != Minimap.PinType.Bed).ToList();
        if (_known == null)
        {
            _known = [.. own];
            return;
        }
        foreach (var pin in own.Where(pin => !_known.Contains(pin)))
        {
            _known.Add(pin);
            if (!_pins.Value || peers.Count == 0)
            {
                continue;
            }
            var pkg = new ZPackage();
            pkg.Write((byte)Kind.Pin);
            pkg.Write(pin.m_pos);
            pkg.Write((int)pin.m_type);
            pkg.Write(pin.m_name ?? "");
            Send(peers, pkg);
        }
        _known.RemoveWhere(pin => !own.Contains(pin));
    }

    private static void Send(List<PartyMember> peers, ZPackage pkg)
    {
        foreach (var member in peers)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(member.Peer, Rpc, pkg);
        }
    }

    private void Receive(long sender, ZPackage pkg)
    {
        try
        {
            var map = Minimap.instance;
            if (map == null || !_party.Members.ContainsKey(sender))
            {
                return;
            }
            var kind = (Kind)pkg.ReadByte();
            if (kind == Kind.Explore && _explore.Value)
            {
                var at = pkg.ReadVector3();
                var radius = Mathf.Clamp(pkg.ReadSingle(), 10f, 500f);
                map.Explore(at, radius);
            }
            else if (kind == Kind.Pin && _pins.Value)
            {
                var at = pkg.ReadVector3();
                var type = (Minimap.PinType)pkg.ReadInt();
                var name = pkg.ReadString();
                if (map.m_pins.Any(pin => Utils.DistanceXZ(pin.m_pos, at) < 1f && pin.m_name == name))
                {
                    return;
                }
                var info = ZNet.instance == null ? default : ZNet.instance.GetPlayerList().FirstOrDefault(p => p.m_characterID.UserID == sender);
                map.AddPin(at, type, name, true, false, sender, info.m_userInfo.m_id);
            }
        }
        catch (Exception)
        {
        }
    }
}
