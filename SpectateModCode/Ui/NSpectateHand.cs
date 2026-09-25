using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// A read-only copy of the spectated teammate's hand, drawn where our own hand would be.
/// <para>
/// It deliberately does not reuse <see cref="NPlayerHand" />: the real hand owns card play, drag, selection and the
/// networked "I am hovering X" state, and feeding somebody else's cards through it would let us act on them. Nothing
/// here creates a card holder, so there is no path from a mirrored card to an action.
/// </para>
/// <para>
/// <b>Never write to an <see cref="NCard" /> outside what the pool resets.</b> Card nodes come from the shared
/// <c>NodePool&lt;NCard&gt;</c>, and <c>NCard.OnReturnedFromPool</c> only clears Position, Rotation, Scale, Modulate,
/// Visible, the model and the materials. Anything else we set would ride along into whatever asks for that node next
/// — the real hand, the play area, card rewards — and there is nobody to clean it up. So each card gets its own
/// wrapper slot that we own, and every presentation property (offset in the fan, angle, scale, draw order, pivot)
/// lives on the slot. The pooled node itself is only ever parented and left alone.
/// </para>
/// </summary>
public partial class NSpectateHand : Control
{
    /// <summary>The real hand fans cards out over this many slots; beyond it the game itself would throw.</summary>
    private const int maxLayoutCards = 10;

    /// <summary>
    /// Centre height of the card being read, in the hand's own space. Mirrors what NPlayerHand.RefreshLayout does to
    /// a focused holder: <c>-Hitbox.Size.Y * 0.5f + 2f</c>, with the hitbox being one card tall.
    /// </summary>
    private static readonly float focusCentreY = -(NCard.defaultSize.Y * 0.5f) + 2f;

    /// <summary>A gentler lift for the teammate's card while we are reading a different one.</summary>
    private const float secondaryRaise = 70f;

    /// <summary>How far the card next to the one being read slides away, tapering off over four slots.</summary>
    private const float neighbourPush = 100f;

    private const int neighbourPushFalloff = 4;

    /// <summary>Scale NCardPlay.CenterCard puts a card at once it is being aimed.</summary>
    private const float targetingScale = 0.75f;

    /// <summary>
    /// Smoothing for the card a teammate is holding. The same rate NHandCardHolder.AnimPosition uses, which matters
    /// more here than it does for them: peer input is only sent every 50ms at best, so the cursor we are given
    /// arrives in steps rather than as a continuous line.
    /// </summary>
    private const float playLerpSpeed = 7f;

    /// <summary>How far NPlayerHand.AnimDisable lowers the hand (its <c>_disablePosition</c>).</summary>
    private const float disabledHandDrop = 100f;

    /// <summary>What the teammate is doing with the card they picked up.</summary>
    public enum PlayVisual
    {
        None,

        /// <summary>Held on the cursor, the way an untargeted card like a block is played.</summary>
        Dragging,

        /// <summary>Parked centre-bottom while they aim, with the arrow running off it.</summary>
        Targeting
    }

    private CardModel? _playCard;
    private PlayVisual _playVisual;
    private Vector2 _playCursor;
    private CardModel? _previewCard;
    private Creature? _previewTarget;
    private Vector2 _playTargetPosition;
    private Vector2 _playTargetScale = Vector2.One;

    private readonly Dictionary<CardModel, Control> _slots = new();
    private readonly Dictionary<CardModel, NCard> _cards = new();
    private readonly Dictionary<CardModel, Control> _hitboxes = new();
    private readonly List<CardModel> _order = new();

    private Control _cardLayer = null!;
    private Control _hoverLayer = null!;
    private NPlayerHand? _realHand;
    private CardPile? _pile;
    private PlayerCombatState? _combatState;
    private bool _trackingCombat;
    private CardModel? _remoteHovered;
    private CardModel? _localHovered;

