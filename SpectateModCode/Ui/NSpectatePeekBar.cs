using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// A row of small player boxes right under the end turn button, for a quick look at somebody's hand without
/// spectating them. See <see cref="NSpectatePeekSlot" /> for how a box triggers the peek.
/// </summary>
public partial class NSpectatePeekBar : HBoxContainer
{
    private const float slotGap = 8f;

    /// <summary>Space between the bottom of the end turn button and the boxes.</summary>
    private const float buttonGap = 6f;

    /// <summary>Kept clear at the bottom of the screen if the button sits too low for the boxes to fit under it.</summary>
    private const float screenMargin = 6f;

    private NEndTurnButton? _endTurnButton;

    public static NSpectatePeekBar Create(NCombatUi ui, CombatState state)
    {
        NSpectatePeekBar node = new()
        {
            Name = "SpectatePeekBar",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        node._endTurnButton = ui.EndTurnButton;
        node.AddThemeConstantOverride("separation", (int)slotGap);

        IPlayerCollection collection = state.RunState;
        foreach (Player player in state.Players.OrderBy(collection.GetPlayerSlotIndex))
        {
            node.AddChildSafely(NSpectatePeekSlot.Create(player));
        }
        return node;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        bool show = SpectateConfig.ShowHandPeekBar
                    && _endTurnButton != null
                    && GodotObject.IsInstanceValid(_endTurnButton)
                    && _endTurnButton.IsVisibleInTree()
                    && InActiveCombat();
        if (show != Visible)
        {
            Visible = show;
            if (!show && SpectateView.PeekPlayer != null)
            {
                // Hiding a Control under the mouse never fires MouseExited, so let go of the peek ourselves.
                SpectateView.SetPeek(null);
            }
        }
        if (show)
        {
            FollowEndTurnButton();
        }
    }

    /// <summary>
    /// Centred under the button, followed every frame because the button animates in and out with the turn.
    /// </summary>
    private void FollowEndTurnButton()
    {
        Rect2 button = _endTurnButton!.GetGlobalRect();
        Vector2 size = GetCombinedMinimumSize();
        Size = size;

        float bottomLimit = GetViewportRect().Size.Y - screenMargin - size.Y;
        GlobalPosition = new Vector2(
            button.Position.X + (button.Size.X - size.X) * 0.5f,
            Mathf.Min(button.End.Y + buttonGap, bottomLimit));
    }

    private static bool InActiveCombat()
    {
        return SpectateController.State != null
               && CombatManager.Instance.IsInProgress
               && NCombatRoom.Instance is { Mode: CombatRoomMode.ActiveCombat };
    }
}
