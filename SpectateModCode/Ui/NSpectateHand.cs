using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
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

    /// <summary>How far the teammate's own hovered card lifts, mirroring what they see.</summary>
    private const float remoteHoverRaise = 90f;

    /// <summary>How far a card lifts when we point at it ourselves, to read it comfortably.</summary>
    private const float localHoverRaise = 130f;

    private readonly Dictionary<CardModel, Control> _slots = new();
    private readonly Dictionary<CardModel, NCard> _cards = new();
    private readonly List<CardModel> _order = new();

    private NPlayerHand? _realHand;
    private CardPile? _pile;
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
        _remoteHovered = null;
        _localHovered = null;
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

    /// <summary>Global position of a mirrored card, used as the origin of the teammate's targeting line.</summary>
    public Vector2? GetCardCenter(CardModel card)
    {
        if (!_slots.TryGetValue(card, out Control? slot) || !GodotObject.IsInstanceValid(slot))
        {
            return null;
        }
        // Slots scale and rotate around their pivot, which sits at the middle of the unscaled rect, so the visual
        // centre is the pivot and scale doesn't come into it.
        return slot.GlobalPosition + NCard.defaultSize * 0.5f;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        // The real container is reached through a unique-name lookup and may sit at any depth, so follow it in global
        // space instead of assuming we share a parent. Doing it per frame also survives resolution changes.
        FollowRealHand();
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

            Control slot = new()
            {
                Name = $"SpectateSlot-{card.Id}",
                MouseFilter = MouseFilterEnum.Ignore,
                Size = NCard.defaultSize,
                PivotOffset = NCard.defaultSize * 0.5f
            };
            this.AddChildSafely(slot);
            slot.AddChildSafely(node);
            if (node.IsNodeReady())
            {
                node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            }

            // Our own hover target. Added after the card so it sits last in tree order and wins the mouse pick, and
            // it is our node rather than the pooled card, so nothing leaks back into the pool.
            CardModel captured = card;
            Control hitbox = new()
            {
                Name = "Hitbox",
                MouseFilter = MouseFilterEnum.Stop
            };
            hitbox.SetAnchorsPreset(LayoutPreset.FullRect);
            slot.AddChildSafely(hitbox);
            hitbox.MouseEntered += () => SetLocalHovered(captured);
            hitbox.MouseExited += () => OnHitboxExited(captured);

            _slots[card] = slot;
            _cards[card] = node;
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
        if (_realHand != null && GodotObject.IsInstanceValid(_realHand.CardHolderContainer))
        {
            GlobalPosition = _realHand.CardHolderContainer.GlobalPosition;
        }
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
        for (int i = 0; i < visible.Count; i++)
        {
            Control slot = _slots[visible[i]];
            if (!GodotObject.IsInstanceValid(slot))
            {
                continue;
            }
            if (i >= count)
            {
                // More cards than the fan supports; the real hand caps at 10 too, so just hide the overflow.
                slot.Visible = false;
                continue;
            }

            slot.Visible = true;
            // Our own hover wins over the teammate's, so pointing at a card always reads it, whatever they are doing.
            bool isLocal = visible[i] == _localHovered;
            bool isRemote = visible[i] == _remoteHovered;
            bool raised = isLocal || isRemote;
            float raise = isLocal ? localHoverRaise : remoteHoverRaise;

            Vector2 position = HandPosHelper.GetPosition(count, i);
            slot.Scale = raised ? Vector2.One : scale;
            slot.Rotation = raised ? 0f : Mathf.DegToRad(HandPosHelper.GetAngle(count, i));
            slot.Position = position - NCard.defaultSize * 0.5f + (raised ? Vector2.Up * raise : Vector2.Zero);
            slot.ZIndex = isLocal ? 2 : isRemote ? 1 : 0;
        }
    }

    private void ClearCards()
    {
        foreach (CardModel card in _slots.Keys.ToList())
        {
            Release(card);
        }
        _slots.Clear();
        _cards.Clear();
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
    }
}
