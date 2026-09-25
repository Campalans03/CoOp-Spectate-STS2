using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// The little eye at the end of a player's row in the top-left party list: click it to start or stop watching that
/// player, without having to find and click their character in the combat scene.
/// <para>
/// The eye is drawn rather than textured so the mod needs no art assets, and the clickable part is a stock
/// <see cref="NButton" /> so it picks up the game's hover and click sounds.
/// </para>
/// </summary>
public partial class NSpectateToggleButton : Control
{
    private const float buttonSize = 34f;

    /// <summary>Clearance between the end of the panel's own content and the eye.</summary>
    private const float panelGap = 8f;

    private static readonly Color idleColor = new(0.78f, 0.74f, 0.68f, 0.85f);
    private static readonly Color activeColor = new(0.90f, 0.25f, 0.25f);

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
    private NMultiplayerPlayerState? _panel;
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
    /// Parks the eye just past the right edge of the player's row, vertically centred on it.
    /// <para>
    /// Every other spot in the row is taken: the character icon is covered edge to edge by the turn-end, disconnected
    /// and network-problem indicators, the name and the energy, star and card counters run to x=261, and the health
    /// bar sits under them. Putting the eye inside the row buried it under the health text. Past the end of the row
    /// there is nothing but empty screen.
    /// </para>
    /// </summary>
    public void AttachTo(NMultiplayerPlayerState panel)
    {
        _panel = panel;
        panel.AddChildSafely(this);
        Reposition();
    }

    /// <summary>
    /// The top info container overhangs the panel's own width, so clear whichever reaches further right. Recomputed
    /// each frame rather than measured once, so it survives a relayout.
    /// </summary>
    private void Reposition()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
        {
            return;
        }
        float right = _panel.Size.X;
        Control? top = _panel._topContainer;
        if (top != null && GodotObject.IsInstanceValid(top))
        {
            right = Mathf.Max(right, top.Position.X + top.Size.X);
        }
        Position = new Vector2(right + panelGap, (_panel.Size.Y - Size.Y) * 0.5f);
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
        Reposition();
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
