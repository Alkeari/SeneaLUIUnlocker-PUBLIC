using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Drives SeneaL UI's own policy state through reflection so that SeneaL UI's code is never patched, and so a SeneaL UI build without these members disables the unlock instead of crashing.
internal sealed class PolicyUnlock
{
    private const string PolicyType = "SeneaLUI.Net.Policy";
    private const string PolicyNoticeType = "SeneaLUI.Net.PolicyNotice";
    private const string LocType = "SeneaLUI.Core.Loc";
    private const string NoticeModalType = "SeneaLUI.Hud.NoticeModal";
    private const string SeneaLPluginType = "SeneaLUI.Plugin";

    private readonly ManualLogSource _log;
    private readonly Settings _settings;
    private readonly ServerDirectory _servers;
    private readonly FieldInfo _asked;
    private readonly FieldInfo _askedAt;
    private readonly FieldInfo _remote;
    private readonly FieldInfo _free;
    private readonly FieldInfo _settled;
    private readonly MethodInfo _applied;
    private readonly PropertyInfo _serverVersion;
    private readonly PropertyInfo _state;
    private readonly Type _limits;
    private readonly Type _radarMode;
    private readonly FieldInfo _noticeShown;
    private readonly NoModPopup _popup;

    public event Action<string> Granted;

    public bool IsUnlocked => _unlocked && Asking && !ServerAnswered();

    // Single player, or hosting without SeneaL UI's enforcement (SeneaL UI also reports Own on the main menu, which is left out so nothing applied there is carried into joining a server): the player's own game is the server, so no SeneaL UI server checks this plugin's patches of SeneaL UI and the parts that patch it may run.
    public bool OwnWorld => !Asking && ZNet.instance != null && ZNet.instance.IsServer() && _state != null && _state.GetValue(null, null)?.ToString() == "Own";

    private float _askedAtReal;
    private bool _waiting;
    private bool _unlocked;
    private bool _popupHeld;
    private object _ownRules;
    private bool _popupOwed;
    private bool _noticeOwed;

    private PolicyUnlock(ManualLogSource log, Settings settings, ServerDirectory servers, Type policy, Type notice, NoModPopup popup)
    {
        _log = log;
        _settings = settings;
        _servers = servers;
        _asked = AccessTools.Field(policy, nameof(SeneaLUI.Net.Policy._asked));
        _askedAt = AccessTools.Field(policy, nameof(SeneaLUI.Net.Policy._askedAt));
        _remote = AccessTools.Field(policy, nameof(SeneaLUI.Net.Policy._remote));
        _free = AccessTools.Field(policy, nameof(SeneaLUI.Net.Policy._free));
        _settled = AccessTools.Field(policy, nameof(SeneaLUI.Net.Policy._settled));
        _applied = AccessTools.Method(policy, nameof(SeneaLUI.Net.Policy.Applied), Type.EmptyTypes);
        _serverVersion = AccessTools.Property(policy, nameof(SeneaLUI.Net.Policy.ServerVersion));
        _state = AccessTools.Property(policy, "State");
        _limits = AccessTools.Inner(policy, nameof(SeneaLUI.Net.Policy.Limits));
        _radarMode = AccessTools.Inner(policy, nameof(SeneaLUI.Net.Policy.RadarMode));
        _noticeShown = notice == null ? null : AccessTools.Field(notice, nameof(SeneaLUI.Net.PolicyNotice._shown));
        _popup = popup;
    }

    private bool Complete => _asked != null && _askedAt != null && _remote != null && _free != null && _settled != null
                             && _applied != null && _serverVersion != null && _limits != null && _radarMode != null && _noticeShown != null;

    public static PolicyUnlock Resolve(ManualLogSource log, Settings settings, ServerDirectory servers)
    {
        var policy = TypeLookup.Find(PolicyType);
        var unlock = policy == null ? null : new PolicyUnlock(log, settings, servers, policy, TypeLookup.Find(PolicyNoticeType), NoModPopup.Resolve());
        if (unlock == null || !unlock.Complete)
        {
            log.LogWarning("This SeneaL UI build does not have the server policy this mod drives, so nothing is unlocked.");
            return null;
        }
        if (unlock._popup == null)
        {
            log.LogWarning("This SeneaL UI build does not have the no-SeneaL-UI popup, so Hide No SeneaL UI Popup = false shows nothing.");
        }
        settings.Unlock.SettingChanged += (_, _) => unlock.Reconsider();
        ServerRules.Changed += () =>
        {
            unlock.Reconsider();
            unlock.Rules();
        };
        settings.Filter.SettingChanged += (_, _) => unlock.Reconsider();
        settings.ListedServers.SettingChanged += (_, _) => unlock.Reconsider();
        var rules = settings.RuleEntries;
        settings.File.SettingChanged += (_, e) =>
        {
            if (Array.IndexOf(rules, e.ChangedSetting) >= 0)
            {
                unlock.Rules();
            }
        };
        return unlock;
    }

