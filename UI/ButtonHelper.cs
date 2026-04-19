using System;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;

using dubiousQOL.Utilities;

namespace dubiousQOL.UI;

/// <summary>
/// Shared button utilities: hover/click SFX wiring, anchor-relative positioning,
/// and toggle state management. General-purpose — usable anywhere, not just run history.
/// </summary>
internal static class ButtonHelper
{
    /// <summary>
    /// Wires hover brightening (scale up, modulate brighten) and SFX onto a raw
    /// BaseButton. Raw TextureButton/Button doesn't route through NButton.OnFocus,
    /// so hover/click SFX must be triggered by hand.
    /// PivotOffset is set to center the scale transform.
    /// </summary>
    public static void WireHoverAndClickSfx(BaseButton btn, float btnSize)
    {
        btn.PivotOffset = new Vector2(btnSize * 0.5f, btnSize * 0.5f);

        btn.MouseEntered += () =>
        {
            if (!btn.Disabled)
            {
                btn.Modulate = Theme.HoverBrighten;
                btn.Scale = new Vector2(Theme.HoverScale, Theme.HoverScale);
                SfxCmd.Play("event:/sfx/ui/clicks/ui_hover");
            }
        };

        btn.MouseExited += () =>
        {
            btn.Modulate = Colors.White;
            btn.Scale = Vector2.One;
        };

        // Fire click SFX on mouse-down (matching NButton.OnPress via HandleMousePress),
        // not on Pressed/release. Godot's BaseButton.Pressed defaults to release-mode.
        btn.ButtonDown += () =>
        {
            if (!btn.Disabled) SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
        };
    }

    /// <summary>
    /// Positions btn to the left of an anchor control with a gap, vertically centered.
    /// If anchor is null, falls back to bottom-left absolute positioning.
    /// The btn is added as a child of parent.
    /// </summary>
    public static void PositionLeftOf(BaseButton btn, Control? anchor, Control parent,
        float btnSize, float gap, float fallbackLeftInset = 110f, float fallbackBottomInset = 80f)
    {
        parent.AddChild(btn);

        if (anchor != null)
        {
            btn.AnchorLeft = anchor.AnchorLeft;
            btn.AnchorRight = anchor.AnchorRight;
            btn.AnchorTop = anchor.AnchorTop;
            btn.AnchorBottom = anchor.AnchorBottom;
            btn.OffsetRight = anchor.OffsetLeft - gap;
            btn.OffsetLeft = btn.OffsetRight - btnSize;
            float anchorCenter = (anchor.OffsetTop + anchor.OffsetBottom) * 0.5f;
            btn.OffsetTop = anchorCenter - btnSize * 0.5f;
            btn.OffsetBottom = anchorCenter + btnSize * 0.5f;
        }
        else
        {
            btn.AnchorLeft = 0f; btn.AnchorRight = 0f;
            btn.AnchorTop = 1f; btn.AnchorBottom = 1f;
            btn.OffsetLeft = fallbackLeftInset;
            btn.OffsetRight = fallbackLeftInset + btnSize;
            btn.OffsetTop = -(fallbackBottomInset + btnSize);
            btn.OffsetBottom = -fallbackBottomInset;
        }
    }

    /// <summary>
    /// Updates a button's disabled/enabled visual state: Disabled flag, MouseFilter,
    /// Modulate reset, Scale reset, and TooltipText.
    /// </summary>
    public static void SetToggleState(BaseButton btn, bool enabled,
        string enabledTooltip, string disabledTooltip)
    {
        btn.Disabled = !enabled;
        btn.MouseFilter = enabled ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        if (!enabled)
        {
            btn.Modulate = Colors.White;
            btn.Scale = Vector2.One;
        }
        btn.TooltipText = enabled ? enabledTooltip : disabledTooltip;
    }

    /// <summary>
    /// Clones a game-styled navigation arrow (NGoldArrowButton / NRunHistoryArrowButton)
    /// with ScriptsOnly flags. Sets the name and _isLeft field via reflection for
    /// NRunHistoryArrowButton sources (avoids calling the IsLeft setter which dereferences
    /// _icon before _Ready). The clone must be added to the scene tree before calling
    /// <see cref="ResetClonedArrow"/> — _Ready wires _icon and other fields that the
    /// reset depends on.
    /// </summary>
    public static Control? CloneGameArrow(Node source, string name, bool isLeft)
    {
        var clone = CloneHelper.Clone<Control>(source, CloneHelper.ScriptsOnly);
        if (clone == null) return null;
        clone.Name = name;
        if (clone is NRunHistoryArrowButton arrowBtn)
            ReflectionHelper.SetField(arrowBtn, "_isLeft", isLeft);
        return clone;
    }

    /// <summary>
    /// Resets a cloned arrow's interactive and visual state after it has been added
    /// to the scene tree. Runs a Disable→Enable cycle to restore _isEnabled and
    /// FocusMode (the source may have been disabled), then resets Modulate and Scale
    /// on both the arrow and its inner TextureRect child.
    /// </summary>
    public static void ResetClonedArrow(Control arrow)
    {
        if (arrow is NClickableControl click)
        {
            try { click.Disable(); click.Enable(); }
            catch (Exception e) { MainFile.Logger.Warn($"ButtonHelper.ResetClonedArrow: {e.Message}"); }
        }
        arrow.Modulate = Colors.White;
        arrow.Scale = Vector2.One;
        var icon = arrow.GetNodeOrNull<TextureRect>("TextureRect");
        if (icon != null)
        {
            icon.Modulate = Colors.White;
            icon.Scale = Vector2.One;
        }
    }
}
