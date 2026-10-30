using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Alerts, the ignore list and the line-of-sight rule for SeneaL Radar, read from the radar's own public members by name; the radar's code is never patched. Both follow SeneaL UI's server policy: no radar, no alerts, and no monster alerts where the server hides monsters.
internal sealed class RadarAlerts
{
    private const string Section = "Radar";
    public const string RadarGuid = "seneaL.valheim.ui.radar";
    private const float CheckSeconds = 0.5f;

    private static readonly Type Policy = TypeLookup.Find("SeneaLUI.Net.Policy");
    private static readonly PropertyInfo PolicyRadar = Policy == null ? null : AccessTools.Property(Policy, nameof(SeneaLUI.Net.Policy.Radar));
    private static readonly PropertyInfo PolicyMonsters = Policy == null ? null : AccessTools.Property(Policy, nameof(SeneaLUI.Net.Policy.RadarMonsters));

    private readonly ConfigEntry<bool> _alerts;
    private readonly ConfigEntry<bool> _bosses;
    private readonly ConfigEntry<bool> _starred;
    private readonly ConfigEntry<string> _named;
    private readonly ConfigEntry<bool> _sound;
    private readonly ConfigEntry<string> _ignored;
    private readonly HashSet<Character> _alerted = [];
    private readonly RadarDots _dots;
    private readonly PropertyInfo _range;
    private readonly PropertyInfo _radarOn;
    private readonly ConfigEntry<bool> _sight;
    private readonly ConfigEntry<float> _memory;
    private readonly Dictionary<Kind, ConfigEntry<bool>> _kindNeedsSight = [];
    private HashSet<string> _namedSet = [];
    private HashSet<string> _ignoredSet = [];
    private float _next;

    private RadarAlerts(ConfigFile cfg, Type plugin, Type blips)
    {
        _alerts = cfg.Bind(Section, "Radar Alerts", false,
            "Show a message in the top left corner when a creature picked below comes within SeneaL Radar's range, once until it leaves that range. Off by default, since the messages come often.");
        _bosses = cfg.Bind(Section, "Alert On Bosses", true, "Radar Alerts include bosses.");
        _starred = cfg.Bind(Section, "Alert On Starred Creatures", true, "Radar Alerts include creatures with one or more stars.");
        _named = cfg.Bind(Section, "Radar Alert Creatures", "",
            "Creatures Radar Alerts always include, by the name the game shows, separated by semicolons (for example Troll; Serpent).");
        _sound = cfg.Bind(Section, "Radar Alert Sound", true, "Play the game's menu select sound with each radar alert.");
        _ignored = cfg.Bind(Section, "Radar Ignored Creatures", "",
            "Creatures SeneaL Radar does not draw on the minimap or the world map and Radar Alerts never name, by the name the game shows, separated by semicolons (for example Deer; Neck).");
        _named.SettingChanged += (_, _) => _namedSet = Names(_named.Value);
        _namedSet = Names(_named.Value);
        _ignored.SettingChanged += (_, _) => _ignoredSet = Names(_ignored.Value);
        _ignoredSet = Names(_ignored.Value);
        _range = AccessTools.Property(blips, "Range");
        _radarOn = AccessTools.Property(plugin, "On");
        _sight = cfg.Bind(Section, "Radar Needs Line Of Sight", true,
            "SeneaL Radar shows a creature, trader or fish only while you could see it from where you stand (terrain, rock and buildings block the view), so the radar never shows what is hidden from you, and radar alerts follow it.");
        _memory = cfg.Bind(Section, "Radar Sight Memory Seconds", 2f, new ConfigDescription(
            "Seconds a dot stays after what it shows goes out of sight, so it does not flicker as it passes behind a tree or a rock; 0 hides it at once.", new AcceptableValueRange<float>(0f, 10f)));
        NeedsSight(cfg, Kind.Monster, "Monsters", true, "creatures hostile to you");
        NeedsSight(cfg, Kind.Boss, "Bosses", true, "bosses");
        NeedsSight(cfg, Kind.Animal, "Animals", true, "animals that do not attack, such as deer and hares");
        NeedsSight(cfg, Kind.Neutral, "Neutral Creatures", true, "creatures that are not hostile to you, such as the Dvergr");
        NeedsSight(cfg, Kind.Trader, "Traders", true, "traders");
        NeedsSight(cfg, Kind.Fish, "Fish", true, "fish");
        NeedsSight(cfg, Kind.Tamed, "Tames", false, "tamed creatures; off by default, so your tames show wherever they are within range");
        _dots = RadarDots.Resolve(blips, Hidden);
    }

    private bool Hidden(Transform who)
    {
        if (who == null)
        {
            return false;
        }
        var character = who.GetComponent<Character>();
        return Ignored(who, character) || !InSight(who, character);
    }

