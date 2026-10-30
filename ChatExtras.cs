using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal enum ChatDefault
{
    Say,
    Shout
}

internal enum Presence
{
    Online,
    Away,
    Invisible
}

// More for the chat window, on top of ChatFilters and ChatPlus: the window is an element of SeneaL UI's HUD editor, lines that mention you are highlighted with a notice, friends' names stand out and their arrivals are announced, players with this mod see each other as online or away (after a while without input), what you type can go out as a shout by default, a key opens the chat with a command started, and other players' shouts can be kept off the map.
internal sealed class ChatExtras
{
    private const string Section = "Chat";
    private const string Rpc = "alkeari.senealuiunlocker.presence";
    private const float BroadcastSeconds = 30f;

    private static ChatExtras _current;

    private readonly ConfigEntry<bool> _mentions;
    private readonly ConfigEntry<Color> _mentionColor;
    private readonly ConfigEntry<bool> _mentionNotice;
    private readonly ConfigEntry<string> _friends;
    private readonly ConfigEntry<Color> _friendColor;
    private readonly ConfigEntry<bool> _friendNotices;
    private readonly ConfigEntry<Presence> _presence;
    private readonly ConfigEntry<float> _awayMinutes;
    private readonly ConfigEntry<ChatDefault> _default;
    private readonly ConfigEntry<bool> _shoutPins;
    private readonly ConfigEntry<KeyboardShortcut> _commandKey;
    private readonly Dictionary<long, Presence> _seen = new();
    private ZRoutedRpc _registered;
    private float _nextBroadcast;
    private Presence _sent = (Presence)(-1);
    private float _lastInput;
    private Vector2 _lastPointer;
    private Vector3 _lastLook;
    private HashSet<string> _present;
    private bool _commandsAdded;
    private readonly ConfigEntry<bool> _element;
    private Chat _chatElement;

    public ChatExtras(ConfigFile cfg)
    {
        _current = this;
        _mentions = cfg.Bind(Section, "Highlight Mentions", true, "Highlight chat lines from other players that contain your character's name.");
        _mentionColor = cfg.Bind(Section, "Mention Color", new Color(0.95f, 0.76f, 0.31f, 0.25f), "Color of the highlight behind a line that mentions you.");
        _mentionNotice = cfg.Bind(Section, "Mention Notice", true, "Also show a notice at the top left of the screen when someone mentions you.");
        _friends = cfg.Bind(Section, "Friends", "", "Character names of your friends, separated by semicolons: their names stand out in the chat and their arrivals are announced.");
        _friendColor = cfg.Bind(Section, "Friend Color", new Color(0.55f, 0.85f, 0.75f, 1f), "Color of your friends' names in the chat window.");
        _friendNotices = cfg.Bind(Section, "Friend Notices", true, "Show a notice at the top left of the screen when a friend joins or leaves the server.");
        _presence = cfg.Bind(Section, "Status", Presence.Online,
            "How players with this mod see you: Online, Away (always), or Invisible (left out of their friend lists). Online turns to Away on its own after Away After Minutes without input.");
        _awayMinutes = cfg.Bind(Section, "Away After Minutes", 5f, new ConfigDescription("Minutes without any input after which you show as away to players with this mod; 0 never.", new AcceptableValueRange<float>(0f, 60f)));
        _default = cfg.Bind(Section, "Default Message Type", ChatDefault.Say, "What a chat line without a command is sent as: said to players near you, or shouted to everyone.");
        _shoutPins = cfg.Bind(Section, "Shout Marks On Map", true, "Show where players shouted from on the map, as the game does.");
        _commandKey = cfg.Bind("Hotkeys", "Chat Command Key", new KeyboardShortcut(KeyCode.Slash),
            "Opens the chat window with a command started (/), as Enter opens it for a message. Empty = no key.");
        _element = cfg.Bind(Section, "Chat In HUD Editor", true,
            "Make the chat window an element of SeneaL UI's HUD editor, so it can be moved, sized, faded and hidden there like SeneaL UI's own elements.");
        _lastInput = Time.time;
    }

