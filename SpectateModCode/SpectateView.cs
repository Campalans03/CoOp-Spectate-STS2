using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Ui;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Applies the spectate state to the combat view: swaps the hand, re-points the combat UI at the watched player and
/// drives the overlay. Every swap is reversible, and the whole apply step is guarded so a failure downgrades the
/// feature instead of breaking combat.
/// </summary>
public static class SpectateView
{
    /// <summary>Energy orb offset used by the game when the star counter is always shown.</summary>
    private static readonly Vector2 starCounterEnergyPosition = new(100f, 806f);

    private static NCombatUi? _ui;
    private static NSpectateHand? _hand;
    private static NSpectateOverlay? _overlay;
    private static NSpectateInput? _input;

    private static Player? _localPlayer;
    private static NEnergyCounter? _localEnergyCounter;
    private static Vector2 _energyContainerHomePosition;
    private static bool _uiSwapped;

    public static void Install(NCombatUi ui, CombatState state)
    {
        Uninstall();
        if (state.Players.Count <= 1)
        {
            return;
        }

        _ui = ui;
        _localPlayer = SpectateController.Me;
        _localEnergyCounter = ui._energyCounter;
        _energyContainerHomePosition = ui.EnergyCounterContainer.Position;

        _hand = NSpectateHand.Create(ui.Hand);
        _hand.Visible = false;
        ui.Hand.AddChildSafely(_hand);

        _overlay = NSpectateOverlay.Create(_hand);
        ui.AddChildSafely(_overlay);
        _overlay.MoveToFrontSafely();
        _overlay.Refresh(null);

        _input = NSpectateInput.Create();
        ui.AddChildSafely(_input);

        SpectateController.TargetChanged += OnTargetChanged;
        RunManager.Instance.HoveredModelTracker.HoverChanged += OnHoverChanged;
    }

    public static void Uninstall()
    {
        if (_ui == null)
        {
            return;
        }

        SpectateController.TargetChanged -= OnTargetChanged;
        try
        {
            RunManager.Instance.HoveredModelTracker.HoverChanged -= OnHoverChanged;
        }
        catch (Exception)
        {
            // RunManager can already be torn down on some exit paths; then there is nothing to unsubscribe from.
        }

        RestoreCombatUi();

        _hand?.QueueFreeSafely();
        _overlay?.QueueFreeSafely();
        _input?.QueueFreeSafely();
        _hand = null;
        _overlay = null;
        _input = null;
        _ui = null;
        _localPlayer = null;
        _localEnergyCounter = null;
        _uiSwapped = false;
    }

