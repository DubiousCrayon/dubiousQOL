using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.addons.mega_text;

namespace dubiousQOL.UI;

/// <summary>
/// Rarity-to-color mappings for relics and potions. Used by hover tips,
/// compendium headers, and any UI that needs rarity-aware coloring.
/// </summary>
internal static class RarityColors
{
    public static readonly Color Starter = new(0.77f, 0.58f, 0.42f);
    public static readonly Color Common = new(0.63f, 0.63f, 0.63f);
    public static readonly Color Uncommon = new(0.28f, 0.78f, 0.28f);
    public static readonly Color Rare = new(1.0f, 0.84f, 0.0f);
    public static readonly Color Shop = new(0.31f, 0.76f, 0.97f);
    public static readonly Color Event = new(0.81f, 0.58f, 0.85f);
    public static readonly Color Ancient = new(1.0f, 0.34f, 0.13f);
    public static readonly Color Token = new(0.56f, 0.64f, 0.68f);

    public static Color GetRelicColor(RelicRarity rarity) => rarity switch
    {
        RelicRarity.Starter => Starter,
        RelicRarity.Common => Common,
        RelicRarity.Uncommon => Uncommon,
        RelicRarity.Rare => Rare,
        RelicRarity.Shop => Shop,
        RelicRarity.Event => Event,
        RelicRarity.Ancient => Ancient,
        _ => Colors.White,
    };

    public static Color GetPotionColor(PotionRarity rarity) => rarity switch
    {
        PotionRarity.Common => Common,
        PotionRarity.Uncommon => Uncommon,
        PotionRarity.Rare => Rare,
        PotionRarity.Event => Event,
        PotionRarity.Token => Token,
        _ => Colors.White,
    };
}

/// <summary>
/// Generates procedural diamond-shaped icon textures, cached by color.
/// Used for rarity indicators in hover tips and other UI.
/// </summary>
internal static class RarityIconGenerator
{
    private const int IconSize = 15;
    private static readonly Dictionary<Color, ImageTexture> _iconCache = new();

    public static ImageTexture GetDiamondIcon(Color color)
    {
        if (_iconCache.TryGetValue(color, out var cached))
            return cached;

        var image = Image.CreateEmpty(IconSize, IconSize, false, Image.Format.Rgba8);
        int center = IconSize / 2;

        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                int dist = Mathf.Abs(x - center) + Mathf.Abs(y - center);
                if (dist <= center - 1)
                {
                    float brightness = 1.0f - (dist / (float)(center * 2));
                    var fill = new Color(
                        Mathf.Min(color.R + brightness * 0.3f, 1.0f),
                        Mathf.Min(color.G + brightness * 0.3f, 1.0f),
                        Mathf.Min(color.B + brightness * 0.3f, 1.0f),
                        1.0f
                    );
                    image.SetPixel(x, y, fill);
                }
                else if (dist <= center)
                {
                    var edge = new Color(color.R * 0.7f, color.G * 0.7f, color.B * 0.7f, 1.0f);
                    image.SetPixel(x, y, edge);
                }
                else
                {
                    image.SetPixel(x, y, Colors.Transparent);
                }
            }
        }

        var texture = ImageTexture.CreateFromImage(image);
        _iconCache[color] = texture;
        return texture;
    }
}

/// <summary>
/// Recolors MegaRichTextLabel title text by swapping game color-effect BBCode
/// tags (e.g. [gold]...[/gold]) with standard [color=#HEX] spans.
/// </summary>
internal static class CompendiumHeaderRecolor
{
    private static readonly string[] ColorEffectTags =
        { "gold", "aqua", "blue", "green", "orange", "pink", "purple", "red" };

    public static void RecolorTitle(MegaRichTextLabel? label, Color color)
    {
        if (label == null) return;
        var text = label.Text;
        if (string.IsNullOrEmpty(text)) return;
        var hex = "#" + color.ToHtml(includeAlpha: false);
        var replaced = text;
        foreach (var tag in ColorEffectTags)
        {
            replaced = replaced.Replace($"[{tag}]", $"[color={hex}]");
            replaced = replaced.Replace($"[/{tag}]", "[/color]");
        }
        if (replaced != text)
            label.Text = replaced;
    }
}