    public static NSpectateHand Create(NPlayerHand realHand)
    {
        NSpectateHand node = new()
        {
            Name = "SpectateHand",
            MouseFilter = MouseFilterEnum.Ignore
        };
        node._realHand = realHand;

        node._cardLayer = new Control { Name = "Cards", MouseFilter = MouseFilterEnum.Ignore };
        node.AddChildSafely(node._cardLayer);

        // Hover targets live in their own layer, added last so they win the mouse pick over the cards they stand in
        // for. They are plain Controls that draw nothing, but they must stay Visible: an invisible Control gets no
        // mouse events at all.
        node._hoverLayer = new Control { Name = "HoverTargets", MouseFilter = MouseFilterEnum.Ignore };
        node.AddChildSafely(node._hoverLayer);
        return node;
    }

    public void Bind(Player player)
    {
        Unbind();
        _pile = player.PlayerCombatState?.Hand;
        if (_pile == null)
        {
            return;
        }
        _pile.ContentsChanged += OnPileChanged;
        _pile.CardAddFinished += OnPileChanged;
        _pile.CardRemoveFinished += OnPileChanged;

        // A card's cost colour (red when it can't be afforded) is worked out once per UpdateVisuals, so it goes stale
        // as soon as the teammate spends energy. The real hand redraws its cards on every combat state change; do the
        // same, plus the teammate's own energy and stars, which is what affordability actually hangs on.
        _combatState = player.PlayerCombatState!;
        _combatState.EnergyChanged += OnResourceChanged;
        _combatState.StarsChanged += OnResourceChanged;
        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        _trackingCombat = true;
        Rebuild();
    }

    public void Unbind()
    {
        if (_pile != null)
        {
            _pile.ContentsChanged -= OnPileChanged;
            _pile.CardAddFinished -= OnPileChanged;
            _pile.CardRemoveFinished -= OnPileChanged;
            _pile = null;
        }
        if (_combatState != null)
        {
            _combatState.EnergyChanged -= OnResourceChanged;
            _combatState.StarsChanged -= OnResourceChanged;
            _combatState = null;
        }
        if (_trackingCombat)
        {
            CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;
            _trackingCombat = false;
        }
        _remoteHovered = null;
        _localHovered = null;
        _playCard = null;
        _playVisual = PlayVisual.None;
        _previewCard = null;
        _previewTarget = null;
        ClearCards();
    }

    /// <summary>Raises the card the teammate is pointing at, mirroring their hover.</summary>
    public void SetRemoteHovered(AbstractModel? model)
    {
        CardModel? card = model as CardModel;
        if (card == _remoteHovered)
        {
            return;
        }
        _remoteHovered = card != null && _slots.ContainsKey(card) ? card : null;
        Layout();
    }

    /// <summary>
    /// Our own mouse hovering a mirrored card. Independent of what the teammate is doing, so you can read through
    /// their hand at your own pace instead of waiting for them to point at something.
    /// </summary>
    private void SetLocalHovered(CardModel? card)
    {
        CardModel? next = card != null && _slots.ContainsKey(card) && SpectateConfig.InspectTeammateCards
            ? card
            : null;
        if (next == _localHovered)
        {
            return;
        }
        _localHovered = next;
        Layout();
    }

    /// <summary>
    /// Mirrors a card the teammate has picked up. The game drags a card on the cursor until it reaches the play zone,
    /// then parks it centre-bottom if it still needs a creature to aim at; a card that needs no target just stays on
    /// the cursor, which is what you see when somebody plays a block.
    /// </summary>
    /// <param name="previewTarget">
    /// The creature they are aiming at, so the card can show what it would actually do to that target. A card's
    /// printed numbers are the base ones; the game recomputes them against the target, which is how six damage reads
    /// as nine on something vulnerable.
    /// </param>
    public void SetPlayState(CardModel? card, PlayVisual visual, Vector2 globalCursor, Creature? previewTarget)
    {
        CardModel? next = card != null && visual != PlayVisual.None && _slots.ContainsKey(card) ? card : null;
        _playCursor = globalCursor;
        SetPreview(next, next == null ? null : previewTarget);
        if (next == _playCard && visual == _playVisual)
        {
            // Same card in the same state: just follow the cursor, no need to lay the whole fan out again.
            ApplyPlayVisual();
            return;
        }
        _playCard = next;
        _playVisual = next == null ? PlayVisual.None : visual;
        Layout();
    }