    private static void OnTargetChanged(Player? target)
    {
        if (_ui == null)
        {
            return;
        }

        try
        {
            ApplyHand(target);
            ApplyCombatUi(target);
            _overlay?.Refresh(target);
            if (target != null)
            {
                OnHoverChanged(target.NetId);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to apply spectate view: {e}");
            try
            {
                SpectateController.Stop();
            }
            catch (Exception)
            {
                // Already unwinding; don't mask the original failure.
            }
        }
    }

    private static void ApplyHand(Player? target)
    {
        if (_ui == null || _hand == null)
        {
            return;
        }

        bool replace = target != null && SpectateConfig.ReplaceHand;
        // Hiding only the holder container leaves the hand node itself alone, so the game's own hand bookkeeping
        // keeps running untouched while we are away.
        _ui.Hand.CardHolderContainer.Visible = !replace;
        _hand.Visible = replace;

        if (replace)
        {
            _hand.Bind(target!);
        }
        else
        {
            _hand.Unbind();
            // NPlayerHand.RefreshLayout skips holders that aren't visible in the tree, so anything drawn or discarded
            // while we were away never got re-fanned. Lay our own hand out again now that it is back on screen.
            _ui.Hand.ForceRefreshCardIndices();
        }
    }

    private static void ApplyCombatUi(Player? target)
    {
        if (_ui == null || _localPlayer == null)
        {
            return;
        }

        if (target != null && SpectateConfig.ReplaceCombatUi)
        {
            BindCombatUi(target);
            _uiSwapped = true;
        }
        else
        {
            RestoreCombatUi();
        }
    }

    private static void RestoreCombatUi()
    {
        if (!_uiSwapped || _localPlayer == null)
        {
            return;
        }
        try
        {
            BindCombatUi(_localPlayer);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to restore combat UI: {e}");
        }
        _uiSwapped = false;
    }

    /// <summary>Re-points the draw/discard/exhaust buttons, energy orb and star counter at <paramref name="player" />.</summary>
    private static void BindCombatUi(Player player)
    {
        if (_ui == null)
        {
            return;
        }

        RebindPile(_ui.DrawPile, player);
        RebindPile(_ui.DiscardPile, player);
        RebindPile(_ui.ExhaustPile, player);
        RebindStarCounter(player);
        RebindEnergyCounter(player);
    }

    /// <summary>
    /// <see cref="NCombatCardPile.Initialize" /> subscribes without unsubscribing first, so drop the previous pile's
    /// handlers here or the button keeps counting the other player's cards too.
    /// <para>
    /// AddCard is protected and virtual, so Publicizer leaves it alone and we rebuild the handler by reflection.
    /// A delegate built this way has the same target and method as the one the game subscribed, so -= removes it.
    /// </para>
    /// </summary>
    private static void RebindPile(NCombatCardPile pile, Player player)
    {
        if (pile._pile != null)
        {
            pile._pile.CardAddFinished -= AddCardHandler(pile);
            pile._pile.CardRemoveFinished -= pile.RemoveCard;
            pile._pile = null;
        }
        pile.Initialize(player);
    }

    private static Action AddCardHandler(NCombatCardPile pile)
    {
        return (Action)Delegate.CreateDelegate(
            typeof(Action), pile, AccessTools.Method(typeof(NCombatCardPile), "AddCard"));
    }

    private static void RebindStarCounter(Player player)
    {
        NStarCounter counter = _ui!._starCounter;
        if (counter._player != null && counter._isListeningToCombatState)
        {
            counter._player.PlayerCombatState!.StarsChanged -= counter.OnStarsChanged;
            counter._isListeningToCombatState = false;
        }
        counter.Initialize(player);
    }

    /// <summary>
    /// The energy orb art is per character, so watching a different character means a different node. Removing the old
    /// one from the tree runs its _ExitTree and unsubscribes it; adding the new one subscribes it. The local player's
    /// counter is kept alive off-tree so it can go straight back when spectating ends.
    /// </summary>
    private static void RebindEnergyCounter(Player player)
    {
        NCombatUi ui = _ui!;
        NEnergyCounter? current = ui._energyCounter;
        if (current != null && current._player == player)
        {
            return;
        }

        NStarCounter star = ui._starCounter;
        bool starWasChild = current != null && GodotObject.IsInstanceValid(star) && current.IsAncestorOf(star);
        if (starWasChild)
        {
            star.Reparent(ui.EnergyCounterContainer);
        }

        if (current != null)
        {
            current.GetParent()?.RemoveChildSafely(current);
            if (current != _localEnergyCounter)
            {
                current.QueueFreeSafely();
            }
        }

        NEnergyCounter? next = _localEnergyCounter != null && _localEnergyCounter._player == player
            ? _localEnergyCounter
            : NEnergyCounter.Create(player);
        if (next == null)
        {
            return;
        }

        ui.EnergyCounterContainer.AddChildSafely(next);
        ui._energyCounter = next;
        if (starWasChild)
        {
            star.Reparent(next);
        }

        ui.EnergyCounterContainer.SetPosition(
            player.Character.ShouldAlwaysShowStarCounter ? starCounterEnergyPosition : _energyContainerHomePosition,
            keepOffsets: true);
    }

    private static void OnHoverChanged(ulong playerId)
    {
        Player? target = SpectateController.Target;
        if (_hand == null || target == null || target.NetId != playerId)
        {
            return;
        }
        if (!SpectateConfig.MirrorHover)
        {
            _hand.SetRemoteHovered(null);
            return;
        }
        AbstractModel? hovered = RunManager.Instance.HoveredModelTracker.GetHoveredModel(playerId);
        _hand.SetRemoteHovered(hovered);
    }
}
