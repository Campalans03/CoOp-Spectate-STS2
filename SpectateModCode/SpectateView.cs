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
/// Applies the spectate state to the combat view: swaps the hand and re-points the combat UI at the watched player.
/// Every swap is reversible, and the whole apply step is guarded so a failure downgrades the feature instead of
/// breaking combat. What outlives combat (relics, potions, the label, the peek boxes) is <see cref="NSpectateRunUi" />'s.
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
    private static NSpectateSelectionSync? _selectionSync;

    private static Player? _localPlayer;
    private static NEnergyCounter? _localEnergyCounter;
    private static Vector2 _energyContainerHomePosition;
    private static bool _uiSwapped;
    private static bool _localStarCounterVisible;

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
        _localStarCounterVisible = ui._starCounter.Visible;

        _hand = NSpectateHand.Create(ui.Hand);
        _hand.Visible = false;
        ui.Hand.AddChildSafely(_hand);

        _overlay = NSpectateOverlay.Create(_hand);
        ui.AddChildSafely(_overlay);
        _overlay.MoveToFrontSafely();

        _selectionSync = NSpectateSelectionSync.Create(ui.Hand);
        ui.AddChildSafely(_selectionSync);

        SpectateController.ViewChanged += OnViewChanged;
        RunManager.Instance.HoveredModelTracker.HoverChanged += OnHoverChanged;

        // Somebody may already be on show (a peek held across the start of combat), so apply it straight away.
        OnViewChanged(SpectateController.Viewed);
    }

    public static void Uninstall()
    {
        if (_ui == null)
        {
            return;
        }

        SpectateController.ViewChanged -= OnViewChanged;
        try
        {
            RunManager.Instance.HoveredModelTracker.HoverChanged -= OnHoverChanged;
        }
        catch (Exception)
        {
            // RunManager can already be torn down on some exit paths; then there is nothing to unsubscribe from.
        }

        // A teammate picked by hand stays picked after combat, so nothing else is going to hand our board back.
        RestoreCombatUi();
        if (GodotObject.IsInstanceValid(_ui.Hand))
        {
            _ui.Hand.CardHolderContainer.Visible = true;
        }

        _hand?.QueueFreeSafely();
        _overlay?.QueueFreeSafely();
        _selectionSync?.QueueFreeSafely();
        _hand = null;
        _overlay = null;
        _selectionSync = null;
        _ui = null;
        _localPlayer = null;
        _localEnergyCounter = null;
        _uiSwapped = false;
    }

    private static void OnViewChanged(Player? viewed)
    {
        if (_ui == null)
        {
            return;
        }

        try
        {
            ApplyHand();
            ApplyCombatUi(viewed);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to apply spectate view: {e}");
            try
            {
                SpectateController.SetPeek(null);
                SpectateController.Stop();
            }
            catch (Exception)
            {
                // Already unwinding; don't mask the original failure.
            }
        }
    }

    /// <summary>
    /// Whose hand is on screen. A peek always shows the hand, since that is what it is there for; spectating only
    /// does with <see cref="SpectateConfig.ReplaceHand" /> on. Null means our own.
    /// </summary>
    public static Player? HandPlayer
    {
        get
        {
            Player? player = SpectateController.Peek
                             ?? (SpectateConfig.ReplaceHand ? SpectateController.Target : null);
            return player == null || player == _localPlayer ? null : player;
        }
    }

    /// <summary>Whose card play the overlay mirrors: the arrow, the reticle and the held card.</summary>
    public static Player? VisualPlayer => SpectateController.Viewed;

    /// <summary>
    /// False in the middle of playing or choosing a card: that needs our real hand and board on screen, so nothing may
    /// switch the view away until it is done. Always true outside combat.
    /// </summary>
    public static bool CanSwitchView =>
        _ui == null || !GodotObject.IsInstanceValid(_ui) || !(_ui.Hand.InCardPlay || _ui.Hand.IsInCardSelection);

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

    /// <summary>
    /// NStarCounter.RefreshVisibility only ever turns the counter on (<c>Visible = Visible || ...</c>), so after
    /// watching somebody with stars it would stay up on our side for good. Start each player from hidden and let
    /// Initialize decide; for ourselves, put back whatever the game had, since it keeps the counter once we have had
    /// stars even after spending them.
    /// </summary>
    private static void RebindStarCounter(Player player)
    {
        NStarCounter counter = _ui!._starCounter;
        if (counter._player == player)
        {
            return;
        }
        if (counter._player == _localPlayer)
        {
            _localStarCounterVisible = counter.Visible;
        }
        if (counter._player != null && counter._isListeningToCombatState)
        {
            counter._player.PlayerCombatState!.StarsChanged -= counter.OnStarsChanged;
            counter._isListeningToCombatState = false;
        }
        counter.Visible = player == _localPlayer && _localStarCounterVisible;
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
