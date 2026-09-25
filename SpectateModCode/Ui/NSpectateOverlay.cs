using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Everything drawn on top of combat while spectating: the "Spectating" label, the red screen border, and the
/// teammate's targeting arrow and selection reticle mirrored into our own view.
/// </summary>
public partial class NSpectateOverlay : Control
{
    private const float borderThickness = 6f;

    private static readonly Color borderColor = new(0.85f, 0.15f, 0.15f, 0.75f);
    /// <summary>Above the combat scene so the arrow is never buried behind a creature.</summary>
    private const int arrowZIndex = 50;

    private readonly List<ColorRect> _border = new();

    private Label _label = null!;
    private NTargetingArrow? _arrow;
    private NSpectateHand? _hand;
    private NCreature? _reticleCreature;

    public static NSpectateOverlay Create(NSpectateHand? hand)
    {
        NSpectateOverlay node = new()
        {
            Name = "SpectateOverlay",
            MouseFilter = MouseFilterEnum.Ignore
        };
        node._hand = hand;
        node.SetAnchorsPreset(LayoutPreset.FullRect);
        node.Build();
        return node;
    }

    /// <summary>Children are built up-front rather than in _Ready so the overlay is usable before it is in the tree.</summary>
    private void Build()
    {
        _label = new Label
        {
            Name = "SpectateLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false
        };
        _label.SetAnchorsPreset(LayoutPreset.TopWide);
        _label.OffsetTop = 24f;
        _label.OffsetBottom = 90f;
        _label.AddThemeFontSizeOverride("font_size", 40);
        _label.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.86f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
        _label.AddThemeConstantOverride("outline_size", 8);
        this.AddChildSafely(_label);

        foreach (LayoutPreset preset in new[]
                 {
                     LayoutPreset.TopWide, LayoutPreset.BottomWide, LayoutPreset.LeftWide, LayoutPreset.RightWide
                 })
        {
            ColorRect rect = new()
            {
                Color = borderColor,
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false
            };
            rect.SetAnchorsPreset(preset);
            bool horizontal = preset is LayoutPreset.TopWide or LayoutPreset.BottomWide;
            if (horizontal)
            {
                rect.OffsetTop = preset == LayoutPreset.TopWide ? 0f : -borderThickness;
                rect.OffsetBottom = preset == LayoutPreset.TopWide ? borderThickness : 0f;
            }
            else
            {
                rect.OffsetLeft = preset == LayoutPreset.LeftWide ? 0f : -borderThickness;
                rect.OffsetRight = preset == LayoutPreset.LeftWide ? borderThickness : 0f;
            }
            _border.Add(rect);
            this.AddChildSafely(rect);
        }
    }

    public void Refresh(Player? target)
    {
        bool spectating = target != null;
        bool showLabel = spectating && SpectateConfig.ShowSpectatingLabel;
        bool showBorder = spectating && SpectateConfig.ShowSpectatingBorder;

        _label.Visible = showLabel;
        if (showLabel)
        {
            _label.Text = $"Spectating {SpectateController.GetDisplayName(target!)}";
        }
        foreach (ColorRect rect in _border)
        {
            rect.Visible = showBorder;
        }
        if (!spectating)
        {
            ClearTargeting();
        }
        Visible = spectating;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        // Whoever's hand is being mirrored, peeked or spectated. The overlay itself is hidden during a bare peek, but
        // the arrow lives under the target manager, not under us, so it still shows.
        Player? target = SpectateView.VisualPlayer;
        PeerInputSynchronizer? sync = RunManager.Instance.InputSynchronizer;
        if (target == null || !SpectateConfig.ShowCardPlayVisuals || sync == null)
        {
            ClearTargeting();
            _hand?.SetPlayState(null, NSpectateHand.PlayVisual.None, Vector2.Zero, null);
            return;
        }

        // The synchronizer hands back a position relative to the control we pass in, so lift it to global space.
        Vector2 cursor = ToGlobalPosition(sync.GetControlSpaceFocusPosition(target.NetId, this));
        CardModel? picked = RunManager.Instance.HoveredModelTracker?.GetHoveredModel(target.NetId) as CardModel;
        bool targeting = sync.GetIsTargeting(target.NetId);

        // NTargetManager only raises the targeting flag for cards that need a creature picked. A card that needs no
        // target never sets it, so a held mouse button on a picked card is the game dragging it along the cursor.
        NSpectateHand.PlayVisual visual = targeting
            ? NSpectateHand.PlayVisual.Targeting
            : picked != null && sync.GetMouseDown(target.NetId)
                ? NSpectateHand.PlayVisual.Dragging
                : NSpectateHand.PlayVisual.None;

        // Handing the aimed creature to the hand lets the card redraw its numbers against that target, so a hit on
        // something vulnerable reads as the damage it will really do rather than the card's printed value.
        NCreature? aimed = targeting ? FindCreatureAt(cursor) : null;
        _hand?.SetPlayState(picked, visual, cursor, aimed?.Entity);

        if (!targeting)
        {
            ClearTargeting();
            return;
        }

        Vector2? origin = GetTargetingOrigin(target);
        NTargetingArrow? arrow = EnsureArrow();
        if (origin == null || arrow == null)
        {
            ClearTargeting();
            return;
        }

        // Drive the arrow by hand rather than through StartDrawingFrom: that helper hides the mouse cursor, which is
        // right when you are the one aiming and very wrong when you are only watching somebody else aim.
        arrow._followMouse = false;
        arrow._fromControl = null;
        arrow._fromPos = origin.Value;
        if (!arrow.Visible)
        {
            arrow._currentArrowPos = origin.Value;
            arrow.Visible = true;
        }
        arrow.UpdateDrawingTo(cursor);

        UpdateHoveredCreature(aimed, arrow);
    }

