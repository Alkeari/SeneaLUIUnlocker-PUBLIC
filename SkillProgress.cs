using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// Skill Progress: a card, an element of SeneaL UI's HUD editor, that shows the skills the local player just gained experience in, each with its level, its share of the way to the next level and the last gain, and fades out once no gain has come for Skill Progress Seconds.
internal static class SkillProgress
{
    private const string Section = "Status Cards";
    private const string ElementId = "SkillProgress";
    private const int MaxRows = 5;
    private const float Width = 270f;
    private const float RowHeight = 42f;
    private const float Gap = 4f;
    private const float FadeSeconds = 0.8f;
    private const int SortingOrder = 27;
    private const string SkillsReworkedGuid = "M2Valheim.SkillsReworked";

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<int> _seconds;
    private static ConfigEntry<int> _rows;
    private static ConfigEntry<bool> _showLevel;
    private static ConfigEntry<bool> _showPercent;
    private static ConfigEntry<bool> _showGain;

    private static readonly Entry[] Entries = new Entry[MaxRows];
    private static readonly Row[] Rows = new Row[MaxRows];
    private static int _count;
    private static float _last = float.MinValue;
    private static bool _dirty;
    private static bool _sample;
    private static int _sized;

    private static RectTransform _root;
    private static CanvasGroup _group;

