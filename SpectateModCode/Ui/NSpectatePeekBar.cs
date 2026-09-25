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
/// A column of small player boxes against the right edge of the screen, for a quick look at somebody's hand without
/// spectating them. See <see cref="NSpectatePeekSlot" /> for how a box triggers the peek.
/// </summary>
public partial class NSpectatePeekBar : VBoxContainer
{
    private const float slotGap = 8f;

    /// <summary>Space kept between the boxes and the right edge of the screen.</summary>
    private const float screenMargin = 12f;

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

        // Shown alongside the end turn button: it is on screen exactly while the combat UI is, and animates away with
        // it at the end of combat.
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
            HugRightEdge();
        }
    }

    /// <summary>Centred vertically against the right edge; placed every frame so it survives resolution changes.</summary>
    private void HugRightEdge()
    {
        Vector2 size = GetCombinedMinimumSize();
        Size = size;
        Vector2 screen = GetViewportRect().Size;
        GlobalPosition = new Vector2(screen.X - screenMargin - size.X, (screen.Y - size.Y) * 0.5f);
    }

    private static bool InActiveCombat()
    {
        return SpectateController.State != null
               && CombatManager.Instance.IsInProgress
               && NCombatRoom.Instance is { Mode: CombatRoomMode.ActiveCombat };
    }
}