    // Runs after SeneaL UI asked the server for its rules. Moving SeneaL UI's own deadline out of reach keeps it in its waiting state instead of the vanilla limits, so the switch below never passes through them: shrinking the inventory there would move or drop items.
    public void Asked()
    {
        _unlocked = false;
        _ownRules = null;
        _popupOwed = false;
        _noticeOwed = false;
        _popupHeld = false;
        if (!Unanswered())
        {
            _waiting = false;
            return;
        }
        _askedAtReal = Time.realtimeSinceStartup;
        _waiting = true;
        _askedAt.SetValue(null, float.PositiveInfinity);
        // Held from the start so SeneaL UI's popup cannot appear in the frame the wait ends; released again if a SeneaL UI server answers, so its rules popup still shows.
        if (ServerDirectory.Option(ServerDirectory.PopupOption, _settings.HidePopup.Value))
        {
            _noticeShown.SetValue(null, true);
            _popupHeld = true;
        }
    }

    public void Tick()
    {
        if (_popupHeld && ServerAnswered())
        {
            _popupHeld = false;
            _noticeShown.SetValue(null, false);
        }
        if (_waiting)
        {
            Settle();
        }
        if (_popupOwed)
        {
            ShowPopup();
        }
        if (_noticeOwed)
        {
            ShowNotice();
        }
    }

    private bool Asking => (bool)_asked.GetValue(null);

    private bool Unanswered() => Asking && _remote.GetValue(null) == null && !(bool)_free.GetValue(null);

    private bool ServerAnswered()
    {
        var remote = _remote.GetValue(null);
        return (remote != null && remote != _ownRules) || ((bool)_free.GetValue(null) && !_unlocked);
    }

    private bool Wanted => _settings.Unlock.Value && ServerRules.Allows("Unlock") && _servers.Allows(ServerDirectory.CurrentKey);

    // A player's own choice for an unlock, turned off where a server running this mod denies it.
    private static bool On(BepInEx.Configuration.ConfigEntry<bool> setting, string key) => ServerRules.On(setting, key);

    private float RecycleRate() => On(_settings.Recycle, "Recycle") ? _settings.MaxRecycleRate.Value / 100f : 0f;

    private int RadarMode() => ServerRules.Allows("Radar") ? (int)_settings.Radar.Value : 0;

    private void Settle()
    {
        if (!Unanswered())
        {
            _waiting = false;
            return;
        }
        if (Time.realtimeSinceStartup - _askedAtReal <= _settings.WaitSeconds.Value)
        {
            return;
        }
        _waiting = false;
        if (!Wanted)
        {
            GiveBack();
            return;
        }
        Grant();
        Granted?.Invoke(ServerDirectory.CurrentKey);
        _log.LogInfo("The server does not run SeneaL UI, so SeneaL UI's features are unlocked.");
        _noticeOwed = _settings.UnlockNotice.Value;
        _popupOwed = !ServerDirectory.Option(ServerDirectory.PopupOption, _settings.HidePopup.Value) && _popup != null;
    }

    // Hands the decision back to SeneaL UI, which then shows its own vanilla limits and, unless held, its popup.
    private void GiveBack()
    {
        _askedAt.SetValue(null, _askedAtReal);
        _settled.SetValue(null, true);
        _applied.Invoke(null, null);
    }

    private void Reconsider()
    {
        if (!Asking || _waiting || ServerAnswered())
        {
            return;
        }
        if (_unlocked && !Wanted)
        {
            _unlocked = false;
            _ownRules = null;
            _remote.SetValue(null, null);
            _free.SetValue(null, false);
            GiveBack();
        }
        else if (!_unlocked && Wanted && (bool)_settled.GetValue(null))
        {
            Grant();
            Granted?.Invoke(ServerDirectory.CurrentKey);
        }
    }