    public static Presence StatusOf(long peer) => _current != null && _current._seen.TryGetValue(peer, out var presence) ? presence : Presence.Online;

    public static bool Seen(long peer) => _current != null && _current._seen.ContainsKey(peer);

    private HashSet<string> FriendNames => new(SettingsPage.Split(_friends.Value), StringComparer.OrdinalIgnoreCase);

    private Presence Own
    {
        get
        {
            if (_presence.Value != Presence.Online)
            {
                return _presence.Value;
            }
            return _awayMinutes.Value > 0f && Time.time - _lastInput > _awayMinutes.Value * 60f ? Presence.Away : Presence.Online;
        }
    }

    public void Tick()
    {
        // Input as the player's own movement, view and pointer, which is what anyone at the keyboard changes.
        var player = Player.m_localPlayer;
        var look = player == null ? Vector3.zero : player.transform.position + player.GetLookDir();
        var pointer = (Vector2)ZInput.pointerPosition;
        if ((look - _lastLook).sqrMagnitude > 0.0001f || pointer != _lastPointer)
        {
            _lastInput = Time.time;
            _lastLook = look;
            _lastPointer = pointer;
        }
        var rpc = ZRoutedRpc.instance;
        if (rpc != null && rpc != _registered)
        {
            _registered = rpc;
            _seen.Clear();
            rpc.Register<int>(Rpc, (sender, presence) => _seen[sender] = (Presence)presence);
        }
        if (rpc != null && Player.m_localPlayer != null && (Own != _sent || Time.time >= _nextBroadcast))
        {
            _sent = Own;
            _nextBroadcast = Time.time + BroadcastSeconds;
            // To each player rather than to everybody, so a vanilla server only relays it and never looks for a handler of its own.
            var me = ZNet.instance == null ? 0L : ZNet.instance.LocalPlayerCharacterID.UserID;
            foreach (var info in ZNet.instance == null ? [] : ZNet.instance.GetPlayerList())
            {
                if (info.m_characterID.UserID != me && info.m_characterID.UserID != 0L)
                {
                    rpc.InvokeRoutedRPC(info.m_characterID.UserID, Rpc, (int)_sent);
                }
            }
        }
        // The chat window joins SeneaL UI's HUD editor through a stand-in the size of its own area (ChatElement); SeneaL UI's own chat skin is drawn inside the chat box, so it moves and sizes with it.
        var chat = Chat.instance;
        if (_element.Value && chat != null && chat != _chatElement && chat.m_chatWindow != null && Hud.instance != null)
        {
            _chatElement = chat;
            var frame = ChatElement.Create(chat);
            if (frame != null)
            {
                SeneaLHud.Register("Chat", "Chat", frame.Frame);
                SeneaLHud.LookRoot("Chat", chat.m_chatWindow);
            }
        }
        Notices();
        Command();
        if (!_commandsAdded)
        {
            _commandsAdded = true;
            _ = new Terminal.ConsoleCommand("friends", "List your friends who are online, with their status.", _ => ListFriends());
        }
    }

