using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal sealed class PartyMember
{
    public long Peer;
    public string Name = "";
    public ZNet.CrossNetworkUserInfo User;
    public int Level = -1;
    public float MaxStamina;
    public float SeenStamina = 50f;
    public bool HasMod;
}

// Who is in the party. A vanilla server only relays the party messages between players who run this mod, the way it relays the game's own chat, so everything is decided on the clients.
// Add or Request (the Party Key on the player you look at): a player without this mod is added at once; a player with it gets a request and joins only by accepting. A player is taken as having the mod once it answers a message; one that stays silent is taken as not having it.
// Players who run this mod share one party: each member that joins is announced to the others (Roster). Remove takes a member off your own party list only; Vote to Kick takes them off everyone's once at least half the party, counted with the member being kicked, votes yes, members without this mod counting as yes.
// The party is kept between sessions, by each member's platform id, for members who allow it (Stay In Parties Between Sessions); a member without this mod is taken as allowing it.
internal sealed class Party
{
    private const string Rpc = "alkeari.senealuiunlocker.party";
    private const string DataKey = "alkeari.senealuiunlocker.party";
    private const float StatusSeconds = 3f;
    private const float AnswerSeconds = 3f;

    private enum Msg : byte
    {
        Request = 1,
        Accept = 2,
        Decline = 3,
        Leave = 4,
        Status = 5,
        Roster = 6,
        KickVote = 7,
        Vote = 8,
        Kicked = 9,
        Rejoin = 10,
        Ack = 11
    }

    private static readonly Type SrApi = Quiet.Type("SkillsReworked.Api.SkillsReworkedApi");

    public readonly Dictionary<long, PartyMember> Members = [];

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<bool> _persist;
    private readonly ConfigEntry<bool> _allowPvp;
    private readonly ConfigEntry<KeyboardShortcut> _partyKey;
    private readonly ConfigEntry<KeyboardShortcut> _acceptKey;
    private readonly ConfigEntry<KeyboardShortcut> _declineKey;
    private readonly ManualLogSource _log;

    // Players asked to join or to rejoin who have not answered yet; a player that never answers has no mod and is added.
    private readonly Dictionary<long, (float Until, bool Rejoin)> _asked = [];
    private readonly HashSet<long> _acked = [];
    private readonly HashSet<string> _removed = [];
    private readonly Dictionary<long, Kick> _kicks = [];
    private HashSet<string> _remembered = [];
    private readonly HashSet<string> _restoreTried = [];

    private ZRoutedRpc _registered;
    private Player _for;
    private (long Peer, string Name, float Until, long Target)? _prompt;
    private float _nextStatus;

    private sealed class Kick
    {
        public long Target;
        public HashSet<long> Yes = [];
        public HashSet<long> No = [];
        public float Until;
    }

    private readonly ConfigEntry<float> _answerSeconds;
    private readonly ConfigEntry<float> _reach;
    private readonly ConfigEntry<float> _aim;

    public Party(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        const string Section = "Party";
        _enabled = cfg.Bind(Section, "Party", true, "Keep a party of players and show it on the Party HUD and the map.");
        _persist = cfg.Bind(Section, "Stay In Parties Between Sessions", true,
            "Stay in a party you joined when you or its other members log in again. Off = every session starts without a party.");
        _allowPvp = cfg.Bind(Section, "Allow Party In PvP", false,
            "Allow party members who have PvP on. Between two players with this mod both must allow it.");
        _partyKey = cfg.Bind(Section, "Party Key", new KeyboardShortcut(KeyCode.I),
            "Add or Request: adds the player you look at to your party, or, when that player runs this mod, asks them to join. Empty = no key.");
        _answerSeconds = cfg.Bind(Section, "Party Answer Seconds", 30f, new ConfigDescription(
            "How long a party request or a vote to kick waits for an answer before it lapses.", new AcceptableValueRange<float>(5f, 120f)));
        _reach = cfg.Bind(Section, "Party Key Reach", 12f, new ConfigDescription(
            "How far away, in meters, the player you look at may be for the Party Key to pick them.", new AcceptableValueRange<float>(2f, 50f)));
        _aim = cfg.Bind(Section, "Party Key Aim Angle", 12f, new ConfigDescription(
            "How far off the center of your view, in degrees, that player may be.", new AcceptableValueRange<float>(2f, 45f)));
        _acceptKey = cfg.Bind(Section, "Accept Invite Key", new KeyboardShortcut(KeyCode.Y), "Accepts a party request or votes yes on a kick. Empty = no key.");
        _declineKey = cfg.Bind(Section, "Decline Invite Key", new KeyboardShortcut(KeyCode.N), "Declines a party request or votes no on a kick. Empty = no key.");
    }

