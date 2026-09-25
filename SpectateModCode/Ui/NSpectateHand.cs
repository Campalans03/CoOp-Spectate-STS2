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

    private const float hoverRaise = 90f;

    private readonly Dictionary<CardModel, Control> _slots = new();
    private readonly Dictionary<CardModel, NCard> _cards = new();
    private readonly List<CardModel> _order = new();

    private NPlayerHand? _realHand;
    private CardPile? _pile;
    private CardModel? _hovered;

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
        _hovered = null;
        ClearCards();
    }

    /// <summary>Raises the card the teammate is pointing at, mirroring their hover.</summary>
    public void SetHovered(AbstractModel? model)
    {
        CardModel? card = model as CardModel;
        if (card == _hovered)
        {
            return;
        }
        _hovered = card != null && _slots.ContainsKey(card) ? card : null;
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
            _slots[card] = slot;
            _cards[card] = node;
        }

        if (_hovered != null && !_slots.ContainsKey(_hovered))
        {
            _hovered = null;
        }
        Layout();
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
            bool isHovered = visible[i] == _hovered;
            Vector2 position = HandPosHelper.GetPosition(count, i);
            slot.Scale = isHovered ? Vector2.One : scale;
            slot.Rotation = isHovered ? 0f : Mathf.DegToRad(HandPosHelper.GetAngle(count, i));
            slot.Position = position - NCard.defaultSize * 0.5f + (isHovered ? Vector2.Up * hoverRaise : Vector2.Zero);
            slot.ZIndex = isHovered ? 1 : 0;
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