    // With every feature at SeneaL UI's maximum the plugin uses the state of a SeneaL UI server that enforces nothing; any narrower choice is handed to SeneaL UI as server rules of this plugin's own.
    private void Grant()
    {
        _unlocked = true;
        if (_settings.AllUnlocked && ServerRules.Denied().Count == 0)
        {
            _ownRules = null;
            _remote.SetValue(null, null);
            _free.SetValue(null, true);
        }
        else
        {
            _ownRules = BuildRules();
            _free.SetValue(null, false);
            _remote.SetValue(null, _ownRules);
            // SeneaL UI would show its server rules popup for these; released like the hold in Asked if a SeneaL UI server answers late.
            _noticeShown.SetValue(null, true);
            _popupHeld = true;
        }
        _settled.SetValue(null, true);
        _applied.Invoke(null, null);
    }

    private void Rules()
    {
        if (_unlocked && Asking && !ServerAnswered())
        {
            Grant();
        }
    }

    private static readonly int PolicyHash = "SeneaLUI_Policy".GetStableHashCode();

    // A SeneaL UI server's answer lists its rules as key and value pairs, and SeneaL UI leaves every key the answer lacks off (an older server knows fewer). Before SeneaL UI reads the answer, each missing key is added with the player's own choice; every pair the server sent passes unchanged, and an answer with no rules (the server enforces nothing) or a locked one is left alone. Only the game's packet handler is patched, never SeneaL UI's code.
    public ZPackage WithUnstated(ZPackage package)
    {
        var pos = package.GetPos();
        try
        {
            if (!_settings.FillUnstatedRules.Value || !Wanted || package.Size() - pos < sizeof(int) || package.ReadInt() != PolicyHash)
            {
                return package;
            }
            var debug = ZRpc.m_DEBUG ? package.ReadString() : null;
            var answer = package.ReadPackage();
            if (answer.Size() == 0)
            {
                return package;
            }
            var format = answer.ReadInt();
            if (format < 1)
            {
                return package;
            }
            var version = answer.ReadString();
            var pairs = new System.Collections.Generic.List<(string Key, string Value)>();
            for (var count = answer.ReadInt(); count > 0; count--)
            {
                pairs.Add((answer.ReadString(), answer.ReadString()));
            }
            if (pairs.Exists(pair => pair.Key == "locked" && pair.Value == "1"))
            {
                return package;
            }
            var stated = new System.Collections.Generic.HashSet<string>(pairs.ConvertAll(pair => pair.Key), StringComparer.Ordinal);
            var own = OwnValues();
            var added = new System.Collections.Generic.List<string>();
            foreach (var pair in own)
            {
                if (!stated.Contains(pair.Key))
                {
                    pairs.Add((pair.Key, pair.Value));
                    added.Add(pair.Key);
                }
            }
            if (added.Count == 0)
            {
                return package;
            }
            var rules = new ZPackage();
            rules.Write(format);
            rules.Write(version);
            rules.Write(pairs.Count);
            foreach (var (key, value) in pairs)
            {
                rules.Write(key);
                rules.Write(value);
            }
            var rebuilt = new ZPackage();
            rebuilt.Write(PolicyHash);
            if (debug != null)
            {
                rebuilt.Write(debug);
            }
            rebuilt.Write(rules);
            rebuilt.SetPos(0);
            _log.LogInfo($"The SeneaL UI server does not state {string.Join(", ", added)}; your own settings apply to them, and every rule it states is kept.");
            return rebuilt;
        }
        catch (Exception e)
        {
            _log.LogWarning($"Could not read which rules the SeneaL UI server states ({e.GetBaseException().Message}); its rules apply as SeneaL UI reads them.");
            return package;
        }
        finally
        {
            package.SetPos(pos);
        }
    }

