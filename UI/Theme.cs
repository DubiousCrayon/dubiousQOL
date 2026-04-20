using Godot;

namespace dubiousQOL.UI;

/// <summary>
/// Shared design tokens for consistent styling across the mod's UI.
/// Feature-specific colors (per-orb, rarity, per-act) stay in their features.
/// </summary>
internal static class Theme
{
    // --- Panel backgrounds ---
    public static readonly Color PanelBg = new(0.12f, 0.14f, 0.18f, 0.92f);
    public static readonly Color PanelBgGame = new(0.117647f, 0.168627f, 0.188235f, 0.501961f);
    public static readonly Color PanelBgDark = new(0.06f, 0.06f, 0.10f, 0.90f);
    public static readonly Color PanelBorder = new(0.3f, 0.3f, 0.4f, 0.5f);
    public static readonly Color TitleBarBg = new(0.10f, 0.10f, 0.16f, 0.95f);
    public static readonly Color Backdrop = new(0f, 0f, 0f, 0.5f);

    // --- Text ---
    public static readonly Color TextPrimary = Colors.White;
    public static readonly Color TextDim = new(0.65f, 0.62f, 0.55f);
    public static readonly Color TextAccent = new(0.91f, 0.86f, 0.65f);
    public static readonly Color TextLabel = new(0.90f, 0.90f, 0.85f);
    public static readonly Color TextValue = new(0.70f, 0.85f, 0.70f);

    // --- Structural ---
    public static readonly Color Divider = new(0.909804f, 0.862745f, 0.745098f, 0.25098f);

    // --- Section headers ---
    public static readonly Color SectionHeader = new(0.95f, 0.80f, 0.30f);
    public static readonly Color SubSectionHeader = new(0.85f, 0.70f, 0.30f);

    // --- Stat categories ---
    public static readonly Color Damage = new(0.85f, 0.35f, 0.35f);
    public static readonly Color Block = new(0.35f, 0.65f, 0.90f);
    public static readonly Color HpLost = new(0.72f, 0.42f, 0.95f);

    // --- Tab styling (StsColors equivalents) ---
    public static readonly Color TabActive = new("FFF6E2");
    public static readonly Color TabInactive = new("FFF6E280");

    // --- Hover/interaction ---
    public static readonly Color HoverBrighten = new(1.2f, 1.2f, 1.2f);
    public const float HoverScale = 1.1f;

    // --- Input styling ---
    public static readonly Color InputFocusBg = new(0.2f, 0.22f, 0.28f, 0.6f);
    public static readonly Color InputSelection = new(0.4f, 0.5f, 0.6f, 0.5f);

    // --- Shadow defaults ---
    public static readonly Color ShadowDefault = new(0f, 0f, 0f, 0.6f);
    public static readonly Color ShadowStrong = new(0f, 0f, 0f, 0.9f);

    // --- Encounter room types ---
    public static readonly Color RoomBoss = new(0.95f, 0.35f, 0.35f);
    public static readonly Color RoomElite = new(0.95f, 0.70f, 0.25f);
}
