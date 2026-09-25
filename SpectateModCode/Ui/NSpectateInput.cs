using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Click-to-spectate. Creature nodes have no press handler of their own, so this listens at the _Input stage and uses
/// the node the mouse is already hovering (<see cref="NCreature.IsFocused" />) rather than hit-testing by hand.
/// </summary>
public partial class NSpectateInput : Node
{
    public static NSpectateInput Create()
    {
        return new NSpectateInput { Name = "SpectateInput" };
    }

    public override void _Input(InputEvent inputEvent)
    {
        base._Input(inputEvent);
        if (!SpectateConfig.ManualSpectateOnClick)
        {
            return;
        }
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            return;
        }

        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null || room.Mode != CombatRoomMode.ActiveCombat)
        {
            return;
        }

        // Never steal a click that combat is already using: targeting, card play and hand selection all take priority.
        if (NTargetManager.Instance.IsInSelection || room.Ui.Hand.InCardPlay || room.Ui.Hand.IsInCardSelection)
        {
            return;
        }

        NCreature? clicked = room.CreatureNodes.FirstOrDefault(c => c.IsFocused && c.Entity.IsPlayer);
        if (clicked?.Entity.Player is not { } player)
        {
            return;
        }

        if (LocalContext.IsMe(player))
        {
            // Clicking yourself is the "take me back" gesture; if we aren't spectating, leave the click alone.
            if (!SpectateController.IsSpectating)
            {
                return;
            }
            SpectateController.Stop();
        }
        else if (SpectateController.CanSpectate(player))
        {
            SpectateController.Toggle(player);
        }
        else
        {
            return;
        }

        GetViewport().SetInputAsHandled();
    }
}