    public bool Enabled => _enabled.Value && ServerRules.Allows("Party");

    public void Tick(float now)
    {
        Register();
        var me = Player.m_localPlayer;
        var net = ZNet.instance;
        if (!Enabled || me == null || net == null || net.IsDedicated())
        {
            Members.Clear();
            return;
        }
        if (me != _for)
        {
            _for = me;
            Load(me);
        }
        var online = net.GetPlayerList().ToDictionary(info => info.m_characterID.UserID, info => info);
        foreach (var gone in Members.Keys.Where(peer => !online.ContainsKey(peer)).ToList())
        {
            Members.Remove(gone);
        }
        foreach (var member in Members.Values)
        {
            member.User = online[member.Peer].m_userInfo;
            member.Name = online[member.Peer].m_name;
        }
        Restore(me, online, now);
        Unanswered(me, online, now);
        Votes(me, now);
        if (!InventoryFeatures.Typing())
        {
            Keys(me, online);
        }
        if (_prompt is { } prompt && now > prompt.Until)
        {
            _prompt = null;
        }
        if (now >= _nextStatus)
        {
            _nextStatus = now + StatusSeconds;
            foreach (var peer in Members.Keys.ToList())
            {
                Send(peer, Msg.Status, me);
            }
        }
    }

    private static string Id(ZNet.PlayerInfo info) => info.m_userInfo.m_id.ToString();

    private void Load(Player me)
    {
        Members.Clear();
        _restoreTried.Clear();
        _remembered = me.m_customData.TryGetValue(DataKey, out var saved) && _persist.Value
            ? saved.Split(';').Where(id => id.Length > 0).ToHashSet()
            : [];
    }

    private void Save()
    {
        var me = Player.m_localPlayer;
        if (me == null)
        {
            return;
        }
        var ids = Members.Values.Select(member => member.User.m_id.ToString()).Where(id => id.Length > 0);
        me.m_customData[DataKey] = _persist.Value ? string.Join(";", ids) : "";
    }

    // Members of a party kept from an earlier session come back as they come online: one without this mod at once, one with it if it still allows it.
    private void Restore(Player me, Dictionary<long, ZNet.PlayerInfo> online, float now)
    {
        if (!_persist.Value || _remembered.Count == 0)
        {
            return;
        }
        foreach (var (peer, info) in online.Select(pair => (pair.Key, pair.Value)))
        {
            var id = Id(info);
            if (peer == PeerOf(me) || Members.ContainsKey(peer) || !_remembered.Contains(id) || !_restoreTried.Add(id))
            {
                continue;
            }
            _acked.Remove(peer);
            _asked[peer] = (now + AnswerSeconds, true);
            Send(peer, Msg.Rejoin, me);
        }
    }

    private void Unanswered(Player me, Dictionary<long, ZNet.PlayerInfo> online, float now)
    {
        foreach (var (peer, ask) in _asked.Select(pair => (pair.Key, pair.Value)).ToList())
        {
            if (_acked.Contains(peer))
            {
                if (now > ask.Until + _answerSeconds.Value)
                {
                    _asked.Remove(peer);
                }
                continue;
            }
            if (now < ask.Until)
            {
                continue;
            }
            _asked.Remove(peer);
            if (online.TryGetValue(peer, out var info))
            {
                Join(me, peer, info, false);
            }
        }
    }