    private static bool _resolved;
    private static bool _skillsReworked;
    private static ConfigEntry<bool> _srXpBar;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind(Section, "Skill Progress", true,
            "A card that appears when you gain skill experience, with the skill, its level and how far it is to the next level, and fades out when the gains stop. Stands aside while SeneaL UI shows Skills Reworked's XP bar.");
        _seconds = cfg.Bind(Section, "Skill Progress Seconds", 6,
            new ConfigDescription("Seconds the Skill Progress card stays after the last gain before it fades out.", new AcceptableValueRange<int>(1, 30)));
        _rows = cfg.Bind(Section, "Skill Progress Rows", 2,
            new ConfigDescription("Most skills the Skill Progress card shows at once, the most recent first.", new AcceptableValueRange<int>(1, MaxRows)));
        _showLevel = cfg.Bind(Section, "Skill Progress Shows Level", true, "The Skill Progress card names each skill's level beside its name.");
        _showPercent = cfg.Bind(Section, "Skill Progress Shows Percent", true, "The Skill Progress card says how far each skill is toward its next level, in percent.");
        _showGain = cfg.Bind(Section, "Skill Progress Shows Gain", true, "The Skill Progress card says how much of a level the last gain added, in percent.");
        foreach (var entry in new[] { _showLevel, _showPercent, _showGain })
        {
            entry.SettingChanged += (_, _) =>
            {
                _sample = false;
                _dirty = true;
            };
        }
    }

    public static void Tick(float now)
    {
        if (_root == null)
        {
            // Built as soon as the HUD exists, because SeneaL UI's HUD editor adds an element's frame only when it rebuilds, which it does while closed.
            if (Hud.instance == null)
            {
                return;
            }
            Build();
        }
        if (_sized != _rows.Value)
        {
            _sized = _rows.Value;
            _dirty = true;
            _sample = false;
            _root.sizeDelta = new Vector2(Width, Mathf.Clamp(_sized, 1, MaxRows) * (RowHeight + Gap) - Gap);
        }
        var player = Player.m_localPlayer;
        var editing = SeneaLHud.Editing(ElementMode.Hud);
        var on = _enabled.Value && ServerRules.Allows("SkillProgress") && player != null && !player.IsDead() && Hud.instance != null && !Hud.IsUserHidden() && !SeneaLShowsXpBar();
        // While the HUD editor is open the card shows sample rows whether or not it is on, so it can be found and placed, as every other card of this plugin does.
        if (editing)
        {
            if (!_sample)
            {
                _sample = true;
                ShowSample();
            }
            Show(1f);
            return;
        }
        if (_sample)
        {
            _sample = false;
            _dirty = true;
        }
        var alpha = on && _count > 0 ? Mathf.Clamp01(1f - (now - _last - _seconds.Value) / FadeSeconds) : 0f;
        if (alpha <= 0f)
        {
            _count = 0;
            Show(0f);
            return;
        }
        if (_dirty)
        {
            _dirty = false;
            var shown = Mathf.Min(_count, _rows.Value);
            for (var i = 0; i < MaxRows; i++)
            {
                if (i < shown)
                {
                    Rows[i].Set(Entries[i]);
                }
                else
                {
                    Rows[i].Hide();
                }
            }
        }
        Show(alpha);
    }

    // The card stays active while faded out, so SeneaL UI keeps placing it and an element attached to it (the Tracker, shown in the Esc menu while the card is faded) keeps its place beside it.
    private static void Show(float alpha)
    {
        if (!_root.gameObject.activeSelf)
        {
            _root.gameObject.SetActive(true);
        }
        if (_group.alpha != alpha)
        {
            _group.alpha = alpha;
        }
    }

    private static void ShowSample()
    {
        for (var i = 0; i < MaxRows; i++)
        {
            if (i < _rows.Value)
            {
                Rows[i].Set(new Entry { Name = i == 0 ? "Woodcutting" : "Run", Level = i == 0 ? 23 : 41, Share = i == 0 ? 0.47f : 0.82f, Gain = i == 0 ? 1.2f : 0.3f });
            }
            else
            {
                Rows[i].Hide();
            }
        }
    }

    // SeneaL UI draws Skills Reworked's level and XP bar while that mod is installed and its SrXpBar setting is on (a setting builds before 1.2.0 lack, and then draw no bar), so the card stands aside to keep one XP display on screen.
    private static bool SeneaLShowsXpBar()
    {
        if (!_resolved)
        {
            _resolved = true;
            _skillsReworked = Chainloader.PluginInfos.ContainsKey(SkillsReworkedGuid);
            var plugin = TypeLookup.Find("SeneaLUI.Plugin");
            var property = plugin == null ? null : AccessTools.Property(plugin, "SrXpBar");
            _srXpBar = property == null ? null : property.GetValue(null, null) as ConfigEntry<bool>;
        }
        return _skillsReworked && _srXpBar != null && _srXpBar.Value;
    }

    private static void Gained(Skills.SkillType type, Skills.Skill skill, float levelBefore, float shareBefore)
    {
        var level = (int)skill.m_level;
        var share = skill.GetLevelPercentage();
        var gain = level > (int)levelBefore ? 1f - shareBefore + share : share - shareBefore;
        var at = 0;
        while (at < _count && Entries[at].Type != type)
        {
            at++;
        }
        var name = at < _count ? Entries[at].Name : SkillName(skill);
        if (at == _count && _count < MaxRows)
        {
            _count++;
        }
        for (var i = Mathf.Min(at, MaxRows - 1); i > 0; i--)
        {
            Entries[i] = Entries[i - 1];
        }
        Entries[0] = new Entry { Type = type, Name = name, Level = level, Share = share, Gain = gain * 100f };
        _last = Time.time;
        _dirty = true;
    }

    // The same text key the game's own level-up message names a skill by.
    private static string SkillName(Skills.Skill skill)
    {
        var name = Localization.instance == null ? null : Localization.instance.Localize("$skill_" + skill.m_info.m_skill.ToString().ToLower());
        return string.IsNullOrEmpty(name) || name.StartsWith("[") ? SettingsPage.Pretty(skill.m_info.m_skill.ToString()) : name;
    }

    private static void Build()
    {
        var canvasObject = new GameObject("SeneaLUIUnlocker_SkillProgress", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasObject.GetComponent<CanvasScaler>().referencePixelsPerUnit = SkinScale.Ppu;
        ResolutionScale.Track(canvasObject.GetComponent<CanvasScaler>());
        Object.DontDestroyOnLoad(canvasObject);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        _root = new GameObject("rows", typeof(RectTransform), typeof(CanvasGroup)).GetComponent<RectTransform>();
        _root.SetParent(canvasObject.transform, false);
        // Default spot on the left edge, which SeneaL UI leaves empty between the status effects and the quick slots; the right edge holds the forecast, base status and shelter cards, which five rows would cover. The HUD editor moves it from there.
        _root.anchorMin = _root.anchorMax = new Vector2(0f, 0.5f);
        _root.pivot = new Vector2(0f, 0.5f);
        _root.anchoredPosition = new Vector2(24f, 40f);
        _group = _root.GetComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.interactable = false;
        _group.blocksRaycasts = false;
        for (var i = 0; i < MaxRows; i++)
        {
            Rows[i] = new Row(_root, i);
        }
        _root.gameObject.SetActive(false);
        SeneaLHud.Register(ElementId, "Skill Progress", _root);
    }

    private struct Entry
    {
        public Skills.SkillType Type;
        public string Name;
        public int Level;
        public float Share;
        public float Gain;
    }

    private sealed class Row
    {
        private readonly GameObject _object;
        private readonly TextMeshProUGUI _name;
        private readonly TextMeshProUGUI _right;
        private readonly RectTransform _fill;
        private readonly float _barWidth;

        public Row(RectTransform parent, int index)
        {
            _object = new GameObject("row" + index, typeof(RectTransform));
            var rect = Place((RectTransform)_object.transform, parent, new Vector2(0f, -index * (RowHeight + Gap)), new Vector2(Width, RowHeight));
            SeneaLTheme.Back(rect, "Glass78", (int)RowHeight);
            _name = Text(rect, "name", new Vector2(10f, -4f), new Vector2(Width - 150f, 22f), 14f, TextAlignmentOptions.Left, "SemiBold");
            _right = Text(rect, "right", new Vector2(Width - 150f, -5f), new Vector2(140f, 22f), 13f, TextAlignmentOptions.Right, "Regular");
            _barWidth = Width - 20f;
            var track = Place(new GameObject("bar", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>(), rect, new Vector2(10f, -30f), new Vector2(_barWidth, 6f));
            var (sprite, _) = SeneaLTheme.Panel("Track", 6);
            var trackImage = track.GetComponent<Image>();
            trackImage.sprite = sprite;
            trackImage.type = sprite == null ? Image.Type.Simple : Image.Type.Sliced;
            trackImage.color = sprite == null ? new Color(0f, 0f, 0f, 0.5f) : Color.white;
            trackImage.raycastTarget = false;
            _fill = Place(new GameObject("fill", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>(), track, Vector2.zero, new Vector2(_barWidth, 6f));
            var fillImage = _fill.GetComponent<Image>();
            fillImage.color = SeneaLTheme.Color("Gold", new Color(0.95f, 0.78f, 0.3f));
            fillImage.raycastTarget = false;
        }

        public void Set(Entry entry)
        {
            _name.text = _showLevel.Value ? Lang.F("SkillProgress.NameLevel", "{0}  level {1}", entry.Name, entry.Level) : entry.Name;
            var parts = new System.Collections.Generic.List<string>(2);
            if (_showPercent.Value)
            {
                parts.Add(Lang.F("SkillProgress.PercentToLevel", "{0:0}% to {1}", entry.Share * 100f, entry.Level + 1));
            }
            if (_showGain.Value)
            {
                parts.Add(entry.Gain < 0.1f ? $"+{entry.Gain:0.00}%" : $"+{entry.Gain:0.0}%");
            }
            _right.text = string.Join("  ", parts);
            _fill.sizeDelta = new Vector2(_barWidth * Mathf.Clamp01(entry.Share), _fill.sizeDelta.y);
            if (!_object.activeSelf)
            {
                _object.SetActive(true);
            }
        }

        public void Hide()
        {
            if (_object.activeSelf)
            {
                _object.SetActive(false);
            }
        }

        private static RectTransform Place(RectTransform rect, RectTransform parent, Vector2 at, Vector2 size)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = at;
            rect.sizeDelta = size;
            return rect;
        }

        private static TextMeshProUGUI Text(RectTransform parent, string name, Vector2 at, Vector2 size, float fontSize, TextAlignmentOptions align, string font)
        {
            // Inactive until the font is set, so TextMeshPro never wakes looking for the default font the game does not ship.
            var host = Place(new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(), parent, at, size).gameObject;
            host.SetActive(false);
            var text = host.AddComponent<TextMeshProUGUI>();
            var asset = SeneaLTheme.Font(font);
            if (asset != null)
            {
                text.font = asset;
            }
            host.SetActive(true);
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = SeneaLTheme.Color("Parchment", new Color(0.93f, 0.88f, 0.76f));
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            // Ellipsis hides a line that is taller than its box, so the box is never shorter than one line of the text.
            var box = text.rectTransform;
            box.sizeDelta = new Vector2(box.sizeDelta.x, Mathf.Max(box.sizeDelta.y, Mathf.Ceil(fontSize * 1.45f)));
            text.raycastTarget = false;
            return text;
        }
    }

    // Skills.RaiseSkill is the one path every skill gain takes (Player.RaiseSkill and the dodge gain both reach it); a gain at level 100 changes nothing and shows nothing.
    [HarmonyPatch(typeof(Skills), nameof(Skills.RaiseSkill))]
    internal static class RaisePatch
    {
        private static void Prefix(Skills __instance, Skills.SkillType skillType, out (float Level, float Share, float Accumulator) __state)
        {
            __state = (-1f, 0f, 0f);
            if (skillType == Skills.SkillType.None || _enabled == null || !_enabled.Value || Player.m_localPlayer == null || __instance.m_player != Player.m_localPlayer)
            {
                return;
            }
            var skill = __instance.GetSkill(skillType);
            __state = (skill.m_level, skill.GetLevelPercentage(), skill.m_accumulator);
        }

        private static void Postfix(Skills __instance, Skills.SkillType skillType, (float Level, float Share, float Accumulator) __state)
        {
            if (__state.Level < 0f)
            {
                return;
            }
            var skill = __instance.GetSkill(skillType);
            if (skill.m_level != __state.Level || skill.m_accumulator != __state.Accumulator)
            {
                Gained(skillType, skill, __state.Level, __state.Share);
            }
        }
    }
}