    private bool Ignored(Transform who, Character character)
    {
        if (_ignoredSet.Count == 0 || !ServerRules.Allows("RadarExtras"))
        {
            return false;
        }
        var name = character != null ? character.m_name : who.GetComponent<Trader>() is { } trader && trader != null ? trader.m_name : who.GetComponent<Fish>() is { } fish && fish != null ? fish.m_name : null;
        return name != null && _ignoredSet.Contains(Localization.instance.Localize(name).ToLowerInvariant());
    }

    private void NeedsSight(ConfigFile cfg, Kind kind, string name, bool on, string what) =>
        _kindNeedsSight[kind] = cfg.Bind(Section, $"{name} Need Line Of Sight", on, $"Radar Needs Line of Sight applies to {what}.");

    private bool InSight(Transform who, Character character) =>
        !_sight.Value || !_kindNeedsSight[KindOf(who, character)].Value || Sight.Seen(who, _memory.Value);

    private enum Kind
    {
        Monster,
        Boss,
        Animal,
        Neutral,
        Trader,
        Fish,
        Tamed
    }

    // The same split SeneaL Radar draws its dots by.
    private static Kind KindOf(Transform who, Character c)
    {
        if (c == null)
        {
            return who.GetComponent<Fish>() != null ? Kind.Fish : Kind.Trader;
        }
        if (c.IsBoss())
        {
            return Kind.Boss;
        }
        if (c.IsTamed())
        {
            return Kind.Tamed;
        }
        var player = Player.m_localPlayer;
        if (player != null && !BaseAI.IsEnemy(player, c))
        {
            return Kind.Neutral;
        }
        var ai = c.GetBaseAI();
        var hunting = c is Humanoid && ai is MonsterAI && ai.HaveTarget();
        return ai is AnimalAI || c.GetFaction() == Character.Faction.AnimalsVeg && !hunting ? Kind.Animal : Kind.Monster;
    }

