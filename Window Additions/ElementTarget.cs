using System.Collections.Generic;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using UnityEngine;

namespace SeneaLUIUnlocker;

// One of this plugin's elements in SeneaL UI's HUD editor. SeneaL UI gives an element added through SeneaL API (ExtTarget) a settings panel with only its size, opacity, rotation, behavior and attachment; this one is the same element with every option SeneaL UI's own elements have: placement (how it grows), caption, text size, background, color and transparency, in SeneaL UI's own tabs and controls, plus the element's own content options where it has some.
// It takes the ExtTarget's place in the editor's list of targets (EditorAdditions), so moving, sizing, locking and saving work as for every other element. It is edited in the editor mode where it appears in play (SeneaLHud.ModeOf) and is editable whenever it is on and shown, whether or not its feature has anything to show right now: the element itself shows sample content while the editor is open in its mode.
internal class ElementTarget : EditTarget
{
    protected readonly ExtRig Rig;
    public readonly string Id;
    public readonly ElementMode Mode;
    private readonly List<System.Action<List<EditOption>>> _content = [];

    public ElementTarget(ExtRig rig) : base(rig.E)
    {
        Rig = rig;
        var key = rig.E.Key ?? "";
        Id = key.Substring(key.LastIndexOf('.') + 1);
        Mode = SeneaLHud.ModeOf(Id);
        if (ElementContent.Options.TryGetValue(Id, out var options))
        {
            _content.Add(options);
        }
    }

    // SeneaL UI keys an element added through SeneaL API as x.<owner>.<id> (ExtElements.Key).
    public static bool Ours(EditTarget target) =>
        target is ExtTarget && target.E?.Key != null && target.E.Key.IndexOf(Plugin.Guid, System.StringComparison.OrdinalIgnoreCase) >= 0;

    public override bool HasPlace => true;

    public override bool HasCaption => true;

    public override bool HasColors => true;

    public override bool HasRotation => true;

    public override bool IsTab => Mode == ElementMode.Tab;

    public override string Name => Rig.Name;

    public override bool Present => Rig.Alive && E.On.Value;

    public override bool Editable => Present && Shown && Rig.Shown && SeneaLHud.EditorMode == Mode;

    public override string IdleLabel => Loc.HudIdleNothing;

    public override bool Shown
    {
        get => E.On.Value && !E.Hidden;
        set
        {
            if (value)
            {
                E.On.Value = true;
            }
            E.Hide.Value = !value;
        }
    }

    public override int Anim
    {
        get => (int)E.Anim.Value;
        set => E.Anim.Value = (Prefs.Fx)value;
    }

    public override Rect Frame() => Rig.Frame();

    public override Vector2 Center() => Rig.Center();

    public override string[] AnimNames() => [Loc.HudAnimFade, Loc.HudAnimSlide, Loc.HudAnimScale, Loc.HudAnimDrift];

    public override void Specific(List<EditOption> list)
    {
        list.Add(Sec(EditOption.TabLook, () => Loc.HudSecSize));
        list.Add(Sl(EditOption.TabLook, () => Loc.HudEditSize + (E.OwnSize ? " · " + Loc.HudOwnNote : ""), 0.5f, 2f, 0.05f, () => E.Size.Value, v => E.Size.Value = v, v => Mathf.RoundToInt(v * 100f) + "%"));
        foreach (var options in _content)
        {
            options(list);
        }
    }

    public override void ResetExtra()
    {
        E.On.Value = true;
        E.Hide.Value = false;
        if (ElementContent.Resets.TryGetValue(Id, out var reset))
        {
            reset();
        }
    }

    // A toggle in the Content tab bound to one of this plugin's settings.
    public static EditToggle Toggle(string label, BepInEx.Configuration.ConfigEntry<bool> entry) =>
        Tg(() => label, () => entry != null && entry.Value, on =>
        {
            if (entry != null)
            {
                entry.Value = on;
            }
        });

    public static EditOption Section(string label) => Sec(EditOption.TabContent, () => label);

    public static EditOption Toggles(params EditToggle[] items) => Tgs(EditOption.TabContent, items);

    public static EditOption Slider(string label, BepInEx.Configuration.ConfigEntry<int> entry, int min, int max) =>
        Sl(EditOption.TabContent, () => label, min, max, 1f, () => entry.Value, v => entry.Value = Mathf.RoundToInt(v), v => Mathf.RoundToInt(v).ToString());

    public static EditOption Slider(string label, BepInEx.Configuration.ConfigEntry<float> entry, float min, float max) =>
        Sl(EditOption.TabContent, () => label, min, max, 1f, () => entry.Value, v => entry.Value = Mathf.Round(v), v => Mathf.RoundToInt(v).ToString());
}

// The Content tab of each of this plugin's elements in the HUD editor: the settings that decide what the element shows and how much detail, the same settings the mod settings page shows, bound by section and key.
internal static class ElementContent
{
    private static BepInEx.Configuration.ConfigFile _cfg;
    private static Dictionary<string, System.Action<List<EditOption>>> _options;
    private static Dictionary<string, System.Action> _resets;