    private void Keys(Player me, Dictionary<long, ZNet.PlayerInfo> online)
    {
        if (_prompt is { } prompt)
        {
            var yes = KeyInput.Pressed(_acceptKey.Value);
            var no = !yes && KeyInput.Pressed(_declineKey.Value);
            if (yes || no)
            {
                _prompt = null;
                if (prompt.Target != 0)
                {
                    SendVote(prompt.Peer, prompt.Target, yes, me);
                }
                else if (yes && online.TryGetValue(prompt.Peer, out var info))
                {
                    Members[prompt.Peer] = new PartyMember { Peer = prompt.Peer, Name = info.m_name, User = info.m_userInfo, HasMod = true };
                    Save();
                    Send(prompt.Peer, Msg.Accept, me);
                    me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.YouJoinedSParty", "You joined {0}'s party", prompt.Name));
                }
                else
                {
                    Send(prompt.Peer, Msg.Decline, me);
                }
                return;
            }
        }
        if (!KeyInput.Pressed(_partyKey.Value))
        {
            return;
        }
        var target = LookedAt(me);
        var peer = target == null ? 0 : PeerOf(target);
        if (peer == 0 || !online.TryGetValue(peer, out var targetInfo))
        {
            me.Message(MessageHud.MessageType.TopLeft, Lang.T("Party.LookAtAPlayerTo", "Look at a player to add them to the party"));
            return;
        }
        if (Members.ContainsKey(peer))
        {
            me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.IsAlreadyInTheParty", "{0} is already in the party", targetInfo.m_name));
            return;
        }
        if (!_allowPvp.Value && (me.IsPVPEnabled() || target.IsPVPEnabled()))
        {
            me.Message(MessageHud.MessageType.TopLeft, Lang.T("Party.PlayersInPvpCannotJoin", "Players in PvP cannot join the party unless Allow Party in PvP is on"));
            return;
        }
        _removed.Remove(Id(targetInfo));
        _acked.Remove(peer);
        _asked[peer] = (Time.time + AnswerSeconds, false);
        Send(peer, Msg.Request, me);
        me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.AddingToTheParty", "Adding {0} to the party", targetInfo.m_name));
    }

    // A player joins this client's party, and every other member with this mod hears of it.
    private void Join(Player me, long peer, ZNet.PlayerInfo info, bool hasMod)
    {
        if (Members.ContainsKey(peer) || peer == PeerOf(me))
        {
            return;
        }
        Members[peer] = new PartyMember { Peer = peer, Name = info.m_name, User = info.m_userInfo, HasMod = hasMod };
        Save();
        me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.JoinedTheParty", "{0} joined the party", info.m_name));
        foreach (var other in Members.Values.Where(member => member.HasMod && member.Peer != peer).ToList())
        {
            SendRoster(other.Peer, me);
        }
        if (hasMod)
        {
            SendRoster(peer, me);
        }
    }

    // Takes a member off this client's party list only.
    public void Remove(long peer)
    {
        var me = Player.m_localPlayer;
        if (me == null || !Members.TryGetValue(peer, out var member))
        {
            return;
        }
        Members.Remove(peer);
        _removed.Add(member.User.m_id.ToString());
        Save();
        me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.RemovedFromYourPartyList", "{0} removed from your party list", member.Name));
    }

    // Leaves the party: every member with this mod takes this player off its list.
    public void Leave()
    {
        var me = Player.m_localPlayer;
        if (me == null)
        {
            return;
        }
        foreach (var member in Members.Values.Where(member => member.HasMod).ToList())
        {
            Send(member.Peer, Msg.Leave, me);
        }
        Members.Clear();
        Save();
        me.Message(MessageHud.MessageType.TopLeft, Lang.T("Party.YouLeftTheParty", "You left the party"));
    }

    // Starts a vote to take a member off the whole party.
    public void VoteToKick(long target)
    {
        var me = Player.m_localPlayer;
        if (me == null || !Members.ContainsKey(target) || _kicks.ContainsKey(target))
        {
            return;
        }
        var kick = new Kick { Target = target, Until = Time.time + _answerSeconds.Value };
        kick.Yes.Add(PeerOf(me));
        foreach (var member in Members.Values.Where(member => member.Peer != target))
        {
            if (member.HasMod)
            {
                SendKickVote(member.Peer, target, me);
            }
            else
            {
                kick.Yes.Add(member.Peer);
            }
        }
        _kicks[target] = kick;
        me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.VoteToKickStarted", "Vote to kick {0} started", Members[target].Name));
    }