    /// <summary>
    /// Points a mirrored card at the creature it is aimed at, which makes it redraw its numbers against that target.
    /// Safe on a pooled node: <c>OnReturnedFromPool</c> clears the preview target along with the model.
    /// </summary>
    private void SetPreview(CardModel? card, Creature? target)
    {
        if (_previewCard == card && _previewTarget == target)
        {
            return;
        }
        if (_previewCard != null && _previewCard != card
                                 && _cards.TryGetValue(_previewCard, out NCard? previous)
                                 && GodotObject.IsInstanceValid(previous))
        {
            previous.SetPreviewTarget(null);
        }
        _previewCard = card;
        _previewTarget = target;
        if (card != null && _cards.TryGetValue(card, out NCard? node) && GodotObject.IsInstanceValid(node))
        {
            node.SetPreviewTarget(target);
        }
    }

    /// <summary>Global position of a mirrored card, used as the origin of the teammate's targeting line.</summary>
    public Vector2? GetCardCenter(CardModel card)
    {
        if (!_slots.TryGetValue(card, out Control? slot) || !GodotObject.IsInstanceValid(slot))
        {
            return null;
        }
        // The slot's origin is already the card's centre, and a zero pivot means scale and rotation leave that point
        // alone, so its global position is the centre outright.
        return slot.GlobalPosition;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        // The real container is reached through a unique-name lookup and may sit at any depth, so follow it in global
        // space instead of assuming we share a parent. Doing it per frame also survives resolution changes.
        FollowRealHand();
        ProcessPlayVisual(delta);
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        Unbind();
    }

    private void OnPileChanged()
    {
        Rebuild();
    }

    private void OnCombatStateChanged(CombatState _)
    {
        RefreshCardVisuals();
    }

    private void OnResourceChanged(int oldValue, int newValue)
    {
        RefreshCardVisuals();
    }

    /// <summary>Redraws cost and playability on every mirrored card, the way NHandCardHolder.UpdateCard does.</summary>
    private void RefreshCardVisuals()
    {
        foreach (NCard node in _cards.Values)
        {
            if (GodotObject.IsInstanceValid(node) && node.IsNodeReady())
            {
                node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            }
        }
    }

    private void Rebuild()
    {
        if (_pile == null)
        {
            return;
        }

        _order.Clear();
        _order.AddRange(_pile.Cards);

        foreach (CardModel gone in _slots.Keys.Where(c => !_order.Contains(c)).ToList())
        {
            Release(gone);
        }

        foreach (CardModel card in _order)
        {
            if (_slots.ContainsKey(card))
            {
                continue;
            }
            NCard? node = NCard.Create(card);
            if (node == null)
            {
                continue;
            }

            // A holder's origin is the centre of its card, not a corner: hand_card_holder.tscn leaves pivot_offset at
            // zero and hangs a hitbox spanning -150,-211 to 150,211 off that origin, and card.tscn likewise centres
            // its art on its own origin. So the slot keeps a zero pivot and simply sits on the point the fan gives
            // us, with the card at zero inside it.
            Control slot = new()
            {
                Name = $"SpectateSlot-{card.Id}",
                MouseFilter = MouseFilterEnum.Ignore,
                PivotOffset = Vector2.Zero
            };
            _cardLayer.AddChildSafely(slot);
            slot.AddChildSafely(node);
            if (node.IsNodeReady())
            {
                node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            }

            // The hover target is a sibling of the card rather than a child of it, and it stays at the card's resting
            // place in the fan. Riding along with the card meant that lifting it on hover pulled the target out from
            // under the cursor, which fired MouseExited, dropped the card, and started over: a flicker loop that only
            // settled when the teammate's own hover had already lifted the card most of the way.
            CardModel captured = card;
            Control hitbox = new()
            {
                Name = $"SpectateHover-{card.Id}",
                MouseFilter = MouseFilterEnum.Stop,
                Size = NCard.defaultSize,
                PivotOffset = NCard.defaultSize * 0.5f
            };
            _hoverLayer.AddChildSafely(hitbox);
            hitbox.MouseEntered += () => SetLocalHovered(captured);
            hitbox.MouseExited += () => OnHitboxExited(captured);

            _slots[card] = slot;
            _cards[card] = node;
            _hitboxes[card] = hitbox;
        }

        if (_remoteHovered != null && !_slots.ContainsKey(_remoteHovered))
        {
            _remoteHovered = null;
        }
        if (_localHovered != null && !_slots.ContainsKey(_localHovered))
        {
            _localHovered = null;
        }
        Layout();
    }

