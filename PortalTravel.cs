using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Carts and tames through portals, both cheats, so both off by default.
// The game keeps a persistent object as its ZDO, unloads the object when its ZDO leaves the player's active area and creates it again once its area has loaded around the player. So the moment a portal starts the local player's teleport, each cart or tame this client owns has its ZDO moved to the far portal (the owner's position update every server stores) and its local object unloaded as ZNetScene.RemoveObjects unloads one, which keeps its transform sync from writing the old position back. The game then creates it at the far portal when that area loads, and once the player has arrived it is set down beside them and the cart attached through the game's own use request.
// The move happens at departure rather than arrival because ownership is only certain then: once the player is gone, the server hands the object to any player still near it, and a client moving a ZDO it no longer owns would show a copy the server never accepted.
internal static class PortalTravel
{
    private const float TeleportTimeout = 60f;
    private const float ArrivalTimeout = 20f;
    private static readonly float[] TameAngles = [60f, -60f, 90f, -90f];

    private sealed class Traveler
    {
        public ZDOID Id;
        public string Name;
        public bool Cart;
        public Vector3 Feet;
        public Vector3 Direction;
        public float Distance;
        public Vector3 Handle;
        public float Lift;
        public Quaternion Rotation;
    }

    private static ConfigEntry<bool> _carts;
    private static ConfigEntry<bool> _tames;
    private static ConfigEntry<float> _range;
    private static readonly List<Traveler> Pending = [];
    private static readonly List<Character> Near = [];
    private static float _departed;
    private static float _arrived = -1f;

    public static void Bind(ConfigFile cfg)
    {
        _carts = cfg.Bind("Cheats: Portals", "Carts Through Portals (Cheat)", false,
            "Cheat, off by default. The cart you pull when you go through a portal arrives with you, with its load, in front of you and attached again when it can be. A cart someone else has open stays behind.");
        _tames = cfg.Bind("Cheats: Portals", "Tames Through Portals (Cheat)", false,
            "Cheat, off by default. Tames following you within Tames Through Portals Range when you go through a portal arrive with you, beside you. A tame another player's game is running stays behind.");
        _range = cfg.Bind("Cheats: Portals", "Tames Through Portals Range", 10f, new ConfigDescription(
            "How far from you, in meters, a following tame may be to go through a portal with you.", new AcceptableValueRange<float>(2f, 30f)));
    }

    public static void Tick()
    {
        Reattach();
        if (Pending.Count == 0)
        {
            return;
        }
        var player = Player.m_localPlayer;
        if (player == null || ZNetScene.instance == null || ZDOMan.instance == null)
        {
            Pending.Clear();
            _arrived = -1f;
            return;
        }
        var now = Time.time;
        if (player.IsTeleporting() || !ZNetScene.instance.IsAreaReady(player.transform.position))
        {
            if (now - _departed > TeleportTimeout)
            {
                GiveUp(player, "did not arrive in time; look by the portal you came out of");
            }
            return;
        }
        if (_arrived < 0f)
        {
            _arrived = now;
        }
        foreach (var traveler in Pending.ToList())
        {
            var zdo = ZDOMan.instance.GetZDO(traveler.Id);
            if (zdo == null)
            {
                player.Message(MessageHud.MessageType.TopLeft, Lang.F("PortalTravel.IsNoLongerInThe", "{0} is no longer in the world", traveler.Name));
                Log($"{traveler.Name} {traveler.Id} has no ZDO after arrival");
                Pending.Remove(traveler);
                continue;
            }
            var view = ZNetScene.instance.FindInstance(zdo);
            if (view == null || !view.IsValid())
            {
                continue;
            }
            // The game frees what a player walks away from and hands it out again a moment later, so an arrival can briefly have no owner, or one whose game has left: take it then, and leave it only to a connected player's game.
            if (!view.IsOwner())
            {
                if (zdo.HasOwner() && OwnerConnected(zdo.GetOwner()))
                {
                    Pending.Remove(traveler);
                    Log($"{traveler.Name} {traveler.Id} arrived but another player's game now runs it; left where the game placed it");
                    continue;
                }
                view.ClaimOwnership();
            }
            var spot = Spot(traveler);
            // The game builds an area's ground colliders a little after the area counts as loaded, and a cart or tame whose physics run before then falls through the ground (a cart fell 80 m in a test, 2026-10-14). It is held still until there is ground under its spot, and set on that ground.
            if (!Ground(spot, out var ground))
            {
                Hold(view, true);
                continue;
            }
            spot.y = Mathf.Max(spot.y, ground + traveler.Lift);
            Hold(view, false);
            Pending.Remove(traveler);
            SetDown(view, spot, traveler.Rotation);
            var cart = traveler.Cart ? view.GetComponent<Vagon>() : null;
            if (cart != null)
            {
                // Held still until it is attached again: on a slope a free cart rolls out of the player's reach (1.8 m in a test, 2026-10-15) before the player has finished arriving.
                Hold(view, true);
                Landing.Add(new Lander { Cart = cart, Traveler = traveler, Until = Time.time + ReattachSeconds });
            }
            Log($"{traveler.Name} {traveler.Id} set down at {spot}");
        }
        if (Pending.Count > 0 && now - _arrived > ArrivalTimeout)
        {
            GiveUp(player, "did not load beside you; look by the portal you came out of");
        }
        if (Pending.Count == 0)
        {
            _arrived = -1f;
        }
    }

