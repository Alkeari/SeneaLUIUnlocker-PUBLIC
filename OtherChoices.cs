using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace SeneaLUIUnlocker;

// A choice whose list is not every possible answer ends with an Other value; the settings page swaps its dropdown for a text box when Other is picked and hands the text over exactly as typed.
internal static class OtherChoices
{
    private static readonly Dictionary<ConfigEntryBase, (string Value, Action<string> Typed)> Entries = [];

    public static void Add(ConfigEntryBase entry, string value, Action<string> typed) => Entries[entry] = (value, typed);

    public static bool Has(ConfigEntryBase entry) => Entries.ContainsKey(entry);

    public static bool Is(ConfigEntryBase entry, object value) => Entries.TryGetValue(entry, out var other) && Equals(value, other.Value);

    public static void Typed(ConfigEntryBase entry, string text) => Entries[entry].Typed(text);
}