    private static readonly Dictionary<string, (string Heading, (string Label, string Section, string Key, float Min, float Max)[] Rows)> Specs = new()
    {
        ["DeathReport"] = ("Death Report", [
            ("Last death", "Death", "Death Report", 0f, 0f),
            ("If you died now", "Death", "Death Preview", 0f, 0f),
            ("Levels before and after", "Death", "Death Shows Levels", 0f, 0f),
            ("Most skills listed", "Death", "Death Skills Listed", 0f, 15f),
            ("Seconds shown after you come back", "Death", "Death Report Seconds", 0f, 120f)]),
        ["Progress"] = ("Progress", [
            ("Bosses", "Progress", "Progress Shows Bosses", 0f, 0f),
            ("Trophies", "Progress", "Progress Shows Trophies", 0f, 0f),
            ("Traders", "Progress", "Progress Shows Traders", 0f, 0f),
            ("Places found", "Progress", "Progress Shows Places", 0f, 0f)]),
        ["SkillProgress"] = ("Skill Progress", [
            ("Level", "Status Cards", "Skill Progress Shows Level", 0f, 0f),
            ("Progress to next level", "Status Cards", "Skill Progress Shows Percent", 0f, 0f),
            ("Last gain", "Status Cards", "Skill Progress Shows Gain", 0f, 0f),
            ("Most skills shown", "Status Cards", "Skill Progress Rows", 1f, 5f),
            ("Seconds shown after a gain", "Status Cards", "Skill Progress Seconds", 1f, 30f)]),
        ["ShelterComfort"] = ("Sheltered and Comfort", [
            ("Cover details", "Status Cards", "Shelter Shows Cover", 0f, 0f),
            ("Comfort pieces counted", "Status Cards", "Shelter Shows Comfort Pieces", 0f, 0f),
            ("Comfort tips", "Status Cards", "Comfort Tips", 0f, 6f)]),
        ["Forecast"] = ("Weather Forecast", [
            ("Weather changes shown", "Cheats: Information", "Forecast Changes", 1f, 6f)]),
        ["Tracker"] = ("Tracker", [
            ("Count nearby chests", "Tracker", "Count Nearby Chests", 0f, 0f),
            ("Hide gathered materials", "Tracker", "Hide Gathered Materials", 0f, 0f),
            ("Material totals", "Tracker", "Material Totals", 0f, 0f)]),
        ["FoodPlanner"] = ("Food Planner", [
            ("Count food in nearby chests", "Food", "Count Food In Nearby Chests", 0f, 0f)]),
        ["Chat"] = ("Chat", [
            ("Timestamps", "Chat", "Timestamps", 0f, 0f),
            ("Always shown", "Chat", "Keep Chat Shown", 0f, 0f),
            ("Seconds shown after a message", "Chat", "Chat Hide Seconds", 1f, 120f)]),
        ["PartyHud"] = ("Party HUD", [
            ("Shown with a party", "Party HUD", "Show Party HUD", 0f, 0f),
            ("Avatar", "Party HUD", "Show Avatar", 0f, 0f),
            ("Face picture", "Party HUD", "Face Pictures", 0f, 0f),
            ("Name", "Party HUD", "Show Name", 0f, 0f),
            ("Health", "Party HUD", "Show Health", 0f, 0f),
            ("Stamina", "Party HUD", "Show Stamina", 0f, 0f),
            ("Distance", "Party HUD", "Show Distance", 0f, 0f),
            ("Level", "Party HUD", "Show Level", 0f, 0f),
            ("Direction", "Party HUD", "Show Direction", 0f, 0f),
            ("PvP status", "Party HUD", "Show PvP Status", 0f, 0f),
            ("Most members shown", "Party HUD", "Max Rows", 1f, 10f)])
    };

    public static void Bind(BepInEx.Configuration.ConfigFile cfg) => _cfg = cfg;

    public static Dictionary<string, System.Action<List<EditOption>>> Options
    {
        get
        {
            Build();
            return _options;
        }
    }

    public static Dictionary<string, System.Action> Resets
    {
        get
        {
            Build();
            return _resets;
        }
    }

    private static void Build()
    {
        if (_options != null || _cfg == null)
        {
            return;
        }
        _options = [];
        _resets = [];
        foreach (var spec in Specs)
        {
            var (heading, rows) = spec.Value;
            var entries = new List<(string Label, BepInEx.Configuration.ConfigEntryBase Entry, float Min, float Max)>();
            foreach (var (label, section, key, min, max) in rows)
            {
                var entry = SeneaLConfig.Entry(_cfg, section, key);
                if (entry != null)
                {
                    entries.Add((label, entry, min, max));
                }
            }
            if (entries.Count == 0)
            {
                continue;
            }
            _options[spec.Key] = list =>
            {
                list.Add(ElementTarget.Section(heading));
                var toggles = new List<EditToggle>();
                foreach (var (label, entry, min, max) in entries)
                {
                    if (entry is BepInEx.Configuration.ConfigEntry<bool> flag)
                    {
                        toggles.Add(ElementTarget.Toggle(label, flag));
                        if (toggles.Count == 2)
                        {
                            list.Add(ElementTarget.Toggles(toggles.ToArray()));
                            toggles.Clear();
                        }
                    }
                }
                if (toggles.Count > 0)
                {
                    list.Add(ElementTarget.Toggles(toggles.ToArray()));
                }
                foreach (var (label, entry, min, max) in entries)
                {
                    if (entry is BepInEx.Configuration.ConfigEntry<int> whole)
                    {
                        list.Add(ElementTarget.Slider(label, whole, (int)min, (int)max));
                    }
                    else if (entry is BepInEx.Configuration.ConfigEntry<float> number)
                    {
                        list.Add(ElementTarget.Slider(label, number, min, max));
                    }
                }
            };
            _resets[spec.Key] = () =>
            {
                foreach (var (_, entry, _, _) in entries)
                {
                    entry.BoxedValue = entry.DefaultValue;
                }
            };
        }
    }
}