    // At least half the party, the member being kicked counted, must vote yes.
    private void Votes(Player me, float now)
    {
        foreach (var kick in _kicks.Values.ToList())
        {
            var size = Members.Count + 1;
            var needed = Mathf.CeilToInt(size / 2f);
            if (kick.Yes.Count >= needed)
            {
                _kicks.Remove(kick.Target);
                var name = Members.TryGetValue(kick.Target, out var gone) ? gone.Name : "The player";
                foreach (var member in Members.Values.Where(member => member.HasMod).ToList())
                {
                    SendKicked(member.Peer, kick.Target, me);
                }
                Members.Remove(kick.Target);
                Save();
                me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.WasKickedFromTheParty", "{0} was kicked from the party", name));
            }
            else if (now > kick.Until || kick.No.Count > size - needed)
            {
                _kicks.Remove(kick.Target);
                me.Message(MessageHud.MessageType.TopLeft, Lang.T("Party.TheVoteToKickDid", "The vote to kick did not pass"));
            }
        }
    }

    public static long PeerOf(Player player) => player == null ? 0 : player.GetZDOID().UserID;

    private Player LookedAt(Player me)
    {
        var hover = me.GetHoverObject();
        var hovered = hover == null ? null : hover.GetComponentInParent<Player>();
        if (hovered != null && hovered != me)
        {
            return hovered;
        }
        var camera = GameCamera.instance == null ? null : GameCamera.instance.transform;
        if (camera == null)
        {
            return null;
        }
        return Player.GetAllPlayers()
            .Where(other => other != me)
            .Select(other => (Player: other, To: other.GetCenterPoint() - camera.position))
            .Where(pair => pair.To.magnitude <= _reach.Value && Vector3.Angle(camera.forward, pair.To) <= _aim.Value)
            .OrderBy(pair => Vector3.Angle(camera.forward, pair.To))
            .Select(pair => pair.Player)
            .FirstOrDefault();
    }

    private void Register()
    {
        var rpc = ZRoutedRpc.instance;
        if (rpc == null || rpc == _registered)
        {
            return;
        }
        _registered = rpc;
        rpc.Register<ZPackage>(Rpc, Receive);
    }

    private ZPackage Header(Msg kind, Player me)
    {
        var pkg = new ZPackage();
        pkg.Write((byte)kind);
        pkg.Write(me.IsPVPEnabled());
        pkg.Write(_allowPvp.Value);
        pkg.Write(OwnLevel(me));
        pkg.Write(me.GetMaxStamina());
        return pkg;
    }

