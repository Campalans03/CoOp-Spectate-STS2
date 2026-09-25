using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// The little eye next to a player's icon in the top-left party list: click it to start or stop watching that player,
/// without having to find and click their character in the combat scene.
/// <para>
/// The eye is drawn rather than textured so the mod needs no art assets, and the clickable part is a stock
/// <see cref="NButton" /> so it picks up the game's hover and click sounds.
/// </para>
/// </summary>
public partial class NSpectateToggleButton : Control
{
    private const float buttonSize = 34f;
    private const float iconGap = 6f;

    private static readonly Color idleColor = new(0.78f, 0.74f, 0.68f, 0.85f);
    private static readonly Color activeColor = new(0.90f, 0.25f, 0.25f);
    private static readonly Color disabledColor = new(0.55f, 0.53f, 0.50f, 0.35f);

    private enum Look
    {
        /// <summary>Clicking starts watching this player.</summary>
        Idle,

        /// <summary>We are watching this player; clicking stops.</summary>
        Watching,

        /// <summary>This is us, and we are watching somebody else; clicking comes back.</summary>
        Return,

        /// <summary>Nothing to do right now.</summary>
        Unavailable
    }

    private Player _player = null!;
    private Control? _followTarget;
    private Look _look = Look.Unavailable;

    public static NSpectateToggleButton Create(Player player)
    {
        NSpectateToggleButton node = new()
        {
            Name = "SpectateToggle",
            Player = player,
            CustomMinimumSize = new Vector2(buttonSize, buttonSize),
            Size = new Vector2(buttonSize, buttonSize),
            MouseFilter = MouseFilterEnum.Ignore,
            TooltipText = "Spectate"
        };

        NButton hitbox = new()
        {
            Name = "Hitbox",
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None
        };
        hitbox.SetAnchorsPreset(LayoutPreset.FullRect);
        node.AddChildSafely(hitbox);
        hitbox.Released += node.OnReleased;
        return node;
    }

    public Player Player
    {
        get => _player;
        private set => _player = value;
    }

    /// <summary>
    /// Sits the button next to <paramref name="icon" />. When the icon lives in a box container we insert ourselves
    /// right after it and let the container do the layout; otherwise we track the icon's global position each frame.
    /// Either way the mod makes no assumptions about the shape of the game's scene.
    /// </summary>
    public void AttachNextTo(Control icon, Control fallbackParent)
    {
        if (icon.GetParent() is BoxContainer box)
        {
            box.AddChildSafely(this);
            box.MoveChildSafely(this, icon.GetIndex() + 1);
            MouseFilter = MouseFilterEnum.Ignore;
            return;
        }

        _followTarget = icon;
        fallbackParent.AddChildSafely(this);
    }

    public override void _Ready()
    {
        SpectateController.TargetChanged += OnTargetChanged;
        Refresh();
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        SpectateController.TargetChanged -= OnTargetChanged;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_followTarget != null && GodotObject.IsInstanceValid(_followTarget))
        {
            GlobalPosition = _followTarget.GlobalPosition
                             + new Vector2(_followTarget.Size.X * _followTarget.Scale.X + iconGap, 0f);
        }
        // Availability follows combat, which has no single event we can hang this off, so re-evaluate each frame.
        // Refresh only redraws when the look actually changes.
        Refresh();
    }

    public override void _Draw()
    {
        if (_look == Look.Unavailable)
        {
            return;
        }

        Color color = _look switch
        {
            Look.Watching => activeColor,
            Look.Return => activeColor,
            _ => idleColor
        };

        Vector2 centre = Size * 0.5f;
        float halfWidth = Size.X * 0.40f;
        float halfHeight = Size.Y * 0.24f;
        const int steps = 14;

        Vector2[] upper = new Vector2[steps + 1];
        Vector2[] lower = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            float x = centre.X - halfWidth + 2f * halfWidth * t;
            float lid = halfHeight * Mathf.Sin(t * Mathf.Pi);
            upper[i] = new Vector2(x, centre.Y - lid);
            lower[i] = new Vector2(x, centre.Y + lid);
        }

        DrawPolyline(upper, color, 2f, antialiased: true);
        DrawPolyline(lower, color, 2f, antialiased: true);
        DrawCircle(centre, Size.X * 0.12f, color);

        if (_look == Look.Return)
        {
            // A slash over the eye reads as "stop watching and come back to me".
            DrawLine(
                centre + new Vector2(-halfWidth, halfHeight * 1.6f),
                centre + new Vector2(halfWidth, -halfHeight * 1.6f),
                color, 2.5f, antialiased: true);
        }
    }

    private void OnTargetChanged(Player? target)
    {
        Refresh();
    }

    private void Refresh()
    {
        Look look = ResolveLook();
        if (look == _look)
        {
            return;
        }
        _look = look;
        Visible = look != Look.Unavailable;
        QueueRedraw();
    }

    private Look ResolveLook()
    {
        if (!SpectateConfig.ShowSpectateButtons || !InActiveCombat())
        {
            return Look.Unavailable;
        }

        if (LocalContext.IsMe(_player))
        {
            // Our own row is only useful as a way back.
            return SpectateController.IsSpectating ? Look.Return : Look.Unavailable;
        }

        if (SpectateController.Target == _player)
        {
            return Look.Watching;
        }
        return SpectateController.CanSpectate(_player) ? Look.Idle : Look.Unavailable;
    }

    private static bool InActiveCombat()
    {
        return SpectateController.State != null
               && CombatManager.Instance.IsInProgress
               && NCombatRoom.Instance is { Mode: CombatRoomMode.ActiveCombat };
    }

    private void OnReleased(NClickableControl _)
    {
        try
        {
            if (LocalContext.IsMe(_player))
            {
                SpectateController.Stop();
            }
            else
            {
                SpectateController.Toggle(_player);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Spectate button failed: {e}");
        }
    }
}
