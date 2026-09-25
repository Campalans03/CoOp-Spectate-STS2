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
/// It deliberately does not reuse <see cref="NPlayerHand"/>: the real hand owns card play, drag, selection and the
/// networked "I am hovering X" state, and feeding somebody else's cards through it would let us act on them. These are
/// plain <see cref="NCard"/> nodes with mouse input disabled, so nothing here can ever enqueue an action.
/// </para>
/// </summary>
public partial class NSpectateHand : Control
{
    /// <summary>The real hand fans cards out over this many slots; beyond it the game itself would throw.</summary>
    private const int maxLayoutCards = 10;

    private const float hoverRaise = 90f;

    private readonly Dictionary<CardModel, NCard> _cards = new();
    private readonly List<CardModel> _order = new();

    private NPlayerHand? _realHand;
    private Player? _player;
    private CardPile? _pile;
    private CardModel? _hovered;

    public IReadOnlyDictionary<CardModel, NCard> Cards => _cards;

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
        _player = player;
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
        _player = null;
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
        _hovered = card != null && _cards.ContainsKey(card) ? card : null;
        Layout();
    }

    /// <summary>Global position of a mirrored card, used as the origin of the teammate's targeting line.</summary>
    public Vector2? GetCardCenter(CardModel card)
    {
        if (!_cards.TryGetValue(card, out NCard? node) || !GodotObject.IsInstanceValid(node))
        {
            return null;
        }
        // Cards scale and rotate around their pivot, which sits at the middle of the unscaled rect, so the visual
        // centre is the pivot and scale doesn't come into it.
        return node.GlobalPosition + NCard.defaultSize * 0.5f;
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

        foreach (CardModel gone in _cards.Keys.Where(c => !_order.Contains(c)).ToList())
        {
            Release(gone);
        }

        foreach (CardModel card in _order)
        {
            if (_cards.ContainsKey(card))
            {
                continue;
            }
            NCard? node = NCard.Create(card);
            if (node == null)
            {
                continue;
            }
            node.MouseFilter = MouseFilterEnum.Ignore;
            node.PivotOffset = NCard.defaultSize * 0.5f;
            this.AddChildSafely(node);
            if (node.IsNodeReady())
            {
                node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            }
            _cards[card] = node;
        }

        if (_hovered != null && !_cards.ContainsKey(_hovered))
        {
            _hovered = null;
        }
        Layout();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        // The real container is reached through a unique-name lookup and may sit at any depth, so follow it in global
        // space instead of assuming we share a parent. Doing it per frame also survives resolution changes.
        FollowRealHand();
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

        List<CardModel> visible = _order.Where(_cards.ContainsKey).ToList();
        int count = Math.Min(visible.Count, maxLayoutCards);
        if (count <= 0)
        {
            return;
        }

        Vector2 scale = HandPosHelper.GetScale(count);
        for (int i = 0; i < visible.Count; i++)
        {
            NCard node = _cards[visible[i]];
            if (!GodotObject.IsInstanceValid(node))
            {
                continue;
            }
            if (i >= count)
            {
                // More cards than the fan supports; the real hand caps at 10 too, so just hide the overflow.
                node.Visible = false;
                continue;
            }

            node.Visible = true;
            bool isHovered = visible[i] == _hovered;
            Vector2 slot = HandPosHelper.GetPosition(count, i);
            Vector2 cardScale = isHovered ? Vector2.One : scale;
            node.Scale = cardScale;
            node.Rotation = isHovered ? 0f : Mathf.DegToRad(HandPosHelper.GetAngle(count, i));
            node.Position = slot - NCard.defaultSize * 0.5f + (isHovered ? Vector2.Up * hoverRaise : Vector2.Zero);
            node.ZIndex = isHovered ? 1 : 0;
        }
    }

    private void ClearCards()
    {
        foreach (CardModel card in _cards.Keys.ToList())
        {
            Release(card);
        }
        _cards.Clear();
        _order.Clear();
    }

    private void Release(CardModel card)
    {
        if (_cards.Remove(card, out NCard? node) && GodotObject.IsInstanceValid(node))
        {
            // QueueFreeSafely returns pooled nodes (NCard is IPoolable) instead of destroying them.
            node.QueueFreeSafely();
        }
    }
}
