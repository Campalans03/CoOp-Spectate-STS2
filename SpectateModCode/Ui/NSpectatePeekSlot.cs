using System;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// One player's box in the <see cref="NSpectatePeekBar" />. A click spectates that player until clicked again; our own
/// box is the way back, and a click on it stops spectating.
/// </summary>
public partial class NSpectatePeekSlot : Control
{
    public const float SlotSize = 52f;

    private const float iconInset = 5f;

    private static readonly Color backgroundColor = new(0.08f, 0.07f, 0.07f, 0.85f);
    private static readonly Color borderColor = new(0.78f, 0.74f, 0.68f, 0.55f);
    private static readonly Color hoverBorderColor = new(1f, 0.92f, 0.86f, 0.95f);
    private static readonly Color watchingBorderColor = new(0.90f, 0.25f, 0.25f);
    private static readonly Color unavailableTint = new(0.45f, 0.45f, 0.45f, 0.7f);

    private Player _player = null!;
    private Texture2D? _icon;
    private bool _hovered;
    private bool _available = true;

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
        node.MouseEntered += () => node._hovered = true;
        node.MouseExited += () => node._hovered = false;
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
        }
        // Cheap, and the look depends on the target, which changes from all over.
        QueueRedraw();
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
            MainFile.Logger.Error($"Player box click failed: {e}");
        }
    }

    public override void _Draw()
    {
        Rect2 box = new(Vector2.Zero, Size);
        bool watching = !IsMe && SpectateController.Target == _player;

        DrawRect(box, backgroundColor);
        if (_icon != null)
        {
            DrawTextureRect(_icon, box.Grow(-iconInset), tile: false);
        }

        // Red marks who we are watching; a bright outline, the box under the mouse.
        Color border = watching ? watchingBorderColor
            : _hovered && _available ? hoverBorderColor
            : borderColor;
        DrawRect(box, border, filled: false, width: watching ? 3f : 2f);

        if (IsMe)
        {
            // A small notch in the corner marks our own box, since the icons alone don't say who is who.
            DrawRect(new Rect2(Size.X - 12f, 2f, 10f, 4f), hoverBorderColor);
        }
    }
}
