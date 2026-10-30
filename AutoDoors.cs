using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Doors and gates open as you walk up to them and close behind you. Only doors anyone may open by hand qualify: never one that needs a key, one that cannot be closed again (dungeon doors), or one inside a ward that does not admit you. A door is closed again only if this opened it, nobody stands in it, and you have been away for the delay. Off by default, since it opens and closes doors other players share.
internal sealed class AutoDoors
{
    private const string Section = "Doors";
    private static readonly Collider[] Near = new Collider[64];
    private static readonly int Mask = LayerMask.GetMask("piece", "piece_nonsolid", "Default");

    private readonly ConfigEntry<bool> _on;
    private readonly ConfigEntry<float> _distance;
    private readonly ConfigEntry<bool> _close;
    private readonly ConfigEntry<float> _delay;
    private readonly Dictionary<Door, float> _opened = [];
    private float _next;

    public AutoDoors(ConfigFile cfg)
    {
        _on = cfg.Bind(Section, "Auto Doors", false,
            "Doors and gates open as you walk up to them, never ones that need a key or sit in a ward that does not admit you. Off by default because it opens doors other players share.");
        _distance = cfg.Bind(Section, "Door Open Distance", 2.2f, new ConfigDescription(
            "How close to a door, in meters, you walk before it opens.", new AcceptableValueRange<float>(1f, 5f)));
        _close = cfg.Bind(Section, "Close Doors Behind You", true,
            "Close a door this opened once you have walked away from it and nobody stands in it.");
        _delay = cfg.Bind(Section, "Door Close Delay", 1.5f, new ConfigDescription(
            "Seconds after you leave a door before it closes behind you.", new AcceptableValueRange<float>(0.2f, 10f)));
    }

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        if (!_on.Value || !ServerRules.Allows("AutoDoors") || player == null || player.IsDead())
        {
            _opened.Clear();
            return;
        }
        if (now < _next)
        {
            return;
        }
        _next = now + 0.1f;
        var at = player.transform.position;
        var count = Physics.OverlapSphereNonAlloc(at, _distance.Value, Near, Mask);
        for (var i = 0; i < count; i++)
        {
            var door = Near[i] == null ? null : Near[i].GetComponentInParent<Door>();
            if (door == null || _opened.ContainsKey(door) || !Usable(door) || State(door) != 0)
            {
                continue;
            }
            // Opens away from you, as the game does when you open it by hand.
            door.Open((at - door.transform.position).normalized);
            _opened[door] = float.MaxValue;
            Log(door, "opened");
        }
        var leave = new List<Door>();
        foreach (var door in new List<Door>(_opened.Keys))
        {
            if (door == null || !door.m_nview.IsValid() || State(door) == 0)
            {
                leave.Add(door);
                continue;
            }
            var away = Vector3.Distance(at, door.transform.position) > _distance.Value + 0.8f;
            if (!away)
            {
                _opened[door] = float.MaxValue;
                continue;
            }
            if (_opened[door] == float.MaxValue)
            {
                _opened[door] = now + _delay.Value;
            }
            if (now >= _opened[door] && _close.Value && !Occupied(door))
            {
                door.m_nview.InvokeRPC("UseDoor", true);
                Log(door, "closed");
                leave.Add(door);
            }
            else if (!_close.Value)
            {
                leave.Add(door);
            }
        }
        foreach (var door in leave)
        {
            _opened.Remove(door);
        }
    }

    private static int State(Door door) => door.m_nview != null && door.m_nview.IsValid() ? door.m_nview.GetZDO().GetInt(ZDOVars.s_state) : 1;

    private static bool Usable(Door door) =>
        door.m_nview != null && door.m_nview.IsValid() && door.m_keyItem == null && !door.m_canNotBeClosed
        && (!door.m_checkGuardStone || PrivateArea.CheckAccess(door.transform.position, 0f, false));

    private static readonly List<Character> Characters = [];

    private static bool Occupied(Door door)
    {
        Characters.Clear();
        Character.GetCharactersInRange(door.transform.position, 1.6f, Characters);
        return Characters.Count > 0;
    }

    private static void Log(Door door, string what)
    {
        if (DebugLog.On)
        {
            DebugLog.Write("doors", $"{what} {door.name} at {door.transform.position}");
        }
    }
}