    private const float ReattachSeconds = 10f;

    private sealed class Lander
    {
        public Vagon Cart;
        public Traveler Traveler;
        public float Until;
        public bool Moved;
        public float StillSince = -1f;
        public float Requested = -1f;
    }

    private const float StillSeconds = 0.5f;

    private const float NoReturnSeconds = 3f;

    private static float _noReturnUntil;

    private static readonly List<Lander> Landing = [];

    // A cart is held still until it is attached again, since a free one rolls with the slope. The player does not arrive exactly where the exit put the cart's handle, and on rising ground the handle can stand out of reach above them (1.6 m in a test, 2026-10-15), so once the player has arrived and stands still the cart is set down once more in front of where they stand; then it is let go and attached as Vagon.Interact would (the cart's next Update attaches it to its requester when CanAttach agrees).
    private static void Reattach()
    {
        var player = Player.m_localPlayer;
        for (var i = Landing.Count - 1; i >= 0; i--)
        {
            var lander = Landing[i];
            var cart = lander.Cart;
            if (cart == null || player == null || Time.time > lander.Until)
            {
                if (cart != null && player != null)
                {
                    Log($"{Localization.instance.Localize(cart.m_name)} not attached again: handle {cart.m_attachPoint.position}, player reach point {player.transform.position + cart.m_attachOffset}, distance {Vector3.Distance(player.transform.position + cart.m_attachOffset, cart.m_attachPoint.position):0.00} (needs under {cart.m_detachDistance:0.00})");
                }
                if (cart != null && cart.m_nview != null)
                {
                    Hold(cart.m_nview, false);
                }
                Landing.RemoveAt(i);
                continue;
            }
            // The game ends a teleport with the player a meter above the exit, from where they drop and can slide on a slope: wait until they stand still on the ground.
            if (player.IsTeleporting() || !player.IsOnGround() || player.m_body.linearVelocity.sqrMagnitude > 0.25f)
            {
                lander.StillSince = -1f;
                continue;
            }
            if (lander.StillSince < 0f)
            {
                lander.StillSince = Time.time;
            }
            // A player can stop for a frame and slide on, which put the cart beside where they had been and out of reach (0.7 m in a test on SeneaL UI 1.2.0-preview.9, 2026-10-15): they must stand still a moment first.
            if (Time.time - lander.StillSince < StillSeconds)
            {
                continue;
            }
            if (lander.Requested >= 0f)
            {
                if (cart.IsAttached(player))
                {
                    Landing.RemoveAt(i);
                    _noReturnUntil = Time.time + NoReturnSeconds;
                    Log($"{Localization.instance.Localize(cart.m_name)} attached again");
                    continue;
                }
                if (Time.time - lander.Requested < StillSeconds)
                {
                    continue;
                }
                // The request was not taken, so the cart is held and set down in front of the player once more.
                Hold(cart.m_nview, true);
                lander.Requested = -1f;
                lander.Moved = false;
            }
            var inReach = Vector3.Distance(player.transform.position + cart.m_attachOffset, cart.m_attachPoint.position) < cart.m_detachDistance;
            if (!inReach && !lander.Moved)
            {
                lander.Moved = true;
                var spot = Nearest(lander.Traveler, cart, player);
                SetDown(cart.m_nview, spot, lander.Traveler.Rotation);
                Log($"{Localization.instance.Localize(cart.m_name)} set down again in front of the player at {spot}: handle {cart.m_attachPoint.position} (kept offset {lander.Traveler.Handle}), player at {player.transform.position}");
                continue;
            }
            if (inReach)
            {
                Hold(cart.m_nview, false);
                cart.m_useRequester = player;
                lander.Requested = Time.time;
            }
        }
    }