    // A friend arriving or leaving, from the game's player list.
    private void Notices()
    {
        var net = ZNet.instance;
        if (net == null || Player.m_localPlayer == null)
        {
            _present = null;
            return;
        }
        var names = new HashSet<string>(net.GetPlayerList().Select(info => info.m_name), StringComparer.OrdinalIgnoreCase);
        if (_present != null && _friendNotices.Value)
        {
            var friends = FriendNames;
            foreach (var name in names.Where(name => !_present.Contains(name) && friends.Contains(name)))
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, Lang.F("ChatExtras.YourFriendIsHere", "Your friend {0} is here", name));
            }
            foreach (var name in _present.Where(name => !names.Contains(name) && friends.Contains(name)))
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, Lang.F("ChatExtras.YourFriendLeft", "Your friend {0} left", name));
            }
        }
        _present = names;
    }

    private void Command()
    {
        var chat = Chat.instance;
        if (chat == null || chat.m_input == null || chat.m_wasFocused || Player.m_localPlayer == null || Console.IsVisible() || TextInput.IsVisible()
            || Minimap.InTextInput() || Menu.IsVisible() || InventoryGui.IsVisible() || InventoryFeatures.Typing() || !KeyInput.Down(_commandKey.Value))
        {
            return;
        }
        chat.m_hideTimer = 0f;
        chat.m_chatWindow.gameObject.SetActive(true);
        chat.m_input.gameObject.SetActive(true);
        chat.m_input.text = "/";
        chat.m_input.ActivateInputField();
        chat.m_input.caretPosition = 1;
    }

    private void ListFriends()
    {
        var net = ZNet.instance;
        if (net == null || Chat.instance == null)
        {
            return;
        }
        var friends = FriendNames;
        var online = net.GetPlayerList().Where(info => friends.Contains(info.m_name)).ToList();
        var lines = online.Where(info => !_seen.TryGetValue(info.m_characterID.UserID, out var p) || p != Presence.Invisible)
            .Select(info => _seen.TryGetValue(info.m_characterID.UserID, out var p) ? $"{info.m_name} ({p.ToString().ToLowerInvariant()})" : info.m_name).ToList();
        Chat.instance.AddString(lines.Count == 0 ? "No friends online" : "Friends online: " + string.Join(", ", lines));
    }

    private static readonly Regex Line = new("^(?<head>.*?<color=[^>]*>)(?<name>[^<]*)(?<tail></color>: )(?<rest>.*)$", RegexOptions.Singleline);

    // Each chat line as the game builds it, "<color=orange>name</color>: text": a friend's name in the friend color, and a line mentioning you highlighted.
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), typeof(string))]
    internal static class LinePatch
    {
        private static void Prefix(Terminal __instance, ref string text)
        {
            var self = _current;
            if (self == null || !ServerRules.Allows("Chat") || __instance is not Chat || text == null)
            {
                return;
            }
            var match = Line.Match(text);
            if (!match.Success)
            {
                return;
            }
            var name = match.Groups["name"].Value.Trim();
            var me = Player.m_localPlayer == null ? "" : Player.m_localPlayer.GetPlayerName();
            if (self.FriendNames.Contains(name))
            {
                text = match.Groups["head"].Value + $"<color=#{ColorUtility.ToHtmlStringRGBA(self._friendColor.Value)}>{match.Groups["name"].Value}</color>" + match.Groups["tail"].Value + match.Groups["rest"].Value;
            }
            if (self._mentions.Value && me.Length > 0 && !name.Equals(me, StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(match.Groups["rest"].Value, @"\b" + Regex.Escape(me) + @"\b", RegexOptions.IgnoreCase))
            {
                text = $"<mark=#{ColorUtility.ToHtmlStringRGBA(self._mentionColor.Value)}>{text}</mark>";
                if (self._mentionNotice.Value)
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, Lang.F("ChatExtras.MentionedYou", "{0} mentioned you", name));
                }
            }
        }
    }

    // A line typed without a command goes out as a shout when that is the default.
    [HarmonyPatch(typeof(Chat), "InputText")]
    internal static class InputPatch
    {
        private static void Prefix(Chat __instance)
        {
            var self = _current;
            var text = __instance.m_input == null ? "" : __instance.m_input.text;
            if (self != null && ServerRules.Allows("Chat") && self._default.Value == ChatDefault.Shout && text.Length > 0 && text[0] != '/')
            {
                __instance.m_input.text = "/s " + text;
            }
        }
    }

    [HarmonyPatch(typeof(Chat), nameof(Chat.GetShoutWorldTexts))]
    internal static class ShoutPinsPatch
    {
        private static void Postfix(List<Chat.WorldTextInstance> texts)
        {
            if (_current != null && !_current._shoutPins.Value && ServerRules.Allows("Chat"))
            {
                texts.RemoveAll(text => text.m_type == Talker.Type.Shout);
            }
        }
    }
}
