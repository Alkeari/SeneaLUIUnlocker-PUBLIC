using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace SeneaLUIUnlocker;

// Asks alkeari.com once per game start for the newest released version and, after the player first spawns, tells them when it is newer than the one installed. A missing file, no network or a bad answer shows nothing.
internal sealed class UpdateNotice
{
    // Not const: a test run points it at a published project's file while this one has none.
    private static string Url = "https://alkeari.com/updates/seneal-ui-unlocker.json";

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<float> _delay;
    private readonly ConfigEntry<string> _text;
    private readonly ManualLogSource _log;
    private string _newest;
    private string _page;
    private float _shownAt = -1f;
    private bool _done;

    public UpdateNotice(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        _enabled = cfg.Bind("General", "Update Notice", true,
            "Once per game start, ask alkeari.com which version of this mod is newest, and show a message in the top left corner after you spawn when it is newer than the one installed. Off = no request is made.");
        _delay = cfg.Bind("General", "Update Notice Delay Seconds", 10f, new ConfigDescription(
            "How long after you first spawn in a world the update message appears.",
            new AcceptableValueRange<float>(0f, 120f)));
        _text = cfg.Bind("General", "Update Notice Text", "SeneaL UI Unlocker {newest} is out (you have {installed})\n{page}",
            "The update message. {newest} is the newest version, {installed} the one you have, {page} the mod's page; \\n starts a new message.");
    }

    public IEnumerator Check()
    {
        if (!_enabled.Value)
        {
            yield break;
        }
        using var request = UnityWebRequest.Get(Url);
        request.timeout = 15;
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            DebugLog.Write("update", $"no answer from {Url}: {request.error}");
            yield break;
        }
        // JsonUtility left lines empty for this file in game (newest blank at 22:35 on 2026-10-19 with a 200 answer), so the two values are read by pattern: every "version" in the file names a released build, and the highest one is the newest.
        var text = request.downloadHandler.text;
        var newest = Regex.Matches(text, "\"version\"\\s*:\\s*\"([^\"]*)\"").Cast<Match>().Select(match => Parse(match.Groups[1].Value)).Where(version => version != null).OrderByDescending(version => version).FirstOrDefault();
        var page = Regex.Match(text, "\"page\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
        var installed = Parse(PluginVersion.Value);
        DebugLog.Write("update", $"newest {newest}, installed {installed}");
        if (newest != null && installed != null && newest > installed)
        {
            _newest = newest.ToString();
            _page = page.Replace("https://", "").TrimEnd('/');
            _log.LogInfo($"SeneaL UI Unlocker {_newest} is out (installed {PluginVersion.Value}): {page}");
        }
    }

    public void Tick(float now)
    {
        if (_done || _newest == null || !_enabled.Value || Player.m_localPlayer == null || MessageHud.instance == null)
        {
            return;
        }
        if (_shownAt < 0f)
        {
            _shownAt = now + _delay.Value;
        }
        if (now < _shownAt)
        {
            return;
        }
        _done = true;
        // The top-left message area shows one line and cuts the rest, so each line of the text is its own message; the game shows them one after another.
        foreach (var line in _text.Value.Replace("{newest}", _newest).Replace("{installed}", PluginVersion.Value).Replace("{page}", _page).Split('\n'))
        {
            MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, line);
        }
    }

    private static System.Version Parse(string text) => System.Version.TryParse(text ?? "", out var version) ? version : null;
}