    // A cart's handle must come within Vagon.m_detachDistance of the player's reach point (1 m for the game's cart), and in front of an exit facing a hillside it stands above them (1.7 m in a test, 2026-10-15): of eight directions around the player, the one whose handle lands nearest that point is taken.
    private static Vector3 Nearest(Traveler traveler, Vagon cart, Player player)
    {
        var reach = player.transform.position + cart.m_attachOffset;
        var handleHeight = cart.m_attachPoint.position.y - cart.transform.position.y;
        var facing = traveler.Direction;
        var best = Vector3.zero;
        var bestRotation = traveler.Rotation;
        var bestDistance = float.MaxValue;
        traveler.Feet = player.transform.position;
        for (var i = 0; i < 8; i++)
        {
            traveler.Direction = Quaternion.Euler(0f, i * 45f, 0f) * facing;
            traveler.Rotation = Quaternion.LookRotation(-traveler.Direction);
            var spot = Spot(traveler);
            var handle = spot + traveler.Rotation * traveler.Handle + Vector3.up * handleHeight;
            var distance = Vector3.Distance(handle, reach);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = spot;
                bestRotation = traveler.Rotation;
            }
        }
        traveler.Rotation = bestRotation;
        return best;
    }

    private static void GiveUp(Player player, string reason)
    {
        player.Message(MessageHud.MessageType.TopLeft, Lang.F("PortalTravel.GaveUp", "{0} {1}", string.Join(", ", Pending.Select(t => t.Name)), reason));
        Log($"gave up waiting for {Pending.Count}: {reason}");
        Pending.Clear();
        foreach (var view in Held.Keys.ToList())
        {
            if (view != null)
            {
                Hold(view, false);
            }
        }
        Held.Clear();
        _arrived = -1f;
    }

    private static void Depart(Player player)
    {
        var cartsOn = _carts.Value && ServerRules.Allows("CartsThroughPortals");
        var tamesOn = _tames.Value && ServerRules.Allows("TamesThroughPortals");
        if ((!cartsOn && !tamesOn) || ZNetScene.instance == null || ZoneSystem.instance == null)
        {
            return;
        }
        // The same target the game just gave Player.TeleportTo: the far portal's exit point, a meter up, facing out of the portal.
        var feet = player.m_teleportTargetPos - Vector3.up;
        var forward = player.m_teleportTargetRot * Vector3.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude < 0.01f ? Vector3.forward : forward.normalized;
        var left = new List<string>();
        if (cartsOn)
        {
            DepartCart(player, feet, forward, left);
        }
        if (tamesOn)
        {
            DepartTames(player, feet, forward, left);
        }
        if (left.Count > 0)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("PortalTravel.LeftBehind", "Left behind: {0}", string.Join(", ", left)));
        }
        if (Pending.Count > 0)
        {
            _departed = Time.time;
            _arrived = -1f;
        }
    }

    private static void DepartCart(Player player, Vector3 feet, Vector3 forward, List<string> left)
    {
        var cart = Vagon.m_instances.FirstOrDefault(v => v != null && v.m_attachJoin != null && v.m_attachJoin.connectedBody != null && v.m_attachJoin.connectedBody.gameObject == player.gameObject);
        if (cart == null)
        {
            return;
        }
        var name = VehicleNames.Label(cart, Localization.instance.Localize(cart.m_name));
        var view = cart.m_nview;
        if (view == null || !view.IsValid() || !view.IsOwner())
        {
            left.Add(name);
            Log($"cart {name} not owned here; left");
            return;
        }
        if (cart.m_container != null && cart.m_container.IsInUse())
        {
            left.Add(name + " (open)");
            Log($"cart {name} open in a container window; left");
            return;
        }
        // Facing back at the player with its handle within reach, as a cart stands when a player walks up to its front to take it.
        var handle = Quaternion.Inverse(cart.transform.rotation) * (cart.m_attachPoint.position - cart.transform.position);
        var traveler = new Traveler
        {
            Name = name,
            Cart = true,
            Feet = feet,
            Direction = forward,
            Distance = 1.2f,
            Handle = new Vector3(handle.x, 0f, handle.z),
            Lift = Mathf.Clamp(cart.transform.position.y - player.transform.position.y, 0f, 1f) + 0.1f,
            Rotation = Quaternion.LookRotation(-forward)
        };
        cart.Detach();
        Send(view, traveler);
    }

    private static void DepartTames(Player player, Vector3 feet, Vector3 forward, List<string> left)
    {
        Near.Clear();
        Character.GetCharactersInRange(player.transform.position, _range.Value, Near);
        var mount = player.IsRiding() && player.GetDoodadController() is Sadle sadle ? sadle.m_character : null;
        var slot = 0;
        foreach (var character in Near)
        {
            if (character == null || character == player || character == mount || !character.IsTamed())
            {
                continue;
            }
            var tame = character.GetComponent<Tameable>();
            var view = character.m_nview;
            if (tame == null || view == null || !view.IsValid())
            {
                continue;
            }
            var zdo = view.GetZDO();
            var follows = (tame.m_monsterAI != null && tame.m_monsterAI.GetFollowTarget() == player.gameObject) || zdo.GetString(ZDOVars.s_follow) == player.GetPlayerName();
            if (!follows)
            {
                continue;
            }
            var name = character.GetHoverName();
            if (!view.IsOwner())
            {
                if (zdo.HasOwner() && OwnerConnected(zdo.GetOwner()))
                {
                    left.Add(name);
                    Log($"tame {name} {zdo.m_uid} run by connected player {zdo.GetOwner()}; left");
                    continue;
                }
                view.ClaimOwnership();
            }
            Send(view, new Traveler
            {
                Name = name,
                Feet = feet,
                Direction = Quaternion.Euler(0f, TameAngles[slot % TameAngles.Length], 0f) * forward,
                Distance = 2.5f + 1.2f * (slot / TameAngles.Length),
                Lift = 0.3f,
                Rotation = Quaternion.LookRotation(forward)
            });
            slot++;
        }
    }

    // A connected player's character ZDO was created by that player's game, so its user id is that game's session id, the id a ZDO owner is.
    private static bool OwnerConnected(long owner) => ZNet.instance != null && ZNet.instance.GetPlayerList().Any(p => p.m_characterID.UserID == owner);

    // Where a traveler stands beside the exit: as far toward its direction as there is room, on the floor found there. Worked out again on arrival, once the far area's pieces and ground have loaded.
    private static Vector3 Spot(Traveler traveler)
    {
        var extent = traveler.Cart ? 2f * traveler.Handle.magnitude : 0.5f;
        var room = Mathf.Max(0.5f, Clear(traveler.Feet, traveler.Direction, traveler.Distance + extent) - extent);
        var spot = traveler.Feet + traveler.Direction * Mathf.Min(room, traveler.Distance) - traveler.Rotation * traveler.Handle;
        spot.y = Floor(spot, traveler.Feet.y) + traveler.Lift;
        return spot;
    }

    private static void Send(ZNetView view, Traveler traveler)
    {
        var zdo = view.GetZDO();
        var spot = Spot(traveler);
        zdo.Set(ZDOVars.s_bodyVelHash, Vector3.zero);
        zdo.Set(ZDOVars.s_bodyAVelHash, Vector3.zero);
        zdo.SetPosition(spot);
        zdo.SetRotation(traveler.Rotation);
        ZNetScene.instance.m_instances.Remove(zdo);
        view.ResetZDO();
        Object.Destroy(view.gameObject);
        traveler.Id = zdo.m_uid;
        Pending.Add(traveler);
        Log($"{(traveler.Cart ? "cart" : "tame")} {traveler.Name} {zdo.m_uid} sent to {spot}");
    }

    // The first solid surface under the spot, from three meters above it (below a roof over the exit) down to fifty meters under it.
    private static bool Ground(Vector3 spot, out float height)
    {
        var found = Physics.Raycast(spot + Vector3.up * 3f, Vector3.down, out var hit, 53f, ZoneSystem.instance.m_solidRayMask);
        height = found ? hit.point.y : spot.y;
        return found;
    }

    private static readonly Dictionary<ZNetView, bool> Held = [];

    // Freezes the arriving object's physics while it waits for ground, and gives back each body's own setting once there is some.
    private static void Hold(ZNetView view, bool hold)
    {
        var body = view.GetComponent<Rigidbody>();
        if (body == null)
        {
            return;
        }
        if (hold && !Held.ContainsKey(view))
        {
            Held[view] = body.isKinematic;
            body.isKinematic = true;
        }
        else if (!hold && Held.TryGetValue(view, out var was))
        {
            body.isKinematic = was;
            Held.Remove(view);
        }
    }

    private static void SetDown(ZNetView view, Vector3 spot, Quaternion rotation)
    {
        view.transform.SetPositionAndRotation(spot, rotation);
        foreach (var body in view.GetComponentsInChildren<Rigidbody>())
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
        var zdo = view.GetZDO();
        zdo.SetPosition(spot);
        zdo.SetRotation(rotation);
        var character = view.GetComponent<Character>();
        if (character != null)
        {
            character.m_maxAirAltitude = spot.y;
        }
    }

    // How far toward direction there is room, so nothing is set down inside a wall or a piece beside the exit. The mask leaves out characters and vehicles, so neither the player nor the cart blocks it.
    private static float Clear(Vector3 feet, Vector3 direction, float wanted)
    {
        return Physics.Raycast(feet + Vector3.up, direction, out var hit, wanted + 0.5f, ZoneSystem.instance.m_solidRayMask)
            ? Mathf.Clamp(hit.distance - 0.5f, 0f, wanted)
            : wanted;
    }

    // From two meters above the exit's floor, so below a roof over it; before the far area has loaded there is no floor to find and the exit's height stands in.
    private static float Floor(Vector3 spot, float exitFloor)
    {
        var probe = new Vector3(spot.x, exitFloor + 1f, spot.z);
        return ZoneSystem.instance.FindFloor(probe, out var height) && height > exitFloor - 3f ? height : exitFloor;
    }

    private static void Log(string message)
    {
        if (DebugLog.On)
        {
            DebugLog.Write("portal travel", message);
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport))]
    internal static class TeleportPatch
    {
        // Attaching the landed cart can pull the player back into the exit portal's trigger, which sent player and cart straight back (SeneaL UI 1.1.7 run, 2026-10-15), so the local player cannot leave again while a cart lands or just after.
        private static bool Prefix(Player player, out bool __state)
        {
            __state = player != null && player.IsTeleporting();
            return player == null || player != Player.m_localPlayer || (Landing.Count == 0 && Time.time >= _noReturnUntil);
        }

        private static void Postfix(Player player, bool __state)
        {
            if (player == null || player != Player.m_localPlayer || __state || !player.IsTeleporting())
            {
                return;
            }
            Depart(player);
        }
    }
}
