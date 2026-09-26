using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// The spectate hotkeys, for the whole run: one per player slot and one to stop. Listens at the _Input stage and takes
/// the key it uses, because the game reads its own shortcuts from the raw key later on (_UnhandledKeyInput) and ignores
/// modifiers there, so Ctrl+1 would otherwise also pick the first card in hand.
/// </summary>
public partial class NSpectateHotkeys : Node
{
    private IRunState _run = null!;

    public static NSpectateHotkeys Create(IRunState run)
    {
        return new NSpectateHotkeys { Name = "SpectateHotkeys", _run = run };
    }

    public override void _Input(InputEvent inputEvent)
    {
        base._Input(inputEvent);
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key || !SpectateConfig.EnableHotkeys
            || !SpectateController.IsActive || !ModifierMatches(key) || IsTyping())
        {
            return;
        }

        try
        {
            if (Handle(key.Keycode))
            {
                GetViewport().SetInputAsHandled();
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Spectate hotkey failed: {e}");
        }
    }

    /// <summary>Returns true when the key is one of ours, whether or not it could do anything right now.</summary>
    private bool Handle(Key keycode)
    {
        if (Matches(keycode, SpectateConfig.StopSpectatingKey))
        {
            if (SpectateView.CanSwitchView)
            {
                SpectateController.Stop();
            }
            return true;
        }

        SpectateHotkey[] slotKeys =
        {
            SpectateConfig.SpectatePlayer1Key, SpectateConfig.SpectatePlayer2Key,
            SpectateConfig.SpectatePlayer3Key, SpectateConfig.SpectatePlayer4Key
        };
        int slot = Array.FindIndex(slotKeys, k => Matches(keycode, k));
        if (slot < 0)
        {
            return false;
        }

        // Same order as the player boxes on the right, so "player 2" is the second box.
        IPlayerCollection collection = _run;
        Player? player = _run.Players.OrderBy(collection.GetPlayerSlotIndex).ElementAtOrDefault(slot);
        if (player == null || !SpectateView.CanSwitchView)
        {
            return true;
        }
        if (LocalContext.IsMe(player))
        {
            SpectateController.Stop();
        }
        else
        {
            SpectateController.Start(player, autoPicked: false);
        }
        return true;
    }

    private static bool Matches(Key keycode, SpectateHotkey hotkey)
    {
        return hotkey != SpectateHotkey.None && keycode == (Key)(long)hotkey;
    }

    /// <summary>The chosen modifier has to be held, and no other: Ctrl+Shift+1 is not Ctrl+1.</summary>
    private static bool ModifierMatches(InputEventKey key)
    {
        bool ctrl = key.CtrlPressed || key.MetaPressed;
        return SpectateConfig.HotkeyModifier switch
        {
            SpectateHotkeyModifier.None => !ctrl && !key.ShiftPressed && !key.AltPressed,
            SpectateHotkeyModifier.Ctrl => ctrl && !key.ShiftPressed && !key.AltPressed,
            SpectateHotkeyModifier.Shift => key.ShiftPressed && !ctrl && !key.AltPressed,
            SpectateHotkeyModifier.Alt => key.AltPressed && !ctrl && !key.ShiftPressed,
            _ => false
        };
    }

    /// <summary>Keys typed into a text box (a seed, a name, the dev console) are text, not hotkeys.</summary>
    private bool IsTyping()
    {
        if (NDevConsole.Instance is { Visible: true } || !NGame.IsGameFocusedWindow())
        {
            return true;
        }
        Control? focus = GetViewport().GuiGetFocusOwner();
        return focus is LineEdit or TextEdit;
    }
}
