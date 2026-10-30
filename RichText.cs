using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;

namespace SeneaLUIUnlocker;

// SeneaL UI creates every label with rich text off (Ui.Text), so an item or text from another mod that colors itself with tags such as <color=#ff8800> shows the tags instead. A label given text that carries a formatting tag gets rich text turned on as the text arrives; text boxes the player types into are left alone.
internal static class RichText
{
    private static readonly Regex Tag = new(@"</?(color|b|i|u|s|size|sprite|mark|sup|sub|alpha|font|material|link|br|nobr|lowercase|uppercase|smallcaps|voffset|cspace|mspace|width|indent|line-height|pos|space|style|rotate|align|margin|gradient)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static ConfigEntry<bool> _enabled;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Interface", "Show Text Formatting", true,
            "Show the colors and styles that items and texts from other mods carry as formatting tags (such as a color tag), where SeneaL UI's windows would show the tags themselves.");
    }

    private static void Allow(TMP_Text text, string value)
    {
        if (_enabled == null || !_enabled.Value || text == null || text.richText || string.IsNullOrEmpty(value) || value.IndexOf('<') < 0 || !Tag.IsMatch(value))
        {
            return;
        }
        if (text.GetComponentInParent<TMP_InputField>() != null)
        {
            return;
        }
        text.richText = true;
    }

    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Setter)]
    internal static class TextPatch
    {
        private static void Prefix(TMP_Text __instance, string value) => Allow(__instance, value);
    }

    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.SetText), typeof(string), typeof(bool))]
    internal static class SetTextPatch
    {
        private static void Prefix(TMP_Text __instance, string sourceText) => Allow(__instance, sourceText);
    }

    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.SetText), typeof(string))]
    internal static class SetTextOnePatch
    {
        private static void Prefix(TMP_Text __instance, string sourceText) => Allow(__instance, sourceText);
    }
}
