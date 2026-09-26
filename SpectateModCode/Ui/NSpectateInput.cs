using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Rooms;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// The mod's mouse input, for the whole run. While spectating, holding the right button draws on the teammate's screen
/// wherever they are, the same button the map draws with. In combat, a left click on a player also spectates them.
/// Creature nodes have no press handler of their own, so this listens at the _Input stage and uses the node the mouse
/// is already hovering (<see cref="NCreature.IsFocused" />) rather than hit-testing by hand.
/// </summary>
public partial class NSpectateInput : Node
{
    private NSpectateDrawLayer? _drawings;

    public static NSpectateInput Create(NSpectateDrawLayer? drawings)
    {
        return new NSpectateInput { Name = "SpectateInput", _drawings = drawings };
    }

    public override void _Input(InputEvent inputEvent)
    {
        base._Input(inputEvent);
        switch (inputEvent)
        {
            case InputEventMouseMotion motion:
                ContinueDrawing(motion.Position);
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false }:
                if (StopDrawing())
                {
                    GetViewport().SetInputAsHandled();
                }
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } press:
                // Taken once a line starts, press and release both: out of combat the right button also previews cards,
                // and a line drawn across one shouldn't end by opening it.
                if (CanStartDrawing() && TryStartDrawing(press.Position))
                {
                    GetViewport().SetInputAsHandled();
                }
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                if (SpectateConfig.ManualSpectateOnClick && IsCombatFree() && TryToggleSpectate(NCombatRoom.Instance!))
                {
                    GetViewport().SetInputAsHandled();
                }
                return;
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        StopDrawing();
    }

    /// <summary>
    /// Never take a click that combat is already using: targeting, card play and hand selection all come first. That
    /// covers the right button too, which is how the game cancels a card or a target.
    /// </summary>
    private static bool IsCombatFree()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        return room != null && room.Mode == CombatRoomMode.ActiveCombat
                            && !NTargetManager.Instance.IsInSelection
                            && !room.Ui.Hand.InCardPlay
                            && !room.Ui.Hand.IsInCardSelection;
    }

    /// <summary>
    /// In combat, only while combat isn't using the button itself. Anywhere else, only without a menu up: those cover
    /// the screen being drawn on.
    /// </summary>
    private static bool CanStartDrawing()
    {
        if (NCombatRoom.Instance is { Mode: CombatRoomMode.ActiveCombat })
        {
            return IsCombatFree();
        }
        return NCapstoneContainer.Instance is not { InUse: true };
    }

    private static bool TryToggleSpectate(NCombatRoom room)
    {
        NCreature? clicked = room.CreatureNodes.FirstOrDefault(c => c.IsFocused && c.Entity.IsPlayer);
        if (clicked?.Entity.Player is not { } player)
        {
            return false;
        }

        if (LocalContext.IsMe(player))
        {
            // Clicking yourself is the "take me back" gesture; if we aren't spectating, leave the click alone.
            if (!SpectateController.IsSpectating)
            {
                return false;
            }
            SpectateController.Stop();
            return true;
        }
        if (SpectateController.CanSpectate(player))
        {
            SpectateController.Toggle(player);
            return true;
        }
        return false;
    }

    /// <summary>A line can start anywhere, buttons included; pointing at End Turn or at a shop item is fair.</summary>
    private bool TryStartDrawing(Vector2 position)
    {
        if (!SpectateConfig.DrawOnClick || _drawings == null || !GodotObject.IsInstanceValid(_drawings)
            || SpectateController.Target is not { } target)
        {
            return false;
        }
        _drawings.BeginLocal(target, position);
        return true;
    }

    private void ContinueDrawing(Vector2 position)
    {
        if (_drawings == null || !GodotObject.IsInstanceValid(_drawings) || !_drawings.IsLocalDrawing)
        {
            return;
        }
        // Stopped watching mid-line (their turn ended, we switched): the line belongs to the old screen, so end it.
        if (!SpectateController.IsSpectating)
        {
            _drawings.EndLocal();
            return;
        }
        _drawings.ContinueLocal(position);
    }

    /// <returns>Whether a line was in progress.</returns>
    private bool StopDrawing()
    {
        if (_drawings == null || !GodotObject.IsInstanceValid(_drawings) || !_drawings.IsLocalDrawing)
        {
            return false;
        }
        _drawings.EndLocal();
        return true;
    }
}
