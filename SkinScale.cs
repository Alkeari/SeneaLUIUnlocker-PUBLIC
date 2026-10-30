using System.Reflection;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// SeneaL UI's drawing scale, Skin.Ppu, read and set by name: 1.2.0-preview.1 declares it a field, 1.2.0-preview.2 a property that also empties SeneaL UI's drawing cache when it changes, and code compiled against one form fails against the other.
internal static class SkinScale
{
    private static readonly System.Type Skin = TypeLookup.Find("SeneaLUI.Theme.Skin");
    private static readonly FieldInfo Field = Skin == null ? null : Quiet.Field(Skin, "Ppu");
    private static readonly PropertyInfo Property = Skin == null || Field != null ? null : Quiet.Property(Skin, "Ppu");

    public static bool Available => Field != null || Property != null;

    public static float Ppu
    {
        get => Field != null ? (float)Field.GetValue(null) : Property != null ? (float)Property.GetValue(null, null) : 100f;
        set
        {
            if (Field != null)
            {
                Field.SetValue(null, value);
            }
            else
            {
                Property?.SetValue(null, value, null);
            }
        }
    }
}