    private void Send(long peer, Msg kind, Player me, ZPackage pkg = null)
    {
        if (ZRoutedRpc.instance != null)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, Rpc, pkg ?? Header(kind, me));
        }
    }

    private void SendRoster(long peer, Player me)
    {
        var pkg = Header(Msg.Roster, me);
        var others = Members.Values.Where(member => member.Peer != peer).ToList();
        pkg.Write(others.Count);
        foreach (var member in others)
        {
            pkg.Write(member.Peer);
            pkg.Write(member.HasMod);
        }
        Send(peer, Msg.Roster, me, pkg);
    }

    private void SendKickVote(long peer, long target, Player me)
    {
        var pkg = Header(Msg.KickVote, me);
        pkg.Write(target);
        Send(peer, Msg.KickVote, me, pkg);
    }

    private void SendVote(long peer, long target, bool yes, Player me)
    {
        var pkg = Header(Msg.Vote, me);
        pkg.Write(target);
        pkg.Write(yes);
        Send(peer, Msg.Vote, me, pkg);
    }

    private void SendKicked(long peer, long target, Player me)
    {
        var pkg = Header(Msg.Kicked, me);
        pkg.Write(target);
        Send(peer, Msg.Kicked, me, pkg);
    }

    private void Receive(long sender, ZPackage pkg)
    {
        try
        {
            var kind = (Msg)pkg.ReadByte();
            var senderPvp = pkg.ReadBool();
            var senderAllows = pkg.ReadBool();
            var level = pkg.ReadInt();
            var maxStamina = pkg.ReadSingle();
            Handle(sender, kind, senderPvp, senderAllows, level, maxStamina, pkg);
        }
        catch (Exception e)
        {
            _log.LogDebug($"Ignored a party message that could not be read ({e.GetBaseException().Message}).");
        }
    }

    private void Handle(long sender, Msg kind, bool senderPvp, bool senderAllows, int level, float maxStamina, ZPackage pkg)
    {
        var me = Player.m_localPlayer;
        var net = ZNet.instance;
        if (!_enabled.Value || me == null || net == null)
        {
            return;
        }
        var online = net.GetPlayerList().ToDictionary(p => p.m_characterID.UserID, p => p);
        if (!online.TryGetValue(sender, out var info))
        {
            return;
        }
        var pvpOk = !(senderPvp || me.IsPVPEnabled()) || (senderAllows && _allowPvp.Value);
        _acked.Add(sender);
        switch (kind)
        {
            case Msg.Request:
                Send(sender, Msg.Ack, me);
                if (!pvpOk)
                {
                    Send(sender, Msg.Decline, me);
                    me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.AskedYouToJoinA", "{0} asked you to join a party, but one of you has PvP on", info.m_name));
                    return;
                }
                _prompt = (sender, info.m_name, Time.time + _answerSeconds.Value, 0);
                me.Message(MessageHud.MessageType.Center, Lang.F("Party.AsksYouToJoinTheir", "{0} asks you to join their party: {1} to accept, {2} to decline", info.m_name, _acceptKey.Value, _declineKey.Value));
                return;
            case Msg.Rejoin:
                Send(sender, _persist.Value && pvpOk ? Msg.Accept : Msg.Decline, me);
                if (_persist.Value && pvpOk && !Members.ContainsKey(sender))
                {
                    Members[sender] = new PartyMember { Peer = sender, Name = info.m_name, User = info.m_userInfo, HasMod = true };
                    Save();
                }
                return;
            case Msg.Accept when pvpOk:
                _asked.Remove(sender);
                Join(me, sender, info, true);
                break;
            case Msg.Decline:
                if (_asked.Remove(sender))
                {
                    me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.DeclinedToJoinTheParty", "{0} declined to join the party", info.m_name));
                }
                return;
            case Msg.Leave:
                if (Members.Remove(sender))
                {
                    Save();
                    me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.LeftTheParty", "{0} left the party", info.m_name));
                }
                return;
            case Msg.Roster when Members.ContainsKey(sender):
                var count = pkg.ReadInt();
                for (var i = 0; i < count; i++)
                {
                    var peer = pkg.ReadLong();
                    var hasMod = pkg.ReadBool();
                    if (peer != PeerOf(me) && !Members.ContainsKey(peer) && online.TryGetValue(peer, out var other) && !_removed.Contains(Id(other)))
                    {
                        Members[peer] = new PartyMember { Peer = peer, Name = other.m_name, User = other.m_userInfo, HasMod = hasMod };
                    }
                }
                Save();
                break;
            case Msg.KickVote when Members.ContainsKey(sender):
                var target = pkg.ReadLong();
                var targetName = Members.TryGetValue(target, out var t) ? t.Name : "a member";
                _prompt = (sender, info.m_name, Time.time + _answerSeconds.Value, target);
                me.Message(MessageHud.MessageType.Center, Lang.F("Party.VotesToKickForYes", "{0} votes to kick {1}: {2} for yes, {3} for no", info.m_name, targetName, _acceptKey.Value, _declineKey.Value));
                return;
            case Msg.Vote:
                var voted = pkg.ReadLong();
                var yes = pkg.ReadBool();
                if (_kicks.TryGetValue(voted, out var kick))
                {
                    (yes ? kick.Yes : kick.No).Add(sender);
                }
                return;
            case Msg.Kicked when Members.ContainsKey(sender):
                var kicked = pkg.ReadLong();
                if (kicked == PeerOf(me))
                {
                    Members.Clear();
                    Save();
                    me.Message(MessageHud.MessageType.TopLeft, Lang.T("Party.YouWereKickedFromThe", "You were kicked from the party"));
                }
                else if (Members.TryGetValue(kicked, out var gone))
                {
                    Members.Remove(kicked);
                    Save();
                    me.Message(MessageHud.MessageType.TopLeft, Lang.F("Party.WasKickedFromTheParty", "{0} was kicked from the party", gone.Name));
                }
                return;
        }
        if (Members.TryGetValue(sender, out var member))
        {
            member.HasMod = true;
            member.Level = level;
            member.MaxStamina = maxStamina;
        }
    }

    // The level Skills Reworked keeps as a skill of its own, when that mod is installed.
    private static int OwnLevel(Player me)
    {
        if (SrApi == null || !Chainloader.PluginInfos.ContainsKey("M2Valheim.SkillsReworked"))
        {
            return -1;
        }
        try
        {
            var type = (Skills.SkillType)AccessTools.PropertyGetter(SrApi, "LevelSkillType").Invoke(null, null);
            return Mathf.FloorToInt(me.GetSkills().GetSkillLevel(type));
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
