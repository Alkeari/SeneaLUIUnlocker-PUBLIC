using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal static class KeyInput
{
    // Held while the main key and each of its modifiers are down. Other keys held alongside do not cancel it, unlike KeyboardShortcut.IsPressed, so a click modifier still counts while another one is down.
    public static bool Held(KeyboardShortcut key) =>
        key.MainKey != KeyCode.None && Key(key.MainKey) && key.Modifiers.All(Key);

    // Pressed this frame: the main key went down while each modifier was already held.
    public static bool Down(KeyboardShortcut key) =>
        key.MainKey != KeyCode.None && KeyDown(key.MainKey) && key.Modifiers.All(Key);

    private static readonly KeyCode[] ModifierKeys = [KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftAlt, KeyCode.RightAlt];

    // A shortcut pressed this frame with exactly its own Shift, Ctrl and Alt keys held, so Ctrl+1 and Ctrl+Shift+1 stay apart, while movement or any other key held alongside does not cancel it, as BepInEx's KeyboardShortcut.IsDown would (holding W while pressing Ctrl+1 did nothing).
    public static bool Pressed(KeyboardShortcut key) =>
        Down(key) && ModifierKeys.All(modifier => key.MainKey == modifier || key.Modifiers.Contains(modifier) || !Key(modifier));

    // The game's input layer has no key for a mouse button, so Mouse0 to Mouse4 are read as its mouse buttons.
    private static bool Key(KeyCode code) =>
        Mouse(code, out var button) ? ZInput.GetMouseButton(button) : ZInput.GetKey(code, true);

    private static bool KeyDown(KeyCode code) =>
        Mouse(code, out var button) ? ZInput.GetMouseButtonDown(button) : ZInput.GetKeyDown(code, true);

    private static bool Mouse(KeyCode code, out int button)
    {
        button = code - KeyCode.Mouse0;
        return button is >= 0 and <= 4;
    }
}
