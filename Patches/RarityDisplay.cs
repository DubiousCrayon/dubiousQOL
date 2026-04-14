using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace dubiousQOL.Patches;

public static class RarityColors
{
    // Relic rarities
    public static readonly Color Starter = new(0.74f, 0.74f, 0.74f);    // #BDBDBD light grey
    public static readonly Color Common = new(0.63f, 0.63f, 0.63f);     // #A0A0A0 grey
    public static readonly Color Uncommon = new(0.28f, 0.78f, 0.28f);   // #48C848 green
    public static readonly Color Rare = new(1.0f, 0.84f, 0.0f);        // #FFD700 gold
    public static readonly Color Shop = new(0.31f, 0.76f, 0.97f);      // #4FC3F7 light blue
    public static readonly Color Event = new(0.81f, 0.58f, 0.85f);     // #CE93D8 purple
    public static readonly Color Ancient = new(1.0f, 0.34f, 0.13f);    // #FF5722 deep orange
    public static readonly Color Token = new(0.56f, 0.64f, 0.68f);     // #90A4AE blue grey

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

public static class RarityIconGenerator
{
    private const int IconSize = 16;
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
                // Diamond shape: |x - center| + |y - center| <= center
                int dist = Mathf.Abs(x - center) + Mathf.Abs(y - center);
                if (dist <= center - 1)
                {
                    // Inner fill with slight brightness gradient from center
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
                    // Edge/border - slightly darker
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

[HarmonyPatch(typeof(NHoverTipSet), "Init")]
public static class PatchHoverTipInit
{
    [HarmonyPostfix]
    public static void Postfix(NHoverTipSet __instance, IEnumerable<IHoverTip> hoverTips)
    {
        var container = __instance._textHoverTipContainer;
        if (container == null) return;

        int childIndex = 0;
        foreach (var tip in hoverTips)
        {
            if (tip is not HoverTip hoverTip) continue;
            if (childIndex >= container.GetChildCount()) break;

            var panel = container.GetChild(childIndex) as Control;
            childIndex++;
            if (panel == null) continue;

            Color? color = null;
            if (hoverTip.CanonicalModel is RelicModel relic)
                color = RarityColors.GetRelicColor(relic.Rarity);
            else if (hoverTip.CanonicalModel is PotionModel potion)
                color = RarityColors.GetPotionColor(potion.Rarity);

            if (!color.HasValue) continue;

            var titleLabel = panel.GetNodeOrNull<Label>("%Title");
            if (titleLabel == null || !titleLabel.Visible) continue;

            // Color the title text
            titleLabel.AddThemeColorOverride("font_color", color.Value);

            // Add diamond icon before the title
            var icon = new TextureRect
            {
                Texture = RarityIconGenerator.GetDiamondIcon(color.Value),
                ExpandMode = TextureRect.ExpandModeEnum.KeepSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(16, 16),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };

            // Wrap the title in an HBoxContainer so icon sits next to it
            var titleParent = titleLabel.GetParent();
            var titleIndex = titleLabel.GetIndex();

            var hbox = new HBoxContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            hbox.AddThemeConstantOverride("separation", 6);

            // Remove title from its parent, put hbox in its place, then add icon + title to hbox
            titleParent.RemoveChild(titleLabel);
            titleParent.AddChild(hbox);
            titleParent.MoveChild(hbox, titleIndex);

            hbox.AddChild(icon);
            hbox.AddChild(titleLabel);

            // Vertically center the icon relative to the text
            icon.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        }
    }
}