    /// <summary>
    /// The game's own arrow, parented where its own one lives so it inherits the same transform. It builds its
    /// segments in _Ready from preloaded combat textures, so a bare instance works.
    /// </summary>
    private NTargetingArrow? EnsureArrow()
    {
        if (_arrow != null && GodotObject.IsInstanceValid(_arrow))
        {
            return _arrow;
        }
        NTargetManager? manager = NRun.Instance?.GlobalUi?.TargetManager;
        if (manager == null)
        {
            return null;
        }
        _arrow = new NTargetingArrow
        {
            Name = "SpectateTargetingArrow",
            Visible = false,
            ZIndex = arrowZIndex
        };
        manager.AddChildSafely(_arrow);
        return _arrow;
    }

    // Control is a CanvasItem, not a Node2D, so it has no ToLocal/ToGlobal of its own.
    private Vector2 ToGlobalPosition(Vector2 local)
    {
        return GetGlobalTransform() * local;
    }

    /// <summary>The arrow starts at the mirrored card they picked up, or at their own creature if we can't find it.</summary>
    private Vector2? GetTargetingOrigin(Player target)
    {
        AbstractModel? selected = RunManager.Instance.HoveredModelTracker?.GetHoveredModel(target.NetId);
        if (selected is CardModel card && _hand != null)
        {
            Vector2? center = _hand.GetCardCenter(card);
            if (center != null)
            {
                return center.Value;
            }
        }

        NCreature? creature = NCombatRoom.Instance?.GetCreatureNode(target.Creature);
        return creature?.GetTopOfHitbox();
    }

    private static NCreature? FindCreatureAt(Vector2 globalCursor)
    {
        return NCombatRoom.Instance?.CreatureNodes
            .FirstOrDefault(c => c.IsInteractable
                                 && GodotObject.IsInstanceValid(c.Hitbox)
                                 && c.Hitbox.GetGlobalRect().HasPoint(globalCursor));
    }

    /// <summary>Mirrors the reticle and the arrow tint on whatever the teammate's cursor is over.</summary>
    private void UpdateHoveredCreature(NCreature? hit, NTargetingArrow arrow)
    {
        if (hit == _reticleCreature)
        {
            return;
        }
        HideReticle();
        _reticleCreature = hit;
        if (hit == null)
        {
            arrow.SetHighlightingOff();
            return;
        }
        hit.ShowSingleSelectReticle();
        // Same red-for-enemy / green-for-ally tint the game uses when you aim yourself.
        arrow.SetHighlightingOn(!hit.Entity.IsPlayer);
    }

    private void ClearTargeting()
    {
        if (_arrow != null && GodotObject.IsInstanceValid(_arrow) && _arrow.Visible)
        {
            // Deliberately not StopDrawing(): it would force our own mouse cursor back to visible.
            _arrow.Visible = false;
            _arrow._currentArrowPos = null;
            _arrow.SetHighlightingOff();
        }
        HideReticle();
    }

    private void HideReticle()
    {
        if (_reticleCreature != null && GodotObject.IsInstanceValid(_reticleCreature))
        {
            _reticleCreature.HideSingleSelectReticle();
        }
        _reticleCreature = null;
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        HideReticle();
        if (_arrow != null && GodotObject.IsInstanceValid(_arrow))
        {
            _arrow.QueueFreeSafely();
        }
        _arrow = null;
    }
}
