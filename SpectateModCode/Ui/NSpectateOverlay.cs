using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Everything drawn on top of combat while spectating: the "Spectating" label, the red screen border, and the
/// teammate's targeting line and selection reticle mirrored into our own view.
/// </summary>
public partial class NSpectateOverlay : Control
{
    private const float borderThickness = 6f;

    private static readonly Color borderColor = new(0.85f, 0.15f, 0.15f, 0.75f);
    private static readonly Color lineColor = new(0.9f, 0.25f, 0.25f, 0.85f);

    private readonly List<ColorRect> _border = new();

    private Label _label = null!;
    private Line2D _targetingLine = null!;
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

        _targetingLine = new Line2D
        {
            Name = "SpectateTargetingLine",
            Width = 6f,
            DefaultColor = lineColor,
            Visible = false
        };
        this.AddChildSafely(_targetingLine);
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
        Player? target = SpectateController.Target;
        if (target == null || !SpectateConfig.ShowCardPlayVisuals)
        {
            ClearTargeting();
            return;
        }

        PeerInputSynchronizer? sync = RunManager.Instance.InputSynchronizer;
        if (sync == null || !sync.GetIsTargeting(target.NetId))
        {
            ClearTargeting();
            return;
        }

        Vector2 cursor = sync.GetControlSpaceFocusPosition(target.NetId, this);
        Vector2? origin = GetTargetingOrigin(target);
        if (origin == null)
        {
            ClearTargeting();
            return;
        }

        _targetingLine.Visible = true;
        _targetingLine.ClearPoints();
        _targetingLine.AddPoint(origin.Value);
        _targetingLine.AddPoint(cursor);

        UpdateReticle(ToGlobalPosition(cursor));
    }

    // Control is a CanvasItem, not a Node2D, so it has no ToLocal/ToGlobal of its own.
    private Vector2 ToLocalPosition(Vector2 global)
    {
        return GetGlobalTransform().AffineInverse() * global;
    }

    private Vector2 ToGlobalPosition(Vector2 local)
    {
        return GetGlobalTransform() * local;
    }

    /// <summary>The line starts at the mirrored card they picked up, or at their own creature if we can't find it.</summary>
    private Vector2? GetTargetingOrigin(Player target)
    {
        AbstractModel? selected = RunManager.Instance.HoveredModelTracker?.GetHoveredModel(target.NetId);
        if (selected is CardModel card && _hand != null)
        {
            Vector2? center = _hand.GetCardCenter(card);
            if (center != null)
            {
                return ToLocalPosition(center.Value);
            }
        }

        NCreature? creature = NCombatRoom.Instance?.GetCreatureNode(target.Creature);
        return creature == null ? null : ToLocalPosition(creature.GetTopOfHitbox());
    }

    /// <summary>Mirrors the reticle the teammate sees on whatever their cursor is over.</summary>
    private void UpdateReticle(Vector2 globalCursor)
    {
        NCreature? hit = NCombatRoom.Instance?.CreatureNodes
            .FirstOrDefault(c => c.IsInteractable
                                 && GodotObject.IsInstanceValid(c.Hitbox)
                                 && c.Hitbox.GetGlobalRect().HasPoint(globalCursor));
        if (hit == _reticleCreature)
        {
            return;
        }
        HideReticle();
        _reticleCreature = hit;
        _reticleCreature?.ShowSingleSelectReticle();
    }

    private void ClearTargeting()
    {
        if (_targetingLine.Visible)
        {
            _targetingLine.Visible = false;
            _targetingLine.ClearPoints();
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
    }
}
