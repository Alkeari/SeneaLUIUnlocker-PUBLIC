using System;
using System.Linq;
using System.Reflection;

namespace SeneaLUIUnlocker;

// Finds a type by its full name in each loaded assembly without listing every type of every assembly. Harmony's AccessTools.TypeByName lists them all when a name is missing, and on a SeneaL UI build that lacks types this plugin names (1.1.7), listing this plugin's own types fails and Harmony logs a warning with a stack trace for each lookup.
internal static class TypeLookup
{
    public static Type Find(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return null;
        }
        var direct = Type.GetType(fullName, false);
        if (direct != null)
        {
            return direct;
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    // This plugin's own types that load under the running SeneaL UI build; those naming members it lacks are left out.
    public static Type[] Loadable(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(type => type != null).ToArray();
        }
    }
}
