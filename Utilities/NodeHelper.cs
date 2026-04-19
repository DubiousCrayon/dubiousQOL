using System;
using Godot;

namespace dubiousQOL.Utilities;

/// <summary>
/// Generic node tree operations: descendant search, scene extraction.
/// </summary>
internal static class NodeHelper
{
    /// <summary>
    /// Depth-first search for the first descendant of type T in the node tree.
    /// Returns null if no descendant matches.
    /// </summary>
    public static T? FindDescendant<T>(Node root) where T : class
    {
        if (root is T match) return match;
        foreach (Node child in root.GetChildren())
        {
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Extracts a node from a packed scene: loads the scene, instantiates it,
    /// finds the target node (by path or type search), detaches it from the
    /// scene tree, and QueueFrees the rest.
    ///
    /// If nodePath is provided, uses GetNodeOrNull. Otherwise uses FindDescendant.
    /// Returns null on failure.
    /// </summary>
    public static T? ExtractFromScene<T>(string scenePath, string? nodePath = null) where T : Node
    {
        try
        {
            var packed = ResourceLoader.Load<PackedScene>(
                scenePath, null, ResourceLoader.CacheMode.Reuse);
            if (packed == null) return null;

            var root = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
            T? target = nodePath != null
                ? root.GetNodeOrNull<T>(nodePath)
                : FindDescendant<T>(root);

            if (target != null)
                target.GetParent()?.RemoveChild(target);

            root.QueueFree();
            return target;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"NodeHelper.ExtractFromScene<{typeof(T).Name}>: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads a scene, searches for a TextureRect matching any of the candidate
    /// paths, reads its Texture, and frees the scene instance.
    ///
    /// Each candidate is tried two ways:
    /// 1. Direct path — GetNodeOrNull&lt;TextureRect&gt;(path) for known layout paths.
    /// 2. Deep search — FindChild(path) then GetNodeOrNull("Icon") on the result,
    ///    for containers nested deep in the tree (e.g. "StatisticsButton").
    ///
    /// Returns the first Texture2D found, or null.
    /// </summary>
    public static Texture2D? ExtractTextureFromScene(string scenePath, params string[] candidatePaths)
    {
        try
        {
            if (!ResourceLoader.Exists(scenePath)) return null;
            var packed = ResourceLoader.Load<PackedScene>(
                scenePath, null, ResourceLoader.CacheMode.Reuse);
            if (packed == null) return null;

            var root = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
            try
            {
                foreach (var path in candidatePaths)
                {
                    // Direct path: the path points to a TextureRect itself.
                    var direct = root.GetNodeOrNull<TextureRect>(path);
                    if (direct?.Texture is Texture2D t) return t;

                    // Deep search: path names a container somewhere in the tree;
                    // look for an "Icon" TextureRect child on it.
                    var found = root.FindChild(path, recursive: true, owned: false);
                    if (found != null)
                    {
                        var icon = found.GetNodeOrNull<TextureRect>("Icon");
                        if (icon?.Texture is Texture2D t2) return t2;
                        // The found node itself might be the TextureRect.
                        if (found is TextureRect tr && tr.Texture is Texture2D t3) return t3;
                    }
                }
                return null;
            }
            finally { root.QueueFree(); }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"NodeHelper.ExtractTextureFromScene: {e.Message}");
            return null;
        }
    }
}
