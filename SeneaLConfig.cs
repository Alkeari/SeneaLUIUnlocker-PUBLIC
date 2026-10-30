using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// SeneaL UI's own ConfigFile (Prefs.Cfg), reached by name so a SeneaL UI build without it leaves the features that need it off.
internal static class SeneaLConfig
{
    private const string PrefsType = "SeneaLUI.Core.Prefs";

    private static readonly System.Type Prefs = TypeLookup.Find(PrefsType);

    // Looked up once: a SeneaL UI build without one of these fields logs a Harmony warning on every lookup, which here ran every frame.
    private static readonly System.Reflection.FieldInfo Cfg = Prefs == null ? null : AccessTools.Field(Prefs, nameof(SeneaLUI.Core.Prefs.Cfg));
    private static readonly System.Reflection.FieldInfo Active = Prefs == null ? null : AccessTools.Field(Prefs, nameof(SeneaLUI.Core.Prefs.ActiveProfile));
    private static readonly System.Reflection.FieldInfo Version = Prefs == null ? null : AccessTools.Field(Prefs, nameof(SeneaLUI.Core.Prefs.Version));

    public static ConfigFile File => Cfg?.GetValue(null) as ConfigFile;

    public static string ActiveProfile => (Active?.GetValue(null) as ConfigEntry<string>)?.Value;

    // SeneaL UI redraws what depends on its settings when this counter moves, as its own preset code does.
    public static void Bump()
    {
        if (Version != null)
        {
            Version.SetValue(null, (int)Version.GetValue(null) + 1);
        }
    }

    public static ConfigEntryBase Entry(ConfigFile cfg, string section, string key) =>
        ((IDictionary<ConfigDefinition, ConfigEntryBase>)cfg).TryGetValue(new ConfigDefinition(section, key), out var entry) ? entry : null;

    public static ConfigEntryBase[] Entries(ConfigFile cfg) => ((IDictionary<ConfigDefinition, ConfigEntryBase>)cfg).Values.ToArray();

    // Writes several values with one save, as SeneaL UI's own preset code does.
    public static void Write(ConfigFile cfg, IEnumerable<(ConfigEntryBase Entry, string Value)> values)
    {
        var save = cfg.SaveOnConfigSet;
        cfg.SaveOnConfigSet = false;
        try
        {
            foreach (var (entry, value) in values)
            {
                entry.SetSerializedValue(value);
            }
        }
        finally
        {
            cfg.SaveOnConfigSet = save;
            cfg.Save();
        }
        Bump();
    }
}
