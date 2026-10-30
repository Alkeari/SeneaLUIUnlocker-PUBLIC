using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;

namespace SeneaLUIUnlocker;

/// <summary>
/// Player-facing text. Every string is asked for by a stable key with its English text beside it, so the
/// English base always exists; a file named after Valheim's language (Languages\German.json) overrides it.
/// A translator edits or adds a file in the mod's Languages folder or in BepInEx\config\SeneaLUIUnlocker\Languages,
/// where the second folder wins, and never has to touch the mod.
/// </summary>
internal static class Lang
{
    private static readonly Regex Pair = new(@"""((?:[^""\\]|\\.)*)""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static Dictionary<string, string> _texts;
    private static string _loadedFor = "";

    public static ManualLogSource Log;

    public static string T(string key, string english)
    {
        var texts = Texts();
        return texts.TryGetValue(key, out var text) && text.Length > 0 ? text : english;
    }

    public static string F(string key, string english, params object[] args)
    {
        var template = T(key, english);
        try { return string.Format(template, args); }
        catch (FormatException) { return string.Format(english, args); }
    }

    public static string N(string key, int n, string one, string other, params object[] more)
    {
        var args = new object[more.Length + 1];
        args[0] = n;
        more.CopyTo(args, 1);
        return n == 1 ? F(key + ".One", one, args) : F(key + ".Other", other, args);
    }

    private static Dictionary<string, string> Texts()
    {
        var language = PlatformLanguage();
        if (_texts != null && language == _loadedFor) return _texts;
        _loadedFor = language;
        _texts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (language == "English") return _texts;
        foreach (var folder in Folders()) Read(Path.Combine(folder, language + ".json"), _texts);
        return _texts;
    }

    private static IEnumerable<string> Folders()
    {
        yield return Path.Combine(Path.GetDirectoryName(typeof(Lang).Assembly.Location) ?? "", "Languages");
        yield return Path.Combine(Paths.ConfigPath, Plugin.Name.Replace(" ", ""), "Languages");
    }

    private static void Read(string file, Dictionary<string, string> into)
    {
        try
        {
            if (!File.Exists(file)) return;
            foreach (Match match in Pair.Matches(File.ReadAllText(file)))
                if (match.Groups[1].Value != "_note") into[Unescape(match.Groups[1].Value)] = Unescape(match.Groups[2].Value);
        }
        catch (Exception exception)
        {
            Log?.LogWarning("Could not read the language file " + file + ": " + exception.Message);
        }
    }

    private static string Unescape(string text) => Regex.Replace(text, @"\\(u[0-9a-fA-F]{4}|.)", match =>
    {
        var code = match.Groups[1].Value;
        return code[0] switch { 'n' => "\n", 't' => "\t", 'u' when code.Length == 5 => ((char)Convert.ToInt32(code.Substring(1), 16)).ToString(), _ => code };
    });

    private static string PlatformLanguage()
    {
        try { return Localization.instance?.GetSelectedLanguage() ?? "English"; }
        catch (Exception) { return "English"; }
    }
}
