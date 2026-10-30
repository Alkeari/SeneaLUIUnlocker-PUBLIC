using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// A full trace of what this plugin does, written to its own file so a test run can be read back afterwards: versions and installed plugins, every setting of this plugin and every change to it or to SeneaL UI's own settings, every method of this plugin as it runs (its first calls with their arguments, then call counts), every exception with its stack, SeneaL UI's server policy whenever it changes, and the Harmony patches this plugin holds. The setting is hidden from the settings page; a test build turns it on by default.
internal static class DebugLog
{
    private const string PatchId = Plugin.Guid + ".debug";
    private const int DetailedCalls = 3;
    private const float SummarySeconds = 30f;

    private static StreamWriter _out;
    private static readonly Dictionary<MethodBase, int> Calls = [];
    private static readonly Dictionary<MethodBase, int> CallsSinceSummary = [];
    private static readonly Dictionary<ConfigEntryBase, string> Seen = [];
    private static float _nextSummary;
    private static string _policy;
    private static string _patches;
    private static ConfigFile _seneaLWatched;
    private static ConfigEntry<bool> _trace;
    private static float _nextCheck;
    private static readonly Type PolicyType = TypeLookup.Find("SeneaLUI.Net.Policy");

    public static bool On => _out != null;

    public static void Start(ConfigFile cfg, ManualLogSource log)
    {
#if TEST_BUILD
        const bool byDefault = true;
#else
        const bool byDefault = false;
#endif
        var enabled = cfg.Bind("Debug", "Debug Log", byDefault, new ConfigDescription(
            "Write everything this mod does to BepInEx\\SeneaL UI Unlocker Debug.log, for a test run or a bug report. It makes the game slower to start and the file large.",
            null, new ConfigurationManagerAttributes { Browsable = false }));
        // Counting every call of every method of this plugin halved the frame rate with a full mod set (36 to 19 FPS, 2026-10-13), so it is its own switch, off unless a bug hunt needs it.
        _trace = cfg.Bind("Debug", "Trace Calls", false, new ConfigDescription(
            "With Debug Log on, also count every call of every method of this mod and log the first calls with their arguments. It roughly halves the frame rate; for hunting a bug only.",
            null, new ConfigurationManagerAttributes { Browsable = false }));
        if (!enabled.Value)
        {
            return;
        }
        try
        {
            var path = Path.Combine(Paths.BepInExRootPath, "SeneaL UI Unlocker Debug.log");
            _out = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception e)
        {
            log.LogWarning($"Could not open the debug log ({e.Message}); it stays off.");
            return;
        }
        Write("start", $"SeneaL UI Unlocker {PluginVersion.Value}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, Unity {Application.unityVersion}, Valheim {Version.GetVersionString()}");
        foreach (var info in Chainloader.PluginInfos.Values.OrderBy(info => info.Metadata.GUID))
        {
            Write("plugin", $"{info.Metadata.GUID} {info.Metadata.Name} {info.Metadata.Version} ({Path.GetFileName(info.Location)})");
        }
        BepInEx.Logging.Logger.Listeners.Add(new Listener());
        Application.logMessageReceived += (message, stack, type) =>
        {
            if (type is LogType.Exception or LogType.Error && (stack ?? "").Contains("SeneaLUIUnlocker"))
            {
                Write("unity " + type, message + "\n" + stack);
            }
        };
        cfg.SettingChanged += (_, e) => Changed("setting", e.ChangedSetting);
    }