    private void OnHitboxExited(CardModel card)
    {
        // Only clear if we are still the card under the cursor; moving between two overlapping cards fires the new
        // card's Entered before the old card's Exited.
        if (_localHovered == card)
        {
            SetLocalHovered(null);
        }
    }

    private void FollowRealHand()
    {
        if (_realHand == null || !GodotObject.IsInstanceValid(_realHand)
                              || !GodotObject.IsInstanceValid(_realHand.CardHolderContainer))
        {
            return;
        }

        // Once our own turn is over (ended, or dead) NPlayerHand.AnimDisable slides the whole hand down by
        // _disablePosition and greys it out. That is about our cards being unplayable, not the teammate's, and since we
        // hang off the hand we would sink with it and lose the bottom of the fan off screen. Undo just that much of the
        // drop; anything past it is AnimOut at the end of combat, which we still want to follow off screen.
        float drop = Mathf.Clamp(_realHand.Position.Y, 0f, disabledHandDrop);
        GlobalPosition = _realHand.CardHolderContainer.GlobalPosition + Vector2.Up * drop;

        // Modulate multiplies down the tree, so the inverse of the hand's tint cancels the grey on our cards.
        Color tint = _realHand.Modulate;
        Modulate = new Color(
            tint.R > 0f ? 1f / tint.R : 1f,
            tint.G > 0f ? 1f / tint.G : 1f,
            tint.B > 0f ? 1f / tint.B : 1f);
    }

    private void Layout()
    {
        FollowRealHand();

        List<CardModel> visible = _order.Where(_slots.ContainsKey).ToList();
        int count = Math.Min(visible.Count, maxLayoutCards);
        if (count <= 0)
        {
            return;
        }

        Vector2 scale = HandPosHelper.GetScale(count);
        // One card at a time is read in full, the way the game only ever focuses one holder. Ours wins over the
        // teammate's, so pointing at a card always reads it whatever they are doing.
        CardModel? focus = _localHovered ?? _remoteHovered;
        int focusIndex = focus == null ? -1 : visible.IndexOf(focus);

        for (int i = 0; i < visible.Count; i++)
        {
            Control slot = _slots[visible[i]];
            if (!GodotObject.IsInstanceValid(slot))
            {
                continue;
            }
            _hitboxes.TryGetValue(visible[i], out Control? hitbox);
            if (i >= count)
            {
                // More cards than the fan supports; the real hand caps at 10 too, so just hide the overflow.
                slot.Visible = false;
                if (hitbox != null)
                {
                    hitbox.Visible = false;
                }
                continue;
            }

            slot.Visible = true;
            bool isPlayed = visible[i] == _playCard;
            bool isFocus = !isPlayed && visible[i] == focus;
            // The teammate's card still peeks up while we are reading a different one, so we don't lose track of it.
            bool isSecondary = !isPlayed && !isFocus && visible[i] == _remoteHovered;

            Vector2 position = HandPosHelper.GetPosition(count, i);
            float angle = Mathf.DegToRad(HandPosHelper.GetAngle(count, i));
            if (focusIndex > -1)
            {
                // The rest of the fan slides away from the card being read, exactly as RefreshLayout does it. The
                // focused card's own push is zero, so the card you are pointing at never moves under your cursor.
                float push = Mathf.Lerp(neighbourPush, 0f,
                    Mathf.Min(1f, (float)Mathf.Abs(focusIndex - i) / neighbourPushFalloff));
                position += Vector2.Left * Mathf.Sign(focusIndex - i) * push;
            }

            if (isPlayed)
            {
                // Driven by the cursor or parked centre-bottom; either way it has left the fan.
                slot.Rotation = 0f;
                slot.ZIndex = 3;
                ApplyPlayVisual(slot);
            }
            else if (isFocus)
            {
                // An absolute height, not a lift: the fan puts the middle card higher than the edges, so adding a
                // fixed amount left the outer cards short. This is the game's own formula for a focused holder
                // (-Hitbox.Size.Y * 0.5f + 2f), which lands the card's bottom edge 2px past the hand's baseline and
                // so shows the whole card whichever slot it came from.
                slot.Scale = Vector2.One;
                slot.Rotation = 0f;
                slot.Position = new Vector2(position.X, focusCentreY);
                slot.ZIndex = 2;
            }
            else
            {
                slot.Scale = isSecondary ? Vector2.One : scale;
                slot.Rotation = isSecondary ? 0f : angle;
                slot.Position = position + (isSecondary ? Vector2.Up * secondaryRaise : Vector2.Zero);
                slot.ZIndex = isSecondary ? 1 : 0;
            }

            if (hitbox != null)
            {
                // Always the resting rect, never the lifted one, so hovering can't move its own target. A Control's
                // Position is its top-left corner, and the fan gives us a centre, so step back half a card - the same
                // thing the real holder's hitbox does with its -150,-211 offsets. The push is included so targets
                // track the cards they stand for; the focused card's push is zero, so it stays put under the cursor.
                hitbox.Visible = !isPlayed;
                hitbox.Position = position - NCard.defaultSize * 0.5f;
                hitbox.Scale = scale;
                hitbox.Rotation = angle;
            }
        }
    }

