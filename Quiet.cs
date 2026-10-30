using System;
using System.Reflection;

namespace SeneaLUIUnlocker;

// Lookups of what an optional mod or another SeneaL UI build may lack. Harmony's AccessTools logs a warning for every miss, which reads as an error in a player's log for a mod they simply do not have.
internal static class Quiet
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static Type Type(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(name, false);
            if (type != null)
            {
                return type;
            }
        }
        return null;
    }

    public static FieldInfo Field(Type type, string name) => type?.GetField(name, Any);

    public static PropertyInfo Property(Type type, string name) => type?.GetProperty(name, Any);
}