    // Traces every method declared in this plugin, after its own patches are in place.
    public static void TraceAll()
    {
        if (!On || _trace == null || !_trace.Value)
        {
            return;
        }
        var harmony = new Harmony(PatchId);
        var enter = new HarmonyMethod(AccessTools.Method(typeof(DebugLog), nameof(Enter)));
        var fail = new HarmonyMethod(AccessTools.Method(typeof(DebugLog), nameof(Fail)));
        int traced = 0, skipped = 0;
        var hasEditor = TypeLookup.Find("SeneaLUI.Hud.HudEditor") != null;
        foreach (var type in Traced())
        {
            if (type == typeof(DebugLog) || type.DeclaringType == typeof(DebugLog) || type.IsInterface)
            {
                continue;
            }
            if (!hasEditor && EditorOnly(type))
            {
                skipped++;
                Write("trace", $"not traced {type.FullName}: it is built on SeneaL UI's HUD editor, which this SeneaL UI build lacks");
                continue;
            }
            if (type.ContainsGenericParameters)
            {
                continue;
            }
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception e)
            {
                // A class that names a SeneaL UI type this SeneaL UI build lacks cannot even be listed.
                skipped++;
                Write("trace", $"not traced {type.FullName}: {e.GetBaseException().Message}");
                continue;
            }
            foreach (var method in methods)
            {
                try
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
                    {
                        continue;
                    }
                    // Harmony's argument copy emits invalid code for some by-ref parameters, and a mod that batches patches (StartupAccelerator) applies it outside this try, where the failure breaks the game's GameObject type for the whole session.
                    if (method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                    {
                        skipped++;
                        Write("trace", $"not traced {Name(method)}: it takes a parameter by reference");
                        continue;
                    }
                    // A method naming a SeneaL UI member this build lacks reads back with an empty operand, and copying it for a patch would crash the game.
                    if (PatchProcessor.GetOriginalInstructions(method).Exists(code => code.opcode.OperandType != System.Reflection.Emit.OperandType.InlineNone && code.operand == null))
                    {
                        skipped++;
                        Write("trace", $"not traced {Name(method)}: it names something this SeneaL UI build lacks");
                        continue;
                    }
                    harmony.Patch(method, prefix: enter, finalizer: fail);
                    traced++;
                }
                catch (Exception e)
                {
                    skipped++;
                    Write("trace", $"not traced {Name(method)}: {e.GetBaseException().Message}");
                }
            }
        }
        Write("trace", $"{traced} methods traced, {skipped} not traced");
    }

    private static IEnumerable<Type> Traced()
    {
        var types = TypeLookup.Loadable(typeof(Plugin).Assembly);
        return WindowAdditions.Available ? types.Concat(TypeLookup.Loadable(WindowAdditions.Assembly)) : types;
    }

    // Classes whose fields or signatures name SeneaL UI 1.2.0's HUD editor types. On SeneaL UI 1.1.x they load only partly, and inspecting their methods crashes the runtime itself, beyond any try.
    private static readonly string[] EditorOnlyTypes = ["GrowCorners"];

    private static bool EditorOnly(Type type)
    {
        for (var t = type; t != null; t = t.DeclaringType)
        {
            if (t.Namespace == typeof(Plugin).Namespace && Array.IndexOf(EditorOnlyTypes, t.Name) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    public static void Write(string area, string message)
    {
        if (_out == null)
        {
            return;
        }
        try
        {
            _out.WriteLine($"{DateTime.Now:HH:mm:ss.fff} f{Time.frameCount} [{area}] {message}");
        }
        catch (Exception)
        {
        }
    }

    // Runs every frame from Plugin.Update; SeneaL UI's policy and this plugin's patches are compared once a second, since walking every patched method of every mod each frame cost half the frame rate with a full mod set.
    public static void Tick(PolicyUnlock unlock)
    {
        if (_out == null)
        {
            return;
        }
        var seneaL = SeneaLConfig.File;
        if (seneaL != null && seneaL != _seneaLWatched)
        {
            _seneaLWatched = seneaL;
            Write("seneal", $"SeneaL UI config has {seneaL.Count} settings");
            seneaL.SettingChanged += (_, e) => Changed("seneal setting", e.ChangedSetting);
        }
        if (Time.realtimeSinceStartup < _nextCheck)
        {
            return;
        }
        _nextCheck = Time.realtimeSinceStartup + 1f;
        var policy = Policy(unlock);
        if (policy != _policy)
        {
            _policy = policy;
            Write("policy", policy);
        }
        // Every mod's patched methods are walked only with the half-minute summary: it costs a few milliseconds each time.
        var patches = Time.realtimeSinceStartup >= _nextSummary ? Patches() : _patches;
        if (patches != _patches)
        {
            _patches = patches;
            Write("patches", patches);
        }
        if (Time.realtimeSinceStartup >= _nextSummary)
        {
            _nextSummary = Time.realtimeSinceStartup + SummarySeconds;
            Summary();
        }
    }

    public static void Settings(ConfigFile cfg)
    {
        if (_out == null)
        {
            return;
        }
        foreach (var entry in SeneaLConfig.Entries(cfg).OrderBy(entry => entry.Definition.Section).ThenBy(entry => entry.Definition.Key))
        {
            Seen[entry] = entry.GetSerializedValue();
            Write("setting", $"[{entry.Definition.Section}] {entry.Definition.Key} = {Seen[entry]}");
        }
        foreach (var orphan in RetiredSettings.Orphans(cfg).Keys)
        {
            Write("setting", $"unbound [{orphan.Section}] {orphan.Key}");
        }
    }

    private static void Changed(string area, ConfigEntryBase entry)
    {
        if (entry == null)
        {
            return;
        }
        Seen.TryGetValue(entry, out var before);
        var now = entry.GetSerializedValue();
        Seen[entry] = now;
        Write(area, $"[{entry.Definition.Section}] {entry.Definition.Key}: {before ?? "?"} -> {now}");
    }

    private static string Policy(PolicyUnlock unlock)
    {
        var type = PolicyType;
        if (type == null)
        {
            return "no SeneaL UI policy";
        }
        var text = new StringBuilder();
        text.Append($"state {Value(type, "State")}, server {ServerDirectory.CurrentKey}, unlocked {unlock?.IsUnlocked}, asked {Value(type, "_asked")}, free {Value(type, "_free")}, settled {Value(type, "_settled")}");
        var remote = AccessTools.Field(type, "_remote")?.GetValue(null);
        if (remote == null)
        {
            text.Append(", no rules");
        }
        else
        {
            text.Append(", rules: ");
            text.Append(string.Join(", ", remote.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(field => $"{field.Name} {field.GetValue(remote)}")));
        }
        return text.ToString();
    }

    private static string Value(Type type, string name)
    {
        // Plain reflection: AccessTools logs a warning for every miss, and this runs every frame.
        const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var field = type.GetField(name, any);
        if (field != null)
        {
            return field.GetValue(null)?.ToString() ?? "null";
        }
        var property = type.GetProperty(name, any);
        return property?.GetValue(null, null)?.ToString() ?? "-";
    }

    private static string Patches()
    {
        var mine = new List<string>();
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            var info = Harmony.GetPatchInfo(method);
            var owners = info.Owners.Where(owner => owner.StartsWith(Plugin.Guid, StringComparison.Ordinal) && owner != PatchId).Distinct().ToList();
            if (owners.Count > 0)
            {
                mine.Add($"{Name(method)} ({string.Join(", ", owners)})");
            }
        }
        mine.Sort(StringComparer.Ordinal);
        return $"{mine.Count} methods patched by this plugin: " + string.Join("; ", mine);
    }

    private static readonly Dictionary<string, (long Total, long Max, int Count)> Timings = [];

    // Time spent in each feature's per-frame tick, summed and at its worst, reported with the call counts every half minute.
    public static void Timed(string feature, long ticks)
    {
        Timings.TryGetValue(feature, out var t);
        Timings[feature] = (t.Total + ticks, System.Math.Max(t.Max, ticks), t.Count + 1);
    }

    private static void Summary()
    {
        if (Timings.Count > 0)
        {
            var ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Write("timing", string.Join(", ", Timings.OrderByDescending(pair => pair.Value.Max).Take(15)
                .Select(pair => $"{pair.Key} worst {pair.Value.Max * ms:0.00} ms, avg {pair.Value.Total * ms / pair.Value.Count:0.000} ms")));
            Timings.Clear();
        }
        if (CallsSinceSummary.Count == 0)
        {
            return;
        }
        Write("calls", string.Join(", ", CallsSinceSummary.OrderByDescending(pair => pair.Value).Select(pair => $"{Name(pair.Key)} x{pair.Value}")));
        CallsSinceSummary.Clear();
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType?.FullName?.Replace("SeneaLUIUnlocker.", "")}.{method.Name}";

    private static void Enter(MethodBase __originalMethod, object[] __args)
    {
        Calls.TryGetValue(__originalMethod, out var count);
        Calls[__originalMethod] = ++count;
        CallsSinceSummary.TryGetValue(__originalMethod, out var recent);
        CallsSinceSummary[__originalMethod] = recent + 1;
        if (count <= DetailedCalls)
        {
            Write("call", $"{Name(__originalMethod)}({string.Join(", ", (__args ?? []).Select(Describe))})" + (count == DetailedCalls ? " (further calls counted only)" : ""));
        }
    }

    private static Exception Fail(MethodBase __originalMethod, Exception __exception)
    {
        if (__exception != null)
        {
            Write("exception", $"{Name(__originalMethod)}: {__exception}");
        }
        return __exception;
    }

    private static string Describe(object value)
    {
        try
        {
            var text = value switch
            {
                null => "null",
                UnityEngine.Object unity when unity == null => "destroyed",
                UnityEngine.Object unity => $"{unity.GetType().Name} '{unity.name}'",
                string s => '"' + s + '"',
                _ => value.ToString()
            };
            return text.Length > 120 ? text.Substring(0, 120) + "..." : text;
        }
        catch (Exception)
        {
            return value.GetType().Name;
        }
    }

    private sealed class Listener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e)
        {
            if (e.Source.SourceName == Plugin.Name)
            {
                Write("log " + e.Level, e.Data?.ToString());
            }
        }

        public void Dispose()
        {
        }
    }
}