    public static RadarAlerts Resolve(ConfigFile cfg, BepInEx.Logging.ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.ContainsKey(RadarGuid))
        {
            return null;
        }
        var plugin = Quiet.Type("SeneaLUI.Radar.RadarPlugin");
        var blips = Quiet.Type("SeneaLUI.Radar.Blips");
        if (plugin == null || blips == null || PolicyRadar == null || PolicyMonsters == null
            || AccessTools.Property(blips, "Range") == null || AccessTools.Property(plugin, "On") == null)
        {
            log.LogWarning("SeneaL Radar has an unknown shape; radar alerts and the radar ignore list are off.");
            return null;
        }
        return new RadarAlerts(cfg, plugin, blips);
    }

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        if (!_alerts.Value || !ServerRules.Allows("RadarExtras") || player == null || now < _next)
        {
            return;
        }
        _next = now + CheckSeconds;
        if (!RadarShown())
        {
            _alerted.Clear();
            return;
        }
        var range = (float)_range.GetValue(null, null);
        var monsters = (bool)PolicyMonsters.GetValue(null, null);
        var at = player.transform.position;
        var inRange = new HashSet<Character>();
        foreach (var c in Character.GetAllCharacters())
        {
            if (c == null || c.IsPlayer() || c.IsDead() || c.GetFaction() == Character.Faction.TrainingDummy || Utils.DistanceXZ(c.transform.position, at) > range
                || !InSight(c.transform, c) || Ignored(c.transform, c))
            {
                continue;
            }
            var why = Reason(player, c, monsters);
            if (why == null)
            {
                continue;
            }
            inRange.Add(c);
            if (_alerted.Add(c))
            {
                Alert(player, Lang.F("RadarAlerts.OnRadar", "{0} on the radar, {1} m", why, Mathf.RoundToInt(Utils.DistanceXZ(c.transform.position, at))));
            }
        }
        _alerted.RemoveWhere(c => c == null || !inRange.Contains(c));
    }

    public void BeforeRender() => _dots?.Hide(RadarShown());

    private bool RadarShown() =>
        _radarOn.GetValue(null, null) is ConfigEntry<bool> { Value: true } && Convert.ToInt32(PolicyRadar.GetValue(null, null)) != 0;

    private string Reason(Player player, Character c, bool monsters)
    {
        var name = Localization.instance.Localize(c.m_name);
        var hostile = c.IsBoss() || !c.IsTamed() && BaseAI.IsEnemy(player, c);
        if (hostile && !monsters)
        {
            return null;
        }
        if (_namedSet.Contains(name.ToLowerInvariant()))
        {
            return name;
        }
        if (c.IsBoss())
        {
            return _bosses.Value ? Lang.F("RadarAlerts.Boss", "Boss {0}", name) : null;
        }
        var stars = c.GetLevel() - 1;
        return _starred.Value && stars > 0 && hostile ? Lang.N("RadarAlerts.Starred", stars, "{1} with {0} star", "{1} with {0} stars", name) : null;
    }

    private void Alert(Player player, string text)
    {
        player.Message(MessageHud.MessageType.TopLeft, text);
        if (_sound.Value && Hud.instance != null)
        {
            Hud.instance.m_selectItemEffect.Create(Hud.instance.transform.position, Quaternion.identity);
        }
    }

    internal static HashSet<string> Names(string list) =>
        list.Split(';').Select(part => part.Trim().ToLowerInvariant()).Where(part => part.Length > 0).ToHashSet();

    // The radar redraws its dots in its LateUpdate; hiding the ignored and unseen ones just before the canvases render means the radar's next pass sees them as already drawn and leaves them hidden.
    private sealed class RadarDots
    {
        private readonly PropertyInfo _current;
        private readonly PropertyInfo _small;
        private readonly PropertyInfo _large;
        private readonly MethodInfo _who;
        private readonly Func<Transform, bool> _hidden;
        private PropertyInfo _made;
        private MethodInfo _dot;

        private RadarDots(PropertyInfo current, PropertyInfo small, PropertyInfo large, MethodInfo who, Func<Transform, bool> hidden)
        {
            _current = current;
            _small = small;
            _large = large;
            _who = who;
            _hidden = hidden;
        }

        public static RadarDots Resolve(Type blips, Func<Transform, bool> hidden)
        {
            var current = AccessTools.Property(blips, "Current");
            var small = AccessTools.Property(blips, "Small");
            var large = AccessTools.Property(blips, "Large");
            var who = AccessTools.Method(blips, "Who", [typeof(int)]);
            return current == null || small == null || large == null || who == null ? null : new RadarDots(current, small, large, who, hidden);
        }

        public void Hide(bool shown)
        {
            var blips = _current.GetValue(null, null);
            if (blips == null || !shown)
            {
                return;
            }
            Layer(blips, _small.GetValue(blips, null));
            Layer(blips, _large.GetValue(blips, null));
        }

        private void Layer(object blips, object layer)
        {
            if (layer == null)
            {
                return;
            }
            _made ??= AccessTools.Property(layer.GetType(), "Made");
            _dot ??= AccessTools.Method(layer.GetType(), "Dot");
            if (_made == null || _dot == null)
            {
                return;
            }
            var made = (int)_made.GetValue(layer, null);
            var args = new object[4];
            for (var i = 0; i < made; i++)
            {
                args[0] = i;
                if (!(bool)_dot.Invoke(layer, args) || args[3] is not RectTransform dot || dot == null)
                {
                    continue;
                }
                var show = !_hidden(_who.Invoke(blips, [i]) as Transform);
                if (dot.gameObject.activeSelf != show)
                {
                    dot.gameObject.SetActive(show);
                }
            }
        }
    }

    // Line of sight from the player's eyes to a creature, against the layers SeneaL UI's own ore detection tests (terrain, rock and building pieces), checked at most every fifth of a second for each one.
    private static class Sight
    {
        private const float CheckSeconds = 0.2f;
        private static readonly Dictionary<Transform, (float Checked, float Seen)> Checks = [];
        private static int _mask = -1;

        public static bool Seen(Transform who, float memory)
        {
            var player = Player.m_localPlayer;
            if (player == null || who == null)
            {
                return true;
            }
            var now = Time.time;
            if (!Checks.TryGetValue(who, out var entry) || now - entry.Checked >= CheckSeconds)
            {
                if (Checks.Count > 512)
                {
                    foreach (var dead in Checks.Keys.Where(key => key == null).ToList())
                    {
                        Checks.Remove(dead);
                    }
                }
                var seen = Clear(player.GetEyePoint(), who) ? now : Checks.TryGetValue(who, out var old) ? old.Seen : float.NegativeInfinity;
                entry = (now, seen);
                Checks[who] = entry;
            }
            return now - entry.Seen <= memory + CheckSeconds;
        }

        private static bool Clear(Vector3 eye, Transform who)
        {
            if (_mask < 0)
            {
                _mask = LayerMask.GetMask("terrain", "static_solid", "piece");
            }
            var character = who.GetComponent<Character>();
            var center = character != null ? character.GetCenterPoint() : who.position + Vector3.up * 0.5f;
            var top = character != null ? character.GetTopPoint() : who.position + Vector3.up;
            return !Physics.Linecast(eye, center, _mask, QueryTriggerInteraction.Ignore) || !Physics.Linecast(eye, top, _mask, QueryTriggerInteraction.Ignore);
        }
    }
}