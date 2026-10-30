using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Hides or recolors chat lines by message type in this player's own chat window only. The game builds each line as "<color=orange>name</color>: <color=#RRGGBBAA>text</color>"; only that text color is changed, so SeneaL UI's chat skin, which restyles the name color, still applies.
internal static class ChatFilters
{
    private const string Section = "Chat";

    private static ConfigEntry<bool> _showNormal;
    private static ConfigEntry<bool> _showShout;
    private static ConfigEntry<bool> _showWhisper;
    private static ConfigEntry<Color> _normalColor;
    private static ConfigEntry<Color> _shoutColor;
    private static ConfigEntry<Color> _whisperColor;
    private static string _pending;
    private static ConfigEntry<bool> _timestamps;
    private static ConfigEntry<string> _timeFormat;
    private static ConfigEntry<bool> _joins;
    private static ConfigEntry<bool> _keepCase;
    private static string _original;
    private static HashSet<string> _present;
    private static float _nextRoster;

    private static readonly Color GameNormal = Color.white;
    private static readonly Color GameShout = Color.yellow;
    private static readonly Color GameWhisper = new(1f, 1f, 1f, 0.75f);

    public static void Bind(ConfigFile cfg)
    {
        _showNormal = cfg.Bind(Section, "Show Normal Messages", true, "Show normal chat messages in the chat window.");
        _showShout = cfg.Bind(Section, "Show Shout Messages", true, "Show shouted chat messages in the chat window.");
        _showWhisper = cfg.Bind(Section, "Show Whisper Messages", true, "Show whispered chat messages in the chat window.");
        _normalColor = cfg.Bind(Section, "Normal Message Color", GameNormal, "Text color of normal chat messages in the chat window; the game's own is white.");
        _shoutColor = cfg.Bind(Section, "Shout Message Color", GameShout, "Text color of shouted chat messages in the chat window; the game's own is yellow.");
        _whisperColor = cfg.Bind(Section, "Whisper Message Color", GameWhisper, "Text color of whispered chat messages in the chat window; the game's own is faded white.");
        _timestamps = cfg.Bind(Section, "Timestamps", true, "Start every line in the chat window with the time it arrived.");
        _timeFormat = cfg.Bind(Section, "Timestamp Format", "HH:mm", "How the time is written: HH hours, mm minutes, ss seconds, tt AM or PM (for example hh:mm tt).");
        _joins = cfg.Bind(Section, "Join And Leave Notices", true, "Write a line in the chat window when a player joins or leaves the server.");
        _keepCase = cfg.Bind(Section, "Keep Message Case", true, "Show shouts and whispers as they were typed; the game writes shouts in capitals and whispers in lowercase.");
    }

    private static bool Show(Talker.Type type) => type switch
    {
        Talker.Type.Normal => _showNormal.Value,
        Talker.Type.Shout => _showShout.Value,
        Talker.Type.Whisper => _showWhisper.Value,
        _ => true
    };

    private static string Recolor(Talker.Type type) => type switch
    {
        Talker.Type.Normal => Swap(GameNormal, _normalColor.Value),
        Talker.Type.Shout => Swap(GameShout, _shoutColor.Value),
        Talker.Type.Whisper => Swap(GameWhisper, _whisperColor.Value),
        _ => null
    };

    private static string Swap(Color game, Color wanted) =>
        game == wanted ? null : ColorUtility.ToHtmlStringRGBA(game) + ">" + ColorUtility.ToHtmlStringRGBA(wanted);

    private static bool Begin(Terminal terminal, Talker.Type type)
    {
        if (terminal is not Chat || _showNormal == null || !ServerRules.Allows("Chat"))
        {
            return true;
        }
        if (!Show(type))
        {
            return false;
        }
        _pending = Recolor(type);
        return true;
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), typeof(PlatformUserID), typeof(string), typeof(Talker.Type), typeof(bool))]
    internal static class PlayerLinePatch
    {
        private static bool Prefix(Terminal __instance, Talker.Type type, string text)
        {
            _original = type is Talker.Type.Shout or Talker.Type.Whisper ? text : null;
            return Begin(__instance, type);
        }

        private static void Postfix()
        {
            _pending = null;
            _original = null;
        }
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), typeof(string), typeof(string), typeof(Talker.Type), typeof(bool))]
    internal static class TitledLinePatch
    {
        private static bool Prefix(Terminal __instance, Talker.Type type, string text)
        {
            _original = type is Talker.Type.Shout or Talker.Type.Whisper ? text : null;
            return Begin(__instance, type);
        }

        private static void Postfix()
        {
            _pending = null;
            _original = null;
        }
    }

    // Every player arriving or leaving, from the game's player list, once a second.
    public static void Tick(float now)
    {
        var net = ZNet.instance;
        if (_joins == null || !ServerRules.Allows("Chat") || net == null || Chat.instance == null || Player.m_localPlayer == null || now < _nextRoster)
        {
            if (net == null)
            {
                _present = null;
            }
            return;
        }
        _nextRoster = now + 1f;
        var names = new HashSet<string>(net.GetPlayerList().Select(info => info.m_name));
        if (_present != null && _joins.Value)
        {
            foreach (var name in names.Where(name => !_present.Contains(name)))
            {
                Chat.instance.AddString($"<color=#{ColorUtility.ToHtmlStringRGB(SeneaLTheme.Color("Gold", new Color(0.95f, 0.78f, 0.3f)))}>{name} joined</color>");
            }
            foreach (var name in _present.Where(name => !names.Contains(name)))
            {
                Chat.instance.AddString($"<color=#{ColorUtility.ToHtmlStringRGB(SeneaLTheme.Color("Ash", new Color(0.66f, 0.61f, 0.53f)))}>{name} left</color>");
            }
        }
        _present = names;
    }

    // The game changed the case of a shout or whisper on its way here; the text as typed goes back in.
    private static string Recase(string text)
    {
        if (_original == null || _keepCase == null || !_keepCase.Value)
        {
            return text;
        }
        foreach (var changed in new[] { _original.ToUpper(), _original.ToLowerInvariant() })
        {
            var at = text.LastIndexOf(changed, System.StringComparison.Ordinal);
            if (at >= 0)
            {
                return text.Substring(0, at) + _original + text.Substring(at + changed.Length);
            }
        }
        return text;
    }

    private static string Stamp(string text)
    {
        if (_timestamps == null || !_timestamps.Value || text.StartsWith("[", System.StringComparison.Ordinal))
        {
            return text;
        }
        string time;
        try
        {
            time = System.DateTime.Now.ToString(_timeFormat.Value);
        }
        catch (System.FormatException)
        {
            time = System.DateTime.Now.ToString("HH:mm");
        }
        return $"<color=#{ColorUtility.ToHtmlStringRGB(SeneaLTheme.Color("Ash", new Color(0.66f, 0.61f, 0.53f)))}>[{time}]</color> {text}";
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), typeof(string))]
    internal static class LinePatch
    {
        private static void Prefix(Terminal __instance, ref string text)
        {
            if (__instance is Chat && ServerRules.Allows("Chat"))
            {
                text = Stamp(Recase(text));
            }
            if (_pending == null)
            {
                return;
            }
            var parts = _pending.Split('>');
            var from = "</color>: <color=#" + parts[0] + ">";
            var at = text.IndexOf(from, System.StringComparison.Ordinal);
            if (at >= 0)
            {
                text = text.Substring(0, at) + "</color>: <color=#" + parts[1] + ">" + text.Substring(at + from.Length);
            }
            _pending = null;
        }
    }
}
