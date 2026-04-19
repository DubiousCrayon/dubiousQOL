using Godot;

namespace dubiousQOL.UI;

/// <summary>
/// Named constants for Godot's Node.Duplicate() flags and typed clone helpers.
/// Each flag combination serves a specific purpose — see per-constant docs.
/// </summary>
internal static class CloneHelper
{
    /// <summary>
    /// Visual hierarchy only. No scripts, signals, or groups.
    /// Use when the source node's scripts crash outside their original context
    /// (e.g., NCommonTooltipsTickbox.OnUntick dereferences _settingsScreen).
    /// </summary>
    public const int VisualOnly = 0;

    /// <summary>
    /// Scripts only. Keeps _Ready/_Process behavior but skips signal bindings
    /// so the clone doesn't inherit the source's event handlers.
    /// Use for interactive controls (buttons, sliders, arrows) that need their
    /// own behavior but must not trigger the source's actions.
    /// </summary>
    public const int ScriptsOnly = 4;

    /// <summary>
    /// Scripts + groups + instantiation, skip signals.
    /// Use when the node needs its full scene structure (groups, instantiation
    /// state) but must not inherit signal connections.
    /// </summary>
    public const int FullNoSignals = 14;

    /// <summary>
    /// Everything: scripts, signals, groups, instantiation.
    /// Use for static UI elements (labels, dividers, tabs) where inherited
    /// signals are harmless or desired.
    /// </summary>
    public const int Full = 15;

    /// <summary>
    /// Clone a node with the specified flags, returning it as T.
    /// Returns null if the Duplicate call fails or the cast doesn't match.
    /// </summary>
    public static T? Clone<T>(Node source, int flags = ScriptsOnly) where T : Node
    {
        return source.Duplicate(flags) as T;
    }

    /// <summary>
    /// Clone a node and re-clone its ShaderMaterial so the copy has an
    /// independent HSV/brightness state. Used for tabs and tickboxes where
    /// each instance needs to animate independently.
    /// If materialChildPath is provided, the material is taken from that child node.
    /// Otherwise, the material is taken from the root clone itself.
    /// </summary>
    public static T? CloneWithMaterial<T>(Node source, int flags = Full,
        string? materialChildPath = null) where T : Node
    {
        var clone = source.Duplicate(flags) as T;
        if (clone == null) return null;

        // Find the node that holds the material to re-clone.
        Node? materialNode = materialChildPath != null
            ? clone.GetNodeOrNull(materialChildPath)
            : clone;

        if (materialNode is CanvasItem canvasItem && canvasItem.Material is ShaderMaterial sm)
            canvasItem.Material = (Material)sm.Duplicate();

        return clone;
    }
}
