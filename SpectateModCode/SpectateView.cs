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

    /// <summary>Size of our own energy orb, relative to the teammate's, while it sits beside it.</summary>
    private const float ownEnergyScale = 0.55f;

    /// <summary>How far up and left of the teammate's orb ours sits, as a fraction of a full-size orb.</summary>
    private const float ownEnergyOffset = 0.42f;

    private static readonly Color ownEnergyTint = new(0.65f, 0.65f, 0.65f);

    private static NCombatUi? _ui;
    private static NSpectateHand? _hand;
    private static NSpectateOverlay? _overlay;
    private static NSpectateInput? _input;
    private static NSpectatePeekBar? _peekBar;

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

        // Added after the overlay so it sits above it; the overlay ignores the mouse, but this has to receive it.
        _peekBar = NSpectatePeekBar.Create(ui, state);
        ui.AddChildSafely(_peekBar);

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
        _peekBar?.QueueFreeSafely();
        _hand = null;
        _overlay = null;
        _input = null;
        _peekBar = null;
        PeekPlayer = null;
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
            ApplyHand();
            ApplyCombatUi(target);
            _overlay?.Refresh(target);
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

    /// <summary>The player whose hand a peek box is showing right now, or null when no box is.</summary>
    public static Player? PeekPlayer { get; private set; }

    /// <summary>
    /// Whose hand is on screen. A peek wins over spectating, and either way it is the hand alone: the piles, energy
    /// and the rest of the combat UI keep following <see cref="SpectateController.Target" />. Null means our own.
    /// </summary>
    public static Player? HandPlayer
    {
        get
        {
            Player? player = PeekPlayer ?? (SpectateConfig.ReplaceHand ? SpectateController.Target : null);
            return player == null || player == _localPlayer ? null : player;
        }
    }

    /// <summary>
    /// Whose card play the overlay mirrors: the arrow, the reticle and the held card. Follows the peek too, so a
    /// quick look shows what that player is doing with their cards and not only what they hold.
    /// </summary>
    public static Player? VisualPlayer
    {
        get
        {
            Player? player = PeekPlayer ?? SpectateController.Target;
            return player == _localPlayer ? null : player;
        }
    }

    /// <summary>
    /// Can a peek box show this player's hand right now? Our own box only means something while another hand is
    /// covering ours. Never in the middle of playing or choosing a card: that needs our real hand on screen.
    /// </summary>
    public static bool CanPeek(Player player)
    {
        if (_ui == null || _hand == null || _ui.Hand.InCardPlay || _ui.Hand.IsInCardSelection)
        {
            return false;
        }
        if (player == _localPlayer)
        {
            return SpectateConfig.ReplaceHand && SpectateController.IsSpectating;
        }
        return SpectateController.CanSpectate(player);
    }

    public static void SetPeek(Player? player)
    {
        if (player == PeekPlayer || _ui == null)
        {
            return;
        }
        PeekPlayer = player;
        try
        {
            ApplyHand();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to peek at a hand: {e}");
            PeekPlayer = null;
        }
    }

    private static void ApplyHand()
    {
        if (_ui == null || _hand == null)
        {
            return;
        }

        Player? shown = HandPlayer;
        bool replace = shown != null;
        // Hiding only the holder container leaves the hand node itself alone, so the game's own hand bookkeeping
        // keeps running untouched while we are away.
        _ui.Hand.CardHolderContainer.Visible = !replace;
        _hand.Visible = replace;

        if (replace)
        {
            _hand.Bind(shown!);
            OnHoverChanged(shown!.NetId);
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

        // Our own orb may be sitting beside the teammate's as the small one; take it down before anything else, so it
        // is off-tree like it expects to be when it goes back to being the main orb.
        HideOwnEnergy();

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

        if (next != _localEnergyCounter)
        {
            ShowOwnEnergy();
        }
    }

    /// <summary>
    /// Puts our own orb back in the container, shrunk and to the right of the teammate's. It is the very node we keep
    /// off-tree while spectating, so entering the tree resubscribes it to our energy and it stays live on its own.
    /// </summary>
    private static void ShowOwnEnergy()
    {
        NEnergyCounter? own = _localEnergyCounter;
        if (!SpectateConfig.ShowOwnEnergy || _ui == null || own == null || !GodotObject.IsInstanceValid(own)
            || own.IsInsideTree())
        {
            return;
        }

        _ui.EnergyCounterContainer.AddChildSafely(own);
        // Tucked against the big orb's top-left, overlapping it a little so the two read as one group. It scales from
        // its own top-left corner, so the offset is in fractions of the full-size orb.
        own.Scale = Vector2.One * ownEnergyScale;
        own.Position = -own.Size * ownEnergyOffset;
        // Dimmed so it reads as secondary to the orb of the player being watched. RefreshLabel only tints the inner
        // layers, never the root, so this stays put until we clear it.
        own.Modulate = ownEnergyTint;
    }

    private static void HideOwnEnergy()
    {
        NEnergyCounter? own = _localEnergyCounter;
        if (_ui == null || own == null || !GodotObject.IsInstanceValid(own) || own == _ui._energyCounter
            || !own.IsInsideTree())
        {
            return;
        }

        own.GetParent()?.RemoveChildSafely(own);
        own.Scale = Vector2.One;
        own.Position = Vector2.Zero;
        own.Modulate = Colors.White;
    }

    private static void OnHoverChanged(ulong playerId)
    {
        Player? shown = HandPlayer;
        if (_hand == null || shown == null || shown.NetId != playerId)
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
