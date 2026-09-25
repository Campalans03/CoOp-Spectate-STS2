using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// One player's box in the <see cref="NSpectatePeekBar" />. Resting the mouse on it for <see cref="HoldSeconds" />
/// swaps just the hand to that player's; leaving it swaps straight back. A bar along the bottom fills while it
/// charges, so the delay reads as intended rather than as lag.
/// </summary>
public partial class NSpectatePeekSlot : Control
{
    public const float SlotSize = 52f;

    /// <summary>How long the mouse has to stay on a box before the hand switches, from the settings slider.</summary>
    private static float HoldSeconds => Mathf.Clamp((float)SpectateConfig.PeekHoverDelay, 0f, 1f);

    private const float iconInset = 5f;
    private const float chargeBarHeight = 4f;

    private static readonly Color backgroundColor = new(0.08f, 0.07f, 0.07f, 0.85f);
    private static readonly Color borderColor = new(0.78f, 0.74f, 0.68f, 0.55f);
    private static readonly Color hoverBorderColor = new(1f, 0.92f, 0.86f, 0.95f);
    private static readonly Color peekingBorderColor = new(0.90f, 0.25f, 0.25f);
    private static readonly Color chargeColor = new(1f, 0.92f, 0.86f, 0.9f);
    private static readonly Color unavailableTint = new(0.45f, 0.45f, 0.45f, 0.7f);

    private Player _player = null!;
    private Texture2D? _icon;
    private bool _hovered;
    private float _charge;
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
        node.MouseEntered += node.OnMouseEntered;
        node.MouseExited += node.OnMouseExited;
        return node;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        bool available = SpectateView.CanPeek(_player);
        if (available != _available)
        {
            _available = available;
            Modulate = available ? Colors.White : unavailableTint;
            if (!available)
            {
                Release();
            }
            QueueRedraw();
        }

        if (!_hovered || !_available || SpectateView.PeekPlayer == _player)
        {
            return;
        }
        _charge += (float)delta;
        float hold = HoldSeconds;
        if (_charge >= hold)
        {
            _charge = hold;
            SpectateView.SetPeek(_player);
        }
        QueueRedraw();
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        Release();
    }

    public override void _Draw()
    {
        Rect2 box = new(Vector2.Zero, Size);
        bool peeking = SpectateView.PeekPlayer == _player;

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

        Color border = peeking ? peekingBorderColor : _hovered && _available ? hoverBorderColor : borderColor;
        DrawRect(box, border, filled: false, width: peeking ? 3f : 2f);

        if (LocalContext.IsMe(_player))
        {
            // A small notch in the corner marks our own box, since the icons alone don't say who is who.
            DrawRect(new Rect2(Size.X - 12f, 2f, 10f, 4f), hoverBorderColor);
        }
    }

    private void OnMouseEntered()
    {
        _hovered = true;
        _charge = 0f;
        QueueRedraw();
    }

    private void OnMouseExited()
    {
        _hovered = false;
        Release();
    }

    /// <summary>Drops the charge and, if the hand is showing this player because of us, hands it back.</summary>
    private void Release()
    {
        _charge = 0f;
        if (SpectateView.PeekPlayer == _player)
        {
            SpectateView.SetPeek(null);
        }
        QueueRedraw();
    }
}