    // The player's own choices for every rule SeneaL UI's server answer can state, in its wire form (Policy.Build and Policy.Read).
    private System.Collections.Generic.Dictionary<string, string> OwnValues()
    {
        var s = _settings;
        string Flag(bool on) => on ? "1" : "0";
        string Num(float value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slots"] = (On(s.InventorySize, "InventorySize") ? s.MaxSlots.Value : 32).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fromchests"] = Flag(On(s.CraftFromChests, "CraftFromChests")),
            ["chestrange"] = Num(On(s.CraftFromChests, "CraftFromChests") ? s.CraftRange.Value : 1f),
            ["buildfromchests"] = Flag(On(s.BuildFromChests, "BuildFromChests")),
            ["buildrange"] = Num(On(s.BuildFromChests, "BuildFromChests") ? s.BuildRange.Value : 1f),
            ["quickstack"] = Flag(On(s.QuickStack, "QuickStack")),
            ["stackrange"] = Num(On(s.QuickStack, "QuickStack") ? s.StackRange.Value : 1f),
            ["showall"] = Flag(On(s.ShowAllRecipes, "ShowAllRecipes")),
            ["areapickup"] = Flag(On(s.AreaPickup, "AreaPickup")),
            ["arearange"] = Num(On(s.AreaPickup, "AreaPickup") ? s.AreaRadius.Value : 1f),
            ["quickslots"] = (On(s.QuickSlots, "QuickSlots") ? s.MaxQuickSlots.Value : 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["actionslots"] = (On(s.ActionSlots, "ActionSlots") ? s.MaxActionSlots.Value : 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["keyslots"] = Flag(On(s.KeySlots, "KeySlots")),
            ["wallet"] = Flag(On(s.Wallet, "Wallet")),
            ["coinweight"] = Flag(On(s.CoinWeight, "CoinWeight")),
            ["radar"] = (RadarMode()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["radarrange"] = Num(s.RadarRange.Value),
            ["radarmonsters"] = Flag(On(s.RadarMonsters, "RadarMonsters")),
            ["oredetect"] = Flag(On(s.OreDetection, "OreDetection")),
            ["orerange"] = Num(On(s.OreDetection, "OreDetection") ? s.OreRadius.Value : 10f),
            ["smelterauto"] = Flag(On(s.SmelterAutoFill, "SmelterAutoFill")),
            ["livemarks"] = Flag(On(s.LiveMarks, "LiveMarks")),
            ["coordinates"] = Flag(On(s.Coordinates, "Coordinates")),
            ["healthnumbers"] = Flag(On(s.HealthNumbers, "HealthNumbers")),
            ["chestpreview"] = Flag(On(s.ChestPreview, "ChestPreview")),
            ["hovertimers"] = Flag(On(s.HoverTimers, "HoverTimers")),
            ["compass"] = Flag(On(s.Compass, "Compass")),
            ["clock"] = Flag(On(s.Clock, "Clock")),
            ["recycle"] = Flag(On(s.Recycle, "Recycle")),
            ["recyclerate"] = Num(RecycleRate()),
            ["recycleown"] = Flag(!On(s.RecycleAnyStation, "RecycleAnyStation")),
            ["recycleunknown"] = Flag(On(s.RecycleUnknown, "RecycleUnknown"))
        };
    }

    [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.HandlePackage))]
    internal static class AnswerPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ref ZPackage package)
        {
            if (Plugin.Unlock != null && ZNet.instance != null && !ZNet.instance.IsServer())
            {
                package = Plugin.Unlock.WithUnstated(package);
            }
        }
    }

    private object BuildRules()
    {
        var s = _settings;
        var rules = Activator.CreateInstance(_limits);
        Set(rules, "Version", (string)_serverVersion.GetValue(null, null));
        Set(rules, "Slots", On(s.InventorySize, "InventorySize") ? s.MaxSlots.Value : 32);
        Set(rules, "FromChests", On(s.CraftFromChests, "CraftFromChests"));
        Set(rules, "ChestRange", On(s.CraftFromChests, "CraftFromChests") ? s.CraftRange.Value : 1f);
        Set(rules, "BuildFromChests", On(s.BuildFromChests, "BuildFromChests"));
        Set(rules, "BuildRange", On(s.BuildFromChests, "BuildFromChests") ? s.BuildRange.Value : 1f);
        Set(rules, "QuickStack", On(s.QuickStack, "QuickStack"));
        Set(rules, "StackRange", On(s.QuickStack, "QuickStack") ? s.StackRange.Value : 1f);
        Set(rules, "ShowAll", On(s.ShowAllRecipes, "ShowAllRecipes"));
        Set(rules, "AreaPickup", On(s.AreaPickup, "AreaPickup"));
        Set(rules, "AreaRange", On(s.AreaPickup, "AreaPickup") ? s.AreaRadius.Value : 1f);
        Set(rules, "QuickSlots", On(s.QuickSlots, "QuickSlots") ? s.MaxQuickSlots.Value : 3);
        Set(rules, "ActionSlots", On(s.ActionSlots, "ActionSlots") ? s.MaxActionSlots.Value : 0);
        Set(rules, "KeySlots", On(s.KeySlots, "KeySlots"));
        Set(rules, "Wallet", On(s.Wallet, "Wallet"));
        Set(rules, "CoinWeight", On(s.CoinWeight, "CoinWeight"));
        Set(rules, "Radar", Enum.ToObject(_radarMode, RadarMode()));
        Set(rules, "RadarRange", s.RadarRange.Value);
        Set(rules, "RadarMonsters", On(s.RadarMonsters, "RadarMonsters"));
        Set(rules, "OreDetect", On(s.OreDetection, "OreDetection"));
        Set(rules, "OreRange", On(s.OreDetection, "OreDetection") ? s.OreRadius.Value : 10f);
        Set(rules, "SmelterAuto", On(s.SmelterAutoFill, "SmelterAutoFill"));
        Set(rules, "LiveMarks", On(s.LiveMarks, "LiveMarks"));
        Set(rules, "Coordinates", On(s.Coordinates, "Coordinates"));
        Set(rules, "HealthNumbers", On(s.HealthNumbers, "HealthNumbers"));
        Set(rules, "ChestPreview", On(s.ChestPreview, "ChestPreview"));
        Set(rules, "HoverTimers", On(s.HoverTimers, "HoverTimers"));
        Set(rules, "Compass", On(s.Compass, "Compass"));
        Set(rules, "Clock", On(s.Clock, "Clock"));
        Set(rules, "Recycle", On(s.Recycle, "Recycle"));
        Set(rules, "RecycleRate", RecycleRate());
        Set(rules, "RecycleOwnStation", !On(s.RecycleAnyStation, "RecycleAnyStation"));
        Set(rules, "RecycleUnknown", On(s.RecycleUnknown, "RecycleUnknown"));
        return rules;
    }

    private static readonly System.Collections.Generic.HashSet<string> Unknown = new(StringComparer.Ordinal);

    private void Set(object rules, string field, object value)
    {
        var info = AccessTools.Field(_limits, field);
        if (info == null)
        {
            if (Unknown.Add(field))
            {
                _log.LogInfo($"This SeneaL UI build has no server rule named {field}; it keeps SeneaL UI's own default.");
            }
            return;
        }
        info.SetValue(rules, value);
    }

    private void ShowNotice()
    {
        if (!Asking)
        {
            _noticeOwed = false;
            return;
        }
        var player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }
        _noticeOwed = false;
        player.Message(MessageHud.MessageType.TopLeft, Lang.T("PolicyUnlock.SenealUiFeaturesUnlockedOn", "SeneaL UI features unlocked on this server"));
    }

    private void ShowPopup()
    {
        if (!Asking)
        {
            _popupOwed = false;
            return;
        }
        if (!_popup.CanShow())
        {
            return;
        }
        _popupOwed = false;
        try
        {
            _popup.Show();
        }
        catch (Exception e)
        {
            _log.LogWarning($"Could not show SeneaL UI's no-SeneaL-UI popup ({e.GetBaseException().Message}).");
        }
    }

    private sealed class NoModPopup
    {
        private readonly PropertyInfo _title;
        private readonly PropertyInfo _fair;
        private readonly PropertyInfo _body;
        private readonly MethodInfo _show;
        private readonly FieldInfo _active;

        private NoModPopup(Type loc, Type modal, Type plugin)
        {
            _title = AccessTools.Property(loc, nameof(SeneaLUI.Core.Loc.SrvNoModTitle));
            _fair = AccessTools.Property(loc, nameof(SeneaLUI.Core.Loc.SrvFair));
            _body = AccessTools.Property(loc, nameof(SeneaLUI.Core.Loc.SrvNoModBody));
            _show = AccessTools.Method(modal, nameof(SeneaLUI.Hud.NoticeModal.Show), [typeof(string), typeof(string)]);
            _active = AccessTools.Field(plugin, nameof(SeneaLUI.Plugin.Active));
        }

        private bool Complete => _title != null && _fair != null && _body != null && _show != null && _active != null;

        public static NoModPopup Resolve()
        {
            var loc = TypeLookup.Find(LocType);
            var modal = TypeLookup.Find(NoticeModalType);
            var plugin = TypeLookup.Find(SeneaLPluginType);
            if (loc == null || modal == null || plugin == null)
            {
                return null;
            }
            var popup = new NoModPopup(loc, modal, plugin);
            return popup.Complete ? popup : null;
        }

        // The same conditions and text SeneaL UI's PolicyNotice uses for this popup.
        public bool CanShow() => Player.m_localPlayer != null && (bool)_active.GetValue(null);

        public void Show()
        {
            var title = (string)_title.GetValue(null);
            var body = (string)_fair.GetValue(null) + "\n\n" + (string)_body.GetValue(null);
            _show.Invoke(null, [title, body]);
        }
    }
}
