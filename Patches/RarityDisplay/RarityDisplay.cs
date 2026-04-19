using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;

using dubiousQOL.UI;

namespace dubiousQOL.Patches;

[HarmonyPatch(typeof(NRelicCollectionCategory), "LoadRelics")]
public static class PatchRelicCollectionCategoryHeader
{
    [HarmonyPostfix]
    public static void Postfix(NRelicCollectionCategory __instance, RelicRarity relicRarity)
    {
        if (!RarityDisplayConfig.Instance.Enabled) return;
        try
        {
            var color = RarityColors.GetRelicColor(relicRarity);
            CompendiumHeaderRecolor.RecolorTitle(__instance._headerLabel, color);

            if (relicRarity == RelicRarity.Ancient)
            {
                foreach (var sub in __instance._subCategories)
                    CompendiumHeaderRecolor.RecolorTitle(sub._headerLabel, color);
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"RarityDisplay relic header: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(NPotionLabCategory), "LoadPotions")]
public static class PatchPotionLabCategoryHeader
{
    [HarmonyPostfix]
    public static void Postfix(NPotionLabCategory __instance, PotionRarity potionRarity)
    {
        if (!RarityDisplayConfig.Instance.Enabled) return;
        try
        {
            CompendiumHeaderRecolor.RecolorTitle(__instance._headerLabel, RarityColors.GetPotionColor(potionRarity));
        }
        catch (Exception e) { MainFile.Logger.Warn($"RarityDisplay potion header: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(NHoverTipSet), "Init")]
public static class PatchHoverTipInit
{
    [HarmonyPostfix]
    public static void Postfix(NHoverTipSet __instance, IEnumerable<IHoverTip> hoverTips)
    {
        if (!RarityDisplayConfig.Instance.Enabled) return;
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

            titleLabel.AddThemeColorOverride("font_color", color.Value);

            var icon = new TextureRect
            {
                Texture = RarityIconGenerator.GetDiamondIcon(color.Value),
                ExpandMode = TextureRect.ExpandModeEnum.KeepSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(16, 16),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };

            var titleParent = titleLabel.GetParent();
            var titleIndex = titleLabel.GetIndex();

            var hbox = new HBoxContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            hbox.AddThemeConstantOverride("separation", 6);

            titleParent.RemoveChild(titleLabel);
            titleParent.AddChild(hbox);
            titleParent.MoveChild(hbox, titleIndex);

            hbox.AddChild(icon);
            hbox.AddChild(titleLabel);

            icon.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        }
    }
}
