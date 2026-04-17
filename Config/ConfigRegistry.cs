using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace dubiousQOL.Config;

public static class ConfigRegistry
{
    private static readonly Dictionary<Type, FeatureConfig> _configs = new();
    private static List<FeatureConfig>? _sorted;

    public static IReadOnlyList<FeatureConfig> All => _sorted ??= _configs.Values.OrderBy(c => c.Name).ToList();

    public static T Get<T>() where T : FeatureConfig
    {
        if (_configs.TryGetValue(typeof(T), out var config))
            return (T)config;
        throw new InvalidOperationException($"Feature config '{typeof(T).Name}' not registered. Was ConfigRegistry.LoadAll() called?");
    }

    public static void LoadAll()
    {
        _configs.Clear();
        _sorted = null;

        try
        {
            var baseType = typeof(FeatureConfig);
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.IsAbstract || !baseType.IsAssignableFrom(type)) continue;
                var instance = (FeatureConfig)Activator.CreateInstance(type)!;
                _configs[type] = instance;
                instance.Load();
            }
            _sorted = _configs.Values.OrderBy(c => c.Name).ToList();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"ConfigRegistry.LoadAll: {e.Message}");
        }
    }
}
