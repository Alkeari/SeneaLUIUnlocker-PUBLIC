using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal enum ArrivalText
{
    Show,
    HideMine,
    HideAll
}

// The game draws every say, shout, whisper and ping as text over the speaker (Chat.AddInworldText), and pushes it to the nearest screen edge while the speaker is off screen. Each player's first spawn of a session shouts "I have arrived!" that way.
// Here the arrival shout's text can be left out, and text from an off-screen speaker gathers at an element placed in SeneaL UI's HUD editor instead of the screen edge. Pings keep the edge, since there they point the way to what was pinged.
internal static class ChatBubbles
{
    private const string Section = "Chat";
    private const float Gap = 4f;

    private static readonly ThemedCard Card = new ThemedCard(new Vector2(0.5f, 0.82f), new Vector2(0.5f, 1f), Vector2.zero, 32000, 14f)
        .Element("OffScreenChat", "Off-Screen Chat Text", "Viking: COME HERE\nRagnar: over by the boat");

    private static ConfigEntry<ArrivalText> _arrival;
    private static ConfigEntry<bool> _gather;

    public static void Bind(ConfigFile cfg)
    {
        _arrival = cfg.Bind(Section, "Arrival Text Above Head", ArrivalText.HideAll,
            "The \"I have arrived!\" text the game shows over a player who just joined, which the chat window also shows: Show, Hide Mine (only your own) or Hide All.");
        _gather = cfg.Bind(Section, "Off-Screen Chat Text", true,
            "Chat text from a player who is off screen shows centered on the Off-Screen Chat Text element, placed in SeneaL UI's HUD editor, instead of at the edge of the screen.");
    }

    public static void Tick()
    {
        Card.Prepare();
        Card.Show(null);
    }

    private static bool IsArrival(string text)
    {
        var mine = Localization.instance == null ? null : Localization.instance.Localize("$text_player_arrived");
        return text == mine || string.Equals(text, "I have arrived!", System.StringComparison.OrdinalIgnoreCase);
    }

    [HarmonyPatch(typeof(Chat), "AddInworldText")]
    internal static class AddPatch
    {
        private static bool Prefix(long senderID, Talker.Type type, string text)
        {
            if (_arrival == null || _arrival.Value == ArrivalText.Show || type != Talker.Type.Shout || !ServerRules.Allows("Chat") || !IsArrival(text))
            {
                return true;
            }
            var mine = ZNet.instance != null && senderID == ZNet.instance.LocalPlayerCharacterID.UserID;
            return !(mine || _arrival.Value == ArrivalText.HideAll);
        }
    }

    [HarmonyPatch(typeof(Chat), "UpdateWorldTexts")]
    internal static class PlacePatch
    {
        private static void Postfix(Chat __instance)
        {
            var area = Card.Rect;
            var camera = Utils.GetMainCamera();
            if (_gather == null || !_gather.Value || !ServerRules.Allows("Chat") || area == null || camera == null)
            {
                return;
            }
            var corners = new Vector3[4];
            area.GetWorldCorners(corners);
            var x = (corners[1].x + corners[2].x) * 0.5f;
            var y = corners[1].y;
            foreach (var text in OffScreen(__instance.WorldTexts, camera))
            {
                var rect = (RectTransform)text.m_gui.transform;
                var size = Vector2.Scale(rect.rect.size, rect.lossyScale);
                rect.position = new Vector3(x + size.x * (rect.pivot.x - 0.5f), y - size.y * (1f - rect.pivot.y), rect.position.z);
                y -= size.y + Gap;
            }
        }

        // The same test the game makes before pushing a text to the screen edge.
        private static IEnumerable<Chat.WorldTextInstance> OffScreen(List<Chat.WorldTextInstance> texts, Camera camera)
        {
            foreach (var text in texts)
            {
                if (text.m_type == Talker.Type.Ping || text.m_gui == null)
                {
                    continue;
                }
                Vector3 point;
                if (text.m_go != null)
                {
                    var character = text.m_go.GetComponent<Character>();
                    point = (character != null ? character.GetHeadPoint() : text.m_go.transform.position) + Vector3.up * 0.3f;
                }
                else
                {
                    point = text.m_position + Vector3.up * 0.3f;
                }
                var screen = camera.WorldToScreenPointScaled(point);
                if (screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height || screen.z < 0f)
                {
                    yield return text;
                }
            }
        }
    }
}
