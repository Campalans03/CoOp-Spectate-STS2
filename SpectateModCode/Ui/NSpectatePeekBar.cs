using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// A column of small player boxes against the right edge of the screen, there for the whole run. Click one to watch
/// that player, or rest the mouse on it for a look that lasts until it moves away. See <see cref="NSpectatePeekSlot" />.
/// </summary>
public partial class NSpectatePeekBar : VBoxContainer
{
    private const float slotGap = 8f;

    /// <summary>Space kept between the boxes and the right edge of the screen.</summary>
    private const float screenMargin = 12f;

    public static NSpectatePeekBar Create(IRunState run)
    {
        NSpectatePeekBar node = new()
        {
            Name = "SpectatePeekBar",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        node.AddThemeConstantOverride("separation", (int)slotGap);

        IPlayerCollection collection = run;
        foreach (Player player in run.Players.OrderBy(collection.GetPlayerSlotIndex))
        {
            node.AddChildSafely(NSpectatePeekSlot.Create(player));
        }
        return node;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        // Out of the way while a full-screen menu is up (pause, settings, the deck view): those cover the whole screen
        // and the boxes would float on top of them.
        bool show = SpectateConfig.ShowHandPeekBar
                    && SpectateController.IsActive
                    && NCapstoneContainer.Instance is not { InUse: true };
        if (show != Visible)
        {
            Visible = show;
            if (!show)
            {
                // Hiding a Control under the mouse never fires MouseExited, so let go of the peek ourselves.
                SpectateController.SetPeek(null);
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
}
