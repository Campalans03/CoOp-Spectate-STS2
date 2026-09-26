using System;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// One player's box in the <see cref="NSpectatePeekBar" />.
/// <list type="bullet">
/// <item>A click spectates that player until clicked again (or until our own box is clicked).</item>
/// <item>Resting the mouse on it for <see cref="HoldSeconds" /> shows that player only while the mouse stays there; a
/// bar along the bottom fills while it charges, so the delay reads as intended rather than as lag.</item>
/// </list>
/// Our own box is the way back: click it to stop spectating, or rest on it for a quick look at our own screen.
/// </summary>
public partial class NSpectatePeekSlot : Control
{
    public const float SlotSize = 52f;

    /// <summary>How long the mouse has to stay on a box before the view switches, from the settings slider.</summary>
    private static float HoldSeconds => Mathf.Clamp((float)SpectateConfig.PeekHoverDelay, 0f, 1f);

    private const float iconInset = 5f;
    private const float chargeBarHeight = 4f;

    private static readonly Color backgroundColor = new(0.08f, 0.07f, 0.07f, 0.85f);
    private static readonly Color borderColor = new(0.78f, 0.74f, 0.68f, 0.55f);
    private static readonly Color hoverBorderColor = new(1f, 0.92f, 0.86f, 0.95f);
    private static readonly Color watchingBorderColor = new(0.90f, 0.25f, 0.25f);
    private static readonly Color chargeColor = new(1f, 0.92f, 0.86f, 0.9f);
    private static readonly Color unavailableTint = new(0.45f, 0.45f, 0.45f, 0.7f);

    private Player _player = null!;
    private Texture2D? _icon;
    private bool _hovered;
    private float _charge;
    private bool _available = true;

    /// <summary>
    /// Set by a click and held until the mouse leaves: the click already decided what is on screen, so the hover must
    /// not charge up again and cover it.
    /// </summary>
    private bool _clicked;

    public Player Player => _player;

    public static NSpectatePeekSlot Create(Player player)
    {
        NSpectatePeekSlot node = new()
        {
            Name = $"PeekSlot-{player.NetId}",
            CustomMinimumSize = new Vector2(SlotSize, SlotSize),
            Size = new Vector2(SlotSize, SlotSize),
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None
        };
        node._player = player;
        node._icon = player.Character.IconTexture;
        node.MouseEntered += node.OnMouseEntered;
        node.MouseExited += node.OnMouseExited;
        return node;
    }

    private bool IsMe => LocalContext.IsMe(_player);

    /// <summary>
    /// Our own box only means something while we are watching somebody else. Never in the middle of playing or
    /// choosing a card: that needs our real hand on screen.
    /// </summary>
    private bool IsAvailable()
    {
        if (!SpectateView.CanSwitchView)
        {
            return false;
        }
        return IsMe ? SpectateController.IsSpectating : SpectateController.CanSpectate(_player);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        bool available = IsAvailable();
        if (available != _available)
        {
            _available = available;
            Modulate = available ? Colors.White : unavailableTint;
            if (!available)
            {
                Release();
            }
        }
        // Cheap, and the look depends on the target and the peek, which change from all over.
        QueueRedraw();

        if (!_hovered || !_available || _clicked || SpectateController.Peek == _player)
        {
            return;
        }
        _charge += (float)delta;
        float hold = HoldSeconds;
        if (_charge >= hold)
        {
            _charge = hold;
            SpectateController.SetPeek(_player);
        }
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        base._GuiInput(inputEvent);
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            return;
        }
        AcceptEvent();
        if (!_available)
        {
            return;
        }

        try
        {
            _clicked = true;
            _charge = 0f;
            // The click replaces the look: whatever it leaves on screen is what stays.
            SpectateController.SetPeek(null);
            if (IsMe)
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
            MainFile.Logger.Error($"Peek box click failed: {e}");
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        Release();
    }

    public override void _Draw()
    {
        Rect2 box = new(Vector2.Zero, Size);
        bool watching = !IsMe && SpectateController.Target == _player;
        bool peeking = SpectateController.Peek == _player;

        DrawRect(box, backgroundColor);
        if (_icon != null)
        {
            DrawTextureRect(_icon, box.Grow(-iconInset), tile: false);
        }

        float hold = HoldSeconds;
        if (!peeking && _charge > 0f && hold > 0f)
        {
            float width = Size.X * Mathf.Min(1f, _charge / hold);
            DrawRect(new Rect2(0f, Size.Y - chargeBarHeight, width, chargeBarHeight), chargeColor);
        }

        // Red marks who we are watching; a bright thick outline, who we are only looking at for the moment.
        Color border = watching ? watchingBorderColor
            : peeking || (_hovered && _available) ? hoverBorderColor
            : borderColor;
        DrawRect(box, border, filled: false, width: watching || peeking ? 3f : 2f);

        if (IsMe)
        {
            // A small notch in the corner marks our own box, since the icons alone don't say who is who.
            DrawRect(new Rect2(Size.X - 12f, 2f, 10f, 4f), hoverBorderColor);
        }
    }

    private void OnMouseEntered()
    {
        _hovered = true;
        _charge = 0f;
        _clicked = false;
    }

    private void OnMouseExited()
    {
        _hovered = false;
        _clicked = false;
        Release();
    }

    /// <summary>Drops the charge and, if the view is showing this player because of us, hands it back.</summary>
    private void Release()
    {
        _charge = 0f;
        if (SpectateController.Peek == _player)
        {
            SpectateController.SetPeek(null);
        }
    }
}
