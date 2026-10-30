using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Fades chosen SeneaL UI HUD elements out while the player is out of combat. Each element's layer gets a CanvasGroup of this plugin's own, which multiplies with SeneaL UI's groups, so no SeneaL UI setting is written and its own hiding, fading and HUD editor keep working.
internal sealed class CombatHud
{
    private const string Section = "Combat HUD";

    private static readonly Type HudRootType = TypeLookup.Find("SeneaLUI.Hud.HudRoot");
    private static readonly Type ElType = TypeLookup.Find("SeneaLUI.Hud.HudRoot+El");
    private static readonly Type EditorType = TypeLookup.Find("SeneaLUI.Hud.HudEditor");
    private static readonly FieldInfo Els = HudRootType == null ? null : AccessTools.Field(HudRootType, nameof(SeneaLUI.Hud.HudRoot._els));
    private static readonly FieldInfo ElPrefs = ElType == null ? null : AccessTools.Field(ElType, nameof(SeneaLUI.Hud.HudRoot.El.P));
    private static readonly FieldInfo ElLayer = ElType == null ? null : AccessTools.Field(ElType, nameof(SeneaLUI.Hud.HudRoot.El.Layer));
    private static readonly FieldInfo ElLayer2 = ElType == null ? null : AccessTools.Field(ElType, nameof(SeneaLUI.Hud.HudRoot.El.Layer2));
    private static readonly Type ElemType = TypeLookup.Find("SeneaLUI.Core.Prefs+Elem");
    private static readonly FieldInfo ElemKey = ElemType == null ? null : AccessTools.Field(ElemType, nameof(SeneaLUI.Core.Prefs.Elem.Key));
    private static readonly PropertyInfo EditorOpen = EditorType == null ? null : AccessTools.Property(EditorType, nameof(SeneaLUI.Hud.HudEditor.Open));

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<string> _elements;
    private readonly ConfigEntry<float> _linger;
    private readonly ConfigEntry<float> _enemyRange;
    private readonly ConfigEntry<float> _fade;
    private readonly Dictionary<RectTransform, CanvasGroup> _groups = [];
    private HashSet<string> _keys = [];
    private Player _watched;
    private float _lastHurt = float.NegativeInfinity;
    private float _alpha = 1f;

    public CombatHud(ConfigFile cfg)
    {
        _enabled = cfg.Bind(Section, "Hide Out Of Combat", true,
            "Fade the SeneaL UI HUD elements listed in Combat Only Elements out while you are out of combat and back in when combat starts.");
        _elements = cfg.Bind(Section, "Combat Only Elements", "",
            "The SeneaL UI HUD elements shown only in combat: pick any number from the list (in the settings file, their keys from SeneaL UI's [Elements] settings separated by semicolons, such as Health; Stamina; HotbarFood). None = nothing is hidden.");
        _linger = cfg.Bind(Section, "Combat Linger Seconds", 10f, new ConfigDescription(
            "How long combat lasts after you last took damage or attacked.", new AcceptableValueRange<float>(1f, 120f)));
        _enemyRange = cfg.Bind(Section, "Combat Enemy Range", 30f, new ConfigDescription(
            "An enemy within this many meters that has noticed you also counts as combat.", new AcceptableValueRange<float>(0f, 100f)));
        _fade = cfg.Bind(Section, "Combat Fade Seconds", 0.33f, new ConfigDescription(
            "How long the combat-only elements take to fade in or out; 0 shows and hides them at once.", new AcceptableValueRange<float>(0f, 5f)));
        _elements.SettingChanged += (_, _) => ParseKeys();
        ParseKeys();
    }

    private static bool Available => Els != null && ElPrefs != null && ElLayer != null && ElLayer2 != null && ElemKey != null;

    public void Tick()
    {
        if (!Available)
        {
            return;
        }
        var player = Player.m_localPlayer;
        Watch(player);
        var hide = _enabled.Value && ServerRules.Allows("CombatHud") && _keys.Count > 0 && player != null && !InCombat(player) && !(EditorOpen != null && (bool)EditorOpen.GetValue(null, null));
        _alpha = Mathf.MoveTowards(_alpha, hide ? 0f : 1f, _fade.Value <= 0f ? 1f : Time.unscaledDeltaTime / _fade.Value);
        foreach (var el in (Array)Els.GetValue(null))
        {
            if (el == null)
            {
                continue;
            }
            var prefs = ElPrefs.GetValue(el);
            var listed = prefs != null && ElemKey.GetValue(prefs) is string key && _keys.Contains(key.ToLowerInvariant());
            Apply(ElLayer.GetValue(el) as RectTransform, listed ? _alpha : 1f);
            Apply(ElLayer2.GetValue(el) as RectTransform, listed ? _alpha : 1f);
        }
        foreach (var gone in _groups.Keys.Where(layer => layer == null).ToList())
        {
            _groups.Remove(gone);
        }
    }

    private void Apply(RectTransform layer, float alpha)
    {
        if (layer == null)
        {
            return;
        }
        if (!_groups.TryGetValue(layer, out var group) || group == null)
        {
            if (alpha >= 1f)
            {
                return;
            }
            group = layer.gameObject.AddComponent<CanvasGroup>();
            if (group == null)
            {
                return;
            }
            group.interactable = false;
            group.blocksRaycasts = false;
            _groups[layer] = group;
        }
        if (group.alpha != alpha)
        {
            group.alpha = alpha;
        }
    }

    private bool InCombat(Player player)
    {
        var now = Time.time;
        if (player.IsTargeted() || now - _lastHurt < _linger.Value || player.m_lastCombatTimer < _linger.Value)
        {
            return true;
        }
        var at = player.transform.position;
        return BaseAI.BaseAIInstances.Any(ai => ai != null && ai.IsAlerted() && ai.m_character != null && !ai.m_character.IsDead()
            && BaseAI.IsEnemy(player, ai.m_character) && Vector3.Distance(ai.transform.position, at) < _enemyRange.Value);
    }

    private void Watch(Player player)
    {
        if (player == _watched)
        {
            return;
        }
        if (_watched != null)
        {
            _watched.m_onDamaged -= Hurt;
        }
        _watched = player;
        _lastHurt = float.NegativeInfinity;
        if (player != null)
        {
            player.m_onDamaged += Hurt;
        }
    }

    private void Hurt(float damage, Character attacker)
    {
        if (damage > 0f)
        {
            _lastHurt = Time.time;
        }
    }

    private void ParseKeys() =>
        _keys = _elements.Value.Split(';').Select(part => part.Trim().ToLowerInvariant()).Where(part => part.Length > 0).ToHashSet();
}
