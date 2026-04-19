using System;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace dubiousQOL.UI;

/// <summary>
/// Shared modal scaffolding: error panels, escape key handling, back button creation.
/// </summary>
internal static class ModalHelper
{
    /// <summary>
    /// Creates a centered error panel (440x160) with a message and Close button
    /// that dismisses the modal via NModalContainer.Instance?.Clear().
    /// </summary>
    public static Control CreateErrorPanel(string controlName, string message)
    {
        var root = new Control { Name = controlName, MouseFilter = Control.MouseFilterEnum.Stop };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AnchorLeft = 0.5f; frame.AnchorRight = 0.5f;
        frame.AnchorTop = 0.5f; frame.AnchorBottom = 0.5f;
        frame.OffsetLeft = -220; frame.OffsetRight = 220;
        frame.OffsetTop = -80; frame.OffsetBottom = 80;
        root.AddChild(frame);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        frame.AddChild(vbox);

        var lbl = new Label { Text = message, HorizontalAlignment = HorizontalAlignment.Center };
        lbl.AddThemeFontSizeOverride("font_size", 18);
        vbox.AddChild(lbl);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(140, 36) };
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        close.Pressed += () => NModalContainer.Instance?.Clear();
        vbox.AddChild(close);

        return root;
    }

    /// <summary>
    /// Handles Escape key press by dismissing the modal.
    /// Call from _UnhandledInput. Returns true if the event was consumed.
    /// </summary>
    public static bool TryHandleEscape(InputEvent inputEvent, Node handler)
    {
        if (inputEvent is InputEventKey { Pressed: true } k && k.Keycode == Key.Escape)
        {
            NModalContainer.Instance?.Clear();
            handler.GetViewport().SetInputAsHandled();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Creates a back button by preloading the game's ui/back_button scene.
    /// Wires Released to dismiss the modal, adds to parent, deferred Enable.
    /// Returns null on failure — caller should add a fallback.
    /// </summary>
    public static NBackButton? CreateBackButton(Control parent, string name)
    {
        try
        {
            var backBtn = PreloadManager.Cache.GetScene(
                SceneHelper.GetScenePath("ui/back_button")
            ).Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
            backBtn.Name = name;
            backBtn.Released += _ => NModalContainer.Instance?.Clear();
            parent.AddChild(backBtn);
            backBtn.CallDeferred(NClickableControl.MethodName.Enable);
            return backBtn;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"ModalHelper.CreateBackButton: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a back button by cloning from a live NBackButton node.
    /// Uses Duplicate(ScriptsOnly) to avoid inheriting the source's signal bindings.
    /// Returns null on failure — caller should add a fallback.
    /// </summary>
    public static NBackButton? CloneBackButton(NBackButton source, Control parent, string name)
    {
        try
        {
            var clone = CloneHelper.Clone<NBackButton>(source, CloneHelper.ScriptsOnly);
            if (clone == null) return null;
            clone.Name = name;
            clone.Released += _ => NModalContainer.Instance?.Clear();
            parent.AddChild(clone);
            clone.Enable();
            return clone;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"ModalHelper.CloneBackButton: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a plain "Close" Button at bottom-left as fallback when both
    /// scene preloading and live-node cloning fail.
    /// </summary>
    public static Button CreateFallbackCloseButton()
    {
        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(140, 44) };
        close.AnchorLeft = 0f; close.AnchorRight = 0f;
        close.AnchorTop = 1f; close.AnchorBottom = 1f;
        close.OffsetLeft = 24; close.OffsetRight = 164;
        close.OffsetTop = -84; close.OffsetBottom = -40;
        close.Pressed += () => NModalContainer.Instance?.Clear();
        return close;
    }
}
