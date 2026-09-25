using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Mouse input during combat. Left click on a player spectates them. While spectating, holding the right button draws
/// on the teammate's screen, the same button the map draws with. Creature nodes have no press handler of their own, so
/// this listens at the _Input stage and uses the node the mouse is already hovering
/// (<see cref="NCreature.IsFocused" />) rather than hit-testing by hand.
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
                StopDrawing();
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } press:
                if (IsCombatFree())
                {
                    TryStartDrawing(press.Position);
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

    /// <summary>
    /// The press is left unhandled so nothing else listening for it misses out. Nothing on the combat screen reacts to
    /// the right button outside card play, so it can start anywhere, buttons included; pointing at End Turn is fair.
    /// </summary>
    private void TryStartDrawing(Vector2 position)
    {
        if (!SpectateConfig.DrawOnClick || _drawings == null || !GodotObject.IsInstanceValid(_drawings)
            || SpectateController.Target is not { } target)
        {
            return;
        }
        _drawings.BeginLocal(target, position);
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

    private void StopDrawing()
    {
        if (_drawings != null && GodotObject.IsInstanceValid(_drawings))
        {
            _drawings.EndLocal();
        }
    }
}
