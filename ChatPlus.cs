using System;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Chat additions: how long the chat window stays up after a message (or always), party chat (/p message) to the party members who run this mod, and direct messages (/dm name message, /r message to answer the last one) between players who run it. The messages travel over the game's routed RPC, which a vanilla server relays.
internal sealed class ChatPlus
{
    private const string Section = "Chat";
    private const string Rpc = "alkeari.senealuiunlocker.chat";

    private enum Kind : byte
    {
        Party = 1,
        Direct = 2
    }

    private readonly Party _party;
    private readonly ConfigEntry<float> _hideDelay;
    private readonly ConfigEntry<bool> _alwaysShown;
    private readonly ConfigEntry<bool> _commands;
    private ZRoutedRpc _registered;
    private bool _commandsAdded;
    private long _lastFrom;

    public ChatPlus(ConfigFile cfg, Party party)
    {
        _party = party;
        _hideDelay = cfg.Bind(Section, "Chat Hide Seconds", 10f, new ConfigDescription(
            "How long the chat window stays up after a message; the game's own is 10.", new AcceptableValueRange<float>(2f, 120f)));
        _alwaysShown = cfg.Bind(Section, "Keep Chat Shown", false, "Keep the chat window up all the time.");
        _commands = cfg.Bind(Section, "Party And Direct Messages", true,
            "Chat commands /p message (to your party) and /dm name message, /r message (to one player); both players need this mod.");
    }

    public void Tick()
    {
        var chat = Chat.instance;
        if (chat != null)
        {
            var delay = _alwaysShown.Value ? 1e6f : _hideDelay.Value;
            if (!Mathf.Approximately(chat.m_hideDelay, delay))
            {
                chat.m_hideDelay = delay;
            }
            // The chat window is an element of the HUD editor, so it stays up while the editor is open in HUD mode, where it can be found and placed.
            if (SeneaLHud.Editing(ElementMode.Hud))
            {
                chat.m_hideTimer = 0f;
            }
        }
        var rpc = ZRoutedRpc.instance;
        if (rpc != null && rpc != _registered)
        {
            _registered = rpc;
            rpc.Register<ZPackage>(Rpc, Receive);
        }
        if (!_commandsAdded)
        {
            _commandsAdded = true;
            _ = new Terminal.ConsoleCommand("p", "[message] Send a message to your party (players with SeneaL UI Unlocker).", args => Party(args.ArgsAll));
            _ = new Terminal.ConsoleCommand("dm", "[name] [message] Send a message to one player with SeneaL UI Unlocker.", args =>
            {
                if (args.Length < 3)
                {
                    Line("Use /dm name message");
                    return;
                }
                Direct(args.Args[1], string.Join(" ", args.Args.Skip(2)));
            });
            _ = new Terminal.ConsoleCommand("r", "[message] Answer the last direct message.", args => Reply(args.ArgsAll));
        }
    }

    private void Party(string text)
    {
        if (!_commands.Value || !ServerRules.Allows("Party") || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var members = _party.Members.Values.Where(member => member.HasMod).ToList();
        if (members.Count == 0)
        {
            Line("Nobody in your party has SeneaL UI Unlocker to read party chat");
            return;
        }
        foreach (var member in members)
        {
            Send(member.Peer, Kind.Party, text);
        }
        Show(Kind.Party, Player.m_localPlayer.GetPlayerName(), text);
    }

    private void Direct(string name, string text)
    {
        if (!_commands.Value || !ServerRules.Allows("Party") || ZNet.instance == null)
        {
            return;
        }
        var target = ZNet.instance.GetPlayerList().FirstOrDefault(info => info.m_name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (target.m_name == null)
        {
            Line($"No player named {name} is online");
            return;
        }
        Send(target.m_characterID.UserID, Kind.Direct, text);
        Show(Kind.Direct, "To " + target.m_name, text);
    }

    private void Reply(string text)
    {
        var from = ZNet.instance == null ? null : ZNet.instance.GetPlayerList().Where(info => info.m_characterID.UserID == _lastFrom).Select(info => info.m_name).FirstOrDefault();
        if (from == null)
        {
            Line("Nobody to answer");
            return;
        }
        Direct(from, text);
    }

    private static void Send(long peer, Kind kind, string text)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }
        var pkg = new ZPackage();
        pkg.Write((byte)kind);
        pkg.Write(text.Length > 500 ? text.Substring(0, 500) : text);
        ZRoutedRpc.instance.InvokeRoutedRPC(peer, Rpc, pkg);
    }

    private void Receive(long sender, ZPackage pkg)
    {
        try
        {
            var kind = (Kind)pkg.ReadByte();
            var text = pkg.ReadString();
            var name = ZNet.instance == null ? "?" : ZNet.instance.GetPlayerList().Where(info => info.m_characterID.UserID == sender).Select(info => info.m_name).FirstOrDefault() ?? "?";
            if (kind == Kind.Party && !_party.Members.ContainsKey(sender))
            {
                return;
            }
            if (kind == Kind.Direct)
            {
                _lastFrom = sender;
                name = "From " + name;
            }
            Show(kind, name, text);
        }
        catch (Exception)
        {
        }
    }

    private static void Show(Kind kind, string name, string text)
    {
        var tag = kind == Kind.Party ? "Party" : "Message";
        var color = kind == Kind.Party ? "#6fcfb0" : "#d39be8";
        if (Chat.instance != null)
        {
            Chat.instance.AddString($"<color={color}>[{tag}] {name}</color>: {text}");
        }
        if (Chat.instance != null)
        {
            Chat.instance.m_hideTimer = 0f;
        }
    }

    private static void Line(string text)
    {
        if (Chat.instance != null)
        {
            Chat.instance.AddString($"<color=#a99c86>{text}</color>");
        }
    }
}
