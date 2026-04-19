using System.Reflection;

namespace dubiousQOL.Utilities;

/// <summary>
/// Shared reflection accessors for getting/setting private fields and properties
/// on game objects. Centralizes the BindingFlags pattern used across features
/// that need to bypass Harmony or set up cloned scene nodes.
/// </summary>
internal static class ReflectionHelper
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void SetField(object target, string name, object? value, bool warnOnMiss = false)
    {
        var f = target.GetType().GetField(name, Flags);
        if (f != null)
            f.SetValue(target, value);
        else if (warnOnMiss)
            MainFile.Logger.Warn($"ReflectionHelper.SetField missing: {target.GetType().Name}.{name}");
    }

    public static object? GetField(object target, string name)
    {
        var f = target.GetType().GetField(name, Flags);
        return f?.GetValue(target);
    }

    public static T? GetField<T>(object target, string name) where T : class
    {
        var f = target.GetType().GetField(name, Flags);
        return f?.GetValue(target) as T;
    }

    public static void SetProperty(object target, string name, object? value, bool warnOnMiss = false)
    {
        var p = target.GetType().GetProperty(name, Flags);
        if (p != null && p.CanWrite)
            p.SetValue(target, value);
        else if (warnOnMiss)
            MainFile.Logger.Warn($"ReflectionHelper.SetProperty missing: {target.GetType().Name}.{name}");
    }
}
