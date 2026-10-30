using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;

namespace SeneaLUIUnlocker;

// SeneaL UI's Profiles.Parse keeps only the first 900 settings of a saved interface preset (6000 from 1.2.0-preview.4) and Apply resets every other setting to its default, so a preset holding more (a full one holds over 1000) comes back partly reset.
// After SeneaL UI applies a saved preset, this sets the settings it skipped, read from the same file; with a limit above what the preset holds it does nothing.
internal sealed class PresetFix
{
    private static readonly int SeneaLLimit = ReadLimit();
    private const int SeneaLMaxValue = 300;
    private const int BulkChange = 100;
    private const string Header = "SeneaL UI interface";

    private readonly ConfigEntry<bool> _enabled;
    private readonly ManualLogSource _log;
    private ConfigFile _seneaL;
    private int _changes;

    public PresetFix(ConfigEntry<bool> enabled, ManualLogSource log)
    {
        _enabled = enabled;
        _log = log;
    }

    // The limit is the constant Profiles.Parse compares values.Count against, read from its own code, since builds that report the same version differ.
    private static int ReadLimit()
    {
        // From 1.2.0-preview.7 there are two Parse overloads, and the limit sits in the one that also reads the author.
        var profiles = TypeLookup.Find("SeneaLUI.Core.Profiles");
        var parses = profiles == null ? [] : AccessTools.GetDeclaredMethods(profiles).FindAll(method => method.Name == "Parse");
        if (parses.Count == 0)
        {
            return int.MaxValue;
        }
        foreach (var parse in parses)
        {
            var code = PatchProcessor.GetOriginalInstructions(parse);
            for (var i = 0; i + 1 < code.Count; i++)
            {
                if (code[i].operand is MethodInfo { Name: "get_Count" } && code[i + 1].opcode == OpCodes.Ldc_I4 && code[i + 1].operand is int limit && limit > 0)
                {
                    return limit;
                }
            }
        }
        return 900;
    }

    private static string Folder => Path.Combine(Paths.ConfigPath, "SeneaL-UI", "profiles");

    public void LateTick()
    {
        if (_seneaL == null)
        {
            _seneaL = SeneaLConfig.File;
            if (_seneaL != null)
            {
                _seneaL.SettingChanged += (_, _) => _changes++;
            }
            return;
        }
        var bulk = _changes >= BulkChange;
        _changes = 0;
        if (bulk && _enabled.Value)
        {
            Repair();
        }
    }

    private void Repair()
    {
        var name = SeneaLConfig.ActiveProfile;
        var path = name == null ? null : Path.Combine(Folder, Safe(name) + ".slui");
        if (path == null || !File.Exists(path))
        {
            return;
        }
        var all = Read(path);
        if (all.Count <= SeneaLLimit)
        {
            return;
        }
        var head = all.Take(SeneaLLimit);
        var tail = all.Skip(SeneaLLimit).Where(pair => pair.Value.Length <= SeneaLMaxValue)
            .Select(pair => (Entry: Lookup(pair.Key), pair.Value)).Where(pair => pair.Entry != null).ToList();
        // Only a fresh apply of this preset leaves its first settings, up to the limit, exactly as the file has them; a revert or an edit does not, and is left alone.
        if (!head.Where(pair => pair.Value.Length <= SeneaLMaxValue).All(pair => Lookup(pair.Key) is not { } entry || entry.GetSerializedValue() == pair.Value))
        {
            return;
        }
        var missing = tail.Where(pair => pair.Entry.GetSerializedValue() != pair.Value).ToList();
        if (missing.Count == 0)
        {
            return;
        }
        SeneaLConfig.Write(_seneaL, missing);
        _log.LogInfo($"Restored {missing.Count} settings of the interface preset {name} that SeneaL UI skipped.");
    }

    private ConfigEntryBase Lookup(string id)
    {
        var slash = id.IndexOf('/');
        return slash <= 0 ? null : SeneaLConfig.Entry(_seneaL, id.Substring(0, slash), id.Substring(slash + 1));
    }

    // The same reading rules as Profiles.Parse, without its setting limit; Apply's 300-character value limit is applied by the caller.
    internal static List<KeyValuePair<string, string>> Read(string path)
    {
        var lines = File.ReadAllText(path).Replace("\r", "").Split('\n');
        var values = new List<KeyValuePair<string, string>>();
        if (lines.Length < 2 || !lines[0].StartsWith(Header))
        {
            return values;
        }
        var seen = new HashSet<string>();
        foreach (var line in lines.Skip(1))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = line.Substring(0, eq);
            var value = line.Substring(eq + 1);
            if (key.IndexOf('/') > 0 && seen.Add(key))
            {
                values.Add(new KeyValuePair<string, string>(key, value));
            }
        }
        return values;
    }

    // Profiles.Safe: the file name SeneaL UI saves a preset under.
    internal static string Safe(string name)
    {
        var kept = new string((name ?? "").Trim().Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray()).Trim();
        return kept.Length <= 40 ? kept : kept.Substring(0, 40).Trim();
    }
}