    private void ApplyPlayVisual()
    {
        if (_playCard != null && _slots.TryGetValue(_playCard, out Control? slot) && GodotObject.IsInstanceValid(slot))
        {
            ApplyPlayVisual(slot);
        }
    }

    /// <summary>
    /// Records where the held card is heading. Nothing is moved here: <see cref="ProcessPlayVisual" /> eases it there
    /// a frame at a time, which both smooths the stepped peer cursor and gives the card the same pick-up glide the
    /// game gives its own.
    /// </summary>
    private void ApplyPlayVisual(Control slot)
    {
        if (_playVisual == PlayVisual.Targeting)
        {
            // NCardPlay.CenterCard: centred horizontally, sitting half a scaled card up from the bottom.
            Vector2 viewport = GetViewportRect().Size;
            _playTargetScale = Vector2.One * targetingScale;
            _playTargetPosition = new Vector2(
                viewport.X * 0.5f,
                viewport.Y - NCard.defaultSize.Y * targetingScale * 0.5f);
            return;
        }
        _playTargetScale = Vector2.One;
        _playTargetPosition = _playCursor;
    }

    private void ProcessPlayVisual(double delta)
    {
        if (_playCard == null
            || !_slots.TryGetValue(_playCard, out Control? slot)
            || !GodotObject.IsInstanceValid(slot))
        {
            return;
        }
        float weight = Mathf.Min(1f, (float)delta * playLerpSpeed);
        slot.GlobalPosition = slot.GlobalPosition.Lerp(_playTargetPosition, weight);
        slot.Scale = slot.Scale.Lerp(_playTargetScale, weight);
    }

    private void ClearCards()
    {
        foreach (CardModel card in _slots.Keys.ToList())
        {
            Release(card);
        }
        _slots.Clear();
        _cards.Clear();
        _hitboxes.Clear();
        _order.Clear();
    }

    private void Release(CardModel card)
    {
        // Return the card to the pool first: QueueFreeSafely detaches an IPoolable from its parent immediately and
        // defers only the pool hand-back. Freeing the slot first would take the card down with it and lose the node.
        if (_cards.Remove(card, out NCard? node) && GodotObject.IsInstanceValid(node))
        {
            node.QueueFreeSafely();
        }
        if (_slots.Remove(card, out Control? slot) && GodotObject.IsInstanceValid(slot))
        {
            slot.QueueFreeSafely();
        }
        if (_hitboxes.Remove(card, out Control? hitbox) && GodotObject.IsInstanceValid(hitbox))
        {
            hitbox.QueueFreeSafely();
        }
        if (_localHovered == card)
        {
            _localHovered = null;
        }
        if (_previewCard == card)
        {
            _previewCard = null;
            _previewTarget = null;
        }
    }
}
