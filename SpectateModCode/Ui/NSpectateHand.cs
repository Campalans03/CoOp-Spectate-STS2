using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
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

    // How fast a hand card holder eases to its place in the fan, and how close counts as there
    // (NHandCardHolder.AnimPosition, AnimAngle and AnimScale).
    private const float moveSpeed = 7f;
    private const float positionSnap = 1f;
    private const float rotateSpeed = 10f;
    private const float angleSnap = 0.1f;
    private const float scaleSpeed = 8f;
    private const float scaleSnap = 0.002f;

    /// <summary>
    /// Frames a card that left the hand waits to be seen arriving in another pile before it is let go of anyway. A move
    /// takes it out of the hand and puts it in the next pile in one go, so one frame is already generous.
    /// </summary>
    private const int departureGraceFrames = 2;

    /// <summary>The piles a card can come into the hand from or leave it for, whose moves we follow.</summary>
    private static readonly PileType[] followedPiles = { PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Play };

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
    private readonly Dictionary<CardModel, Control> _flashes = new();
    private readonly Dictionary<CardModel, Tween> _flashTweens = new();
    private readonly Dictionary<CardModel, Action> _flashHandlers = new();
    private readonly List<CardModel> _order = new();

    /// <summary>Where each card's slot is heading in the fan; <see cref="Animate" /> eases it there.</summary>
    private readonly Dictionary<CardModel, SlotTarget> _targets = new();

    /// <summary>The pile each card last left, so a card arriving in the hand can come from the right place.</summary>
    private readonly Dictionary<CardModel, PileType> _cameFrom = new();

    /// <summary>Cards that have left the hand, on their way out.</summary>
    private readonly List<Departure> _departing = new();

    /// <summary>Played cards whose slot the game's own card has taken over (<see cref="HandOff" />).</summary>
    private readonly HashSet<CardModel> _handedOff = new();

    private readonly List<(CardPile pile, Action<CardModel> left, Action<CardModel> arrived)> _pileHandlers = new();

    /// <summary>
    /// Put cards straight where they belong instead of easing them there: a hand that has only just come on screen
    /// should look settled, not fly in from everywhere.
    /// </summary>
    private bool _snap;

    private Control _cardLayer = null!;
    private Control _hoverLayer = null!;
    private RichTextLabel _selectionHeader = null!;
    private Control? _selectionBackstop;
    private Tween? _backstopTween;
    private NPlayerHand? _realHand;
    private Player? _player;
    private CardPile? _pile;

    /// <summary>How far we are holding ourselves up against the real hand's disabled drop, see FollowRealHand.</summary>
    private float _drop;
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

        // First, so it darkens what is behind the hand and not the cards.
        node._selectionBackstop = CreateSelectionBackstop(realHand._selectModeBackstop);
        if (node._selectionBackstop != null)
        {
            node.AddChildSafely(node._selectionBackstop);
        }

        node._cardLayer = new Control { Name = "Cards", MouseFilter = MouseFilterEnum.Ignore };
        node.AddChildSafely(node._cardLayer);

        // Hover targets live in their own layer, added last so they win the mouse pick over the cards they stand in
        // for. They are plain Controls that draw nothing, but they must stay Visible: an invisible Control gets no
        // mouse events at all.
        node._hoverLayer = new Control { Name = "HoverTargets", MouseFilter = MouseFilterEnum.Ignore };
        node.AddChildSafely(node._hoverLayer);

        node._selectionHeader = CreateSelectionHeader(realHand._selectionHeader);
        node.AddChildSafely(node._selectionHeader);
        return node;
    }

    /// <summary>
    /// Our copy of the "Choose a card to discard" line the real hand shows while picking. A fresh label dressed in the
    /// real one's fonts and colours rather than a duplicate of it: that one is a game script with its own ideas about
    /// sizing, and we only need the look.
    /// </summary>
    private static RichTextLabel CreateSelectionHeader(RichTextLabel? source)
    {
        RichTextLabel header = new()
        {
            Name = "SpectateSelectionHeader",
            BbcodeEnabled = true,
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        if (source == null || !GodotObject.IsInstanceValid(source))
        {
            return header;
        }
        foreach (string font in new[] { "normal_font", "bold_font", "italics_font", "bold_italics_font" })
        {
            header.AddThemeFontOverride(font, source.GetThemeFont(font));
            header.AddThemeFontSizeOverride($"{font}_size", source.GetThemeFontSize($"{font}_size"));
        }
        foreach (string color in new[] { "default_color", "font_outline_color", "font_shadow_color" })
        {
            header.AddThemeColorOverride(color, source.GetThemeColor(color));
        }
        header.AddThemeConstantOverride("outline_size", source.GetThemeConstant("outline_size"));
        // The game's own tags ([gold], [blue], ...) are custom effects MegaRichTextLabel installs on itself in _Ready;
        // without them a plain RichTextLabel prints the tags as text. The source is ready by now, so borrow its set.
        header.CustomEffects = source.CustomEffects.Duplicate();
        return header;
    }

    /// <summary>
    /// Our copy of the shade the real hand drops over the screen while picking. A plain copy of the node (no script,
    /// no signals) that never takes the mouse: the real one swallows clicks while it is up, which is right for the
    /// player choosing and would lock out the one watching.
    /// </summary>
    private static Control? CreateSelectionBackstop(Control? source)
    {
        if (source == null || !GodotObject.IsInstanceValid(source) || source.Duplicate(0) is not Control copy)
        {
            return null;
        }
        copy.Name = "SpectateSelectionBackstop";
        copy.Visible = false;
        IgnoreMouse(copy);
        return copy;
    }

    private static void IgnoreMouse(Node node)
    {
        if (node is Control control)
        {
            control.MouseFilter = MouseFilterEnum.Ignore;
        }
        foreach (Node child in node.GetChildren())
        {
            IgnoreMouse(child);
        }
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        HandSelection.Changed += OnSelectionChanged;
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

        // A card's cost colour (red when it can't be afforded) is worked out once per UpdateVisuals, so it goes stale
        // as soon as the teammate spends energy. The real hand redraws its cards on every combat state change; do the
        // same, plus the teammate's own energy and stars, which is what affordability actually hangs on.
        _combatState = player.PlayerCombatState!;
        _combatState.EnergyChanged += OnResourceChanged;
        _combatState.StarsChanged += OnResourceChanged;
        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        _trackingCombat = true;
        FollowPiles(player);
        _snap = true;
        Rebuild();
        _snap = false;
        RefreshSelectionHeader();
    }

    /// <summary>
    /// Listens to the teammate's other piles, which is how we learn where a card that entered the hand came from and
    /// where one that left it went. A move takes the card out of one pile and puts it in the next straight away, so
    /// the two halves arrive back to back.
    /// </summary>
    private void FollowPiles(Player player)
    {
        foreach (PileType type in followedPiles)
        {
            CardPile pile = type.GetPile(player);
            Action<CardModel> left = card => Safe.Run("follow the watched player's card", () => _cameFrom[card] = type);
            Action<CardModel> arrived = card => Safe.Run("animate the watched player's card", () => OnCardArrived(type, card));
            pile.CardRemoved += left;
            pile.CardAdded += arrived;
            _pileHandlers.Add((pile, left, arrived));
        }
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
        foreach ((CardPile pile, Action<CardModel> left, Action<CardModel> arrived) in _pileHandlers)
        {
            pile.CardRemoved -= left;
            pile.CardAdded -= arrived;
        }
        _pileHandlers.Clear();
        _cameFrom.Clear();
        _remoteHovered = null;
        _localHovered = null;
        _playCard = null;
        _playVisual = PlayVisual.None;
        _previewCard = null;
        _previewTarget = null;
        _player = null;
        ClearCards();
        RefreshSelectionHeader();
    }

    private void OnSelectionChanged(ulong netId)
    {
        if (_player == null || _player.NetId != netId)
        {
            return;
        }
        RefreshSelectionHeader();
        // A choice can decide which cards glow gold, and the game redoes every card's glow as one opens and closes.
        RefreshCardVisuals();
        Layout();
    }

    private void RefreshSelectionHeader()
    {
        HandSelection.Choice? choice = _player == null ? null : HandSelection.GetChoice(_player);
        _selectionHeader.Visible = choice != null;
        if (choice != null)
        {
            _selectionHeader.Text = $"[center]{choice.Prompt}[/center]";
        }
        ShowSelectionBackstop(choice != null);
    }

    /// <summary>Fades the shade in and out over the same 0.2s NPlayerHand.SelectCards uses.</summary>
    private void ShowSelectionBackstop(bool show)
    {
        Control? backstop = _selectionBackstop;
        if (backstop == null || !GodotObject.IsInstanceValid(backstop) || backstop.Visible == show)
        {
            return;
        }
        _backstopTween?.Kill();
        _backstopTween = null;
        Color modulate = backstop.SelfModulate;
        if (!show || !IsInsideTree())
        {
            backstop.Visible = false;
            return;
        }
        modulate.A = 0f;
        backstop.SelfModulate = modulate;
        backstop.Visible = true;
        _backstopTween = CreateTween();
        _backstopTween.TweenProperty(backstop, "self_modulate:a", 1f, 0.2);
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
        Animate((float)delta);
        ProcessPlayVisual(delta);
        ProcessDepartures();
    }

    /// <summary>
    /// Eases every card toward its place in the fan, a frame at a time, at the rates a hand card holder uses. Not the
    /// held card: <see cref="ProcessPlayVisual" /> drives that one.
    /// </summary>
    private void Animate(float delta)
    {
        foreach ((CardModel card, SlotTarget target) in _targets)
        {
            if (card == _playCard || !_slots.TryGetValue(card, out Control? slot) || !GodotObject.IsInstanceValid(slot))
            {
                continue;
            }
            slot.Position = slot.Position.DistanceSquaredTo(target.Position) < positionSnap
                ? target.Position
                : slot.Position.Lerp(target.Position, Mathf.Min(1f, delta * moveSpeed));
            float degrees = Mathf.Lerp(slot.RotationDegrees, target.Degrees, Mathf.Min(1f, delta * rotateSpeed));
            slot.RotationDegrees = Mathf.Abs(degrees - target.Degrees) < angleSnap ? target.Degrees : degrees;
            Vector2 scale = slot.Scale.Lerp(target.Scale, Mathf.Min(1f, delta * scaleSpeed));
            slot.Scale = Mathf.Abs(scale.X - target.Scale.X) < scaleSnap ? target.Scale : scale;
        }
    }

    /// <summary>Sets where a card's slot is heading, or puts it there outright while <see cref="_snap" /> is on.</summary>
    private void MoveTo(CardModel card, Control slot, Vector2 position, float degrees, Vector2 scale)
    {
        _targets[card] = new SlotTarget(position, degrees, scale);
        if (_snap)
        {
            slot.Position = position;
            slot.RotationDegrees = degrees;
            slot.Scale = scale;
        }
    }

    /// <summary>
    /// A card coming into the hand starts where it came from, the way the game creates our drawn card on the draw pile
    /// and lets the hand pull it in: the button of the pile it left, or the play area. One that came from nowhere,
    /// made by a card or a power, grows out of its own place in the fan instead.
    /// </summary>
    private void PlaceArrival(CardModel card, Control slot, PileType? from)
    {
        switch (from)
        {
            case PileType.Draw or PileType.Discard or PileType.Exhaust when _player != null && CardFlights.PilesShow(_player):
                slot.GlobalPosition = CardFlights.PileCentre(from.Value);
                slot.Scale = Vector2.One;
                break;
            case PileType.Play:
                slot.GlobalPosition = CardFlights.PlayCentre();
                slot.Scale = Vector2.One * CardFlights.PlayScale;
                break;
            default:
                if (_targets.TryGetValue(card, out SlotTarget target))
                {
                    slot.Position = target.Position;
                    slot.RotationDegrees = target.Degrees;
                }
                slot.Scale = Vector2.Zero;
                break;
        }
    }

    /// <summary>
    /// A card of the teammate's landing in one of their piles. One that just left the hand flies there from its slot.
    /// One that went between two piles gets the flight the game gives ours: a card back from pile to pile for a
    /// shuffle, or off the play area once a played card is done.
    /// </summary>
    private void OnCardArrived(PileType type, CardModel card)
    {
        bool known = _cameFrom.Remove(card, out PileType from);
        Departure? departure = _departing.Find(d => d.Card == card && d.Tween == null);
        if (departure != null)
        {
            Dispatch(departure, type);
            return;
        }
        if (!known || _player == null || !IsVisibleInTree())
        {
            return;
        }
        if (from == PileType.Play)
        {
            // Into the exhaust pile the game already burns it up as it does ours.
            if ((type is PileType.Draw or PileType.Discard) && CardFlights.PilesShow(_player))
            {
                CardFlights.LeavePlay(card, type);
            }
            return;
        }
        if (from != type && (from is PileType.Draw or PileType.Discard or PileType.Exhaust)
                         && (type is PileType.Draw or PileType.Discard) && CardFlights.PilesShow(_player))
        {
            CardFlights.Shuffle(from.GetPile(_player), type.GetPile(_player), _player);
        }
    }

    /// <summary>
    /// The game's own card for one the teammate is playing, created as the card leaves their hand. It would set off
    /// from their character; start it from the card in the hand we are showing instead, and stand ours down.
    /// </summary>
    public void HandOff(CardModel card, NCard played)
    {
        if (_player == null || card.Owner != _player || !IsVisibleInTree()
            || !_slots.TryGetValue(card, out Control? slot) || !GodotObject.IsInstanceValid(slot) || !slot.Visible)
        {
            return;
        }
        played.GlobalPosition = slot.GlobalPosition;
        played.Scale = slot.Scale;
        slot.Visible = false;
        _handedOff.Add(card);
        if (_hitboxes.TryGetValue(card, out Control? hitbox) && GodotObject.IsInstanceValid(hitbox))
        {
            hitbox.Visible = false;
        }
    }

    /// <summary>
    /// Takes a card that has left the hand out of the fan, keeping its slot where it stands until we see which pile it
    /// went to. Without a flight to make (a hand not on screen, or one being put up afresh) it just goes.
    /// </summary>
    private void Depart(CardModel card)
    {
        bool handedOff = _handedOff.Contains(card);
        if (_snap || handedOff || !IsVisibleInTree()
            || !_slots.TryGetValue(card, out Control? slot) || !GodotObject.IsInstanceValid(slot) || !slot.Visible
            || !_cards.TryGetValue(card, out NCard? node) || !GodotObject.IsInstanceValid(node))
        {
            Release(card);
            return;
        }
        Forget(card);
        _slots.Remove(card);
        _cards.Remove(card);
        _departing.Add(new Departure(card, slot, node));
    }

    /// <summary>
    /// Sends a departed card on its way, as CardPileCmd.Add would send ours: flying into the discard or draw pile,
    /// burning up into the exhaust pile, or, into the play area, nowhere at all, since the game's own card has already
    /// taken its place there. Anything else (a pile without a button, a card gone from the fight) sinks and fades.
    /// </summary>
    private void Dispatch(Departure departure, PileType? to)
    {
        NCard node = departure.Node;
        Control slot = departure.Slot;
        if (!GodotObject.IsInstanceValid(node) || !GodotObject.IsInstanceValid(slot) || to == PileType.Play)
        {
            _departing.Remove(departure);
            Free(node, slot);
            return;
        }
        node.CardHighlight.AnimHide();
        if ((to is PileType.Discard or PileType.Draw) && _player != null && CardFlights.PilesShow(_player))
        {
            _departing.Remove(departure);
            node.UpdateVisuals(to.Value, CardPreviewMode.Normal);
            CardFlights.FlyToPile(node, to.Value);
            slot.QueueFreeSafely();
            return;
        }
        if (to == PileType.Exhaust)
        {
            CardFlights.Exhaust(node);
        }
        departure.Tween = CardFlights.FadeOut(slot, to != PileType.Exhaust, () =>
        {
            _departing.Remove(departure);
            Free(departure.Node, departure.Slot);
        });
    }

    /// <summary>Lets go of departed cards that never turned up in a pile we follow, going by where they are now.</summary>
    private void ProcessDepartures()
    {
        foreach (Departure departure in _departing.ToList())
        {
            if (departure.Tween == null && ++departure.Frames >= departureGraceFrames)
            {
                Safe.Run("send off the watched player's card", () => Dispatch(departure, departure.Card.Pile?.Type));
            }
        }
    }

    private void ClearDepartures()
    {
        foreach (Departure departure in _departing)
        {
            departure.Tween?.Kill();
            Free(departure.Node, departure.Slot);
        }
        _departing.Clear();
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        HandSelection.Changed -= OnSelectionChanged;
        Unbind();
    }

    // The handlers below are raised from inside the game's draws, discards and card plays; see Safe for why they are
    // walled off.

    private void OnPileChanged()
    {
        Safe.Run("mirror the watched player's hand", Rebuild);
    }

    private void OnCombatStateChanged(CombatState _)
    {
        Safe.Run("redraw the watched player's cards", RefreshCardVisuals);
    }

    private void OnResourceChanged(int oldValue, int newValue)
    {
        Safe.Run("redraw the watched player's cards", RefreshCardVisuals);
    }

    /// <summary>Redraws cost and playability on every mirrored card, the way NHandCardHolder.UpdateCard does.</summary>
    private void RefreshCardVisuals()
    {
        foreach (NCard node in _cards.Values)
        {
            UpdateCard(node);
        }
    }

    /// <summary>
    /// NHandCardHolder.UpdateCard: the card's own visuals, then the glow around it. Blue for a card they can play, gold
    /// or red for one the card itself (or the choice they are making) wants to call out.
    /// <para>
    /// The glow lives on the pooled node, which is allowed here: <c>OnReturnedFromPool</c> puts the highlight back to
    /// its playable colour and hides it, so nothing we do to it outlives our hold on the card.
    /// </para>
    /// </summary>
    private void UpdateCard(NCard node)
    {
        if (!GodotObject.IsInstanceValid(node) || !node.IsNodeReady())
        {
            return;
        }
        node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
        CardModel? card = node.Model;
        if (card == null || !CombatManager.Instance.IsInProgress)
        {
            return;
        }
        bool red = ShouldGlowRed(card);
        bool gold = ShouldGlowGold(card);
        if (!card.CanPlay() && !red && !gold)
        {
            node.CardHighlight.AnimHide();
            return;
        }
        node.CardHighlight.AnimShow();
        node.CardHighlight.Modulate = red ? NCardHighlight.red : gold ? NCardHighlight.gold : NCardHighlight.playableColor;
    }

    /// <summary>NHandCardHolder.ShouldGlowGold, with the teammate's choice standing in for the real hand's.</summary>
    private bool ShouldGlowGold(CardModel card)
    {
        bool? chosen = _player == null ? null : HandSelection.GetChoice(_player)?.GlowsGold(card);
        if (chosen != null)
        {
            return chosen.Value;
        }
        return card.CanPlay() && card.ShouldGlowGold && IsInPlayPhase(card);
    }

    /// <summary>NHandCardHolder.ShouldGlowRed.</summary>
    private static bool ShouldGlowRed(CardModel card)
    {
        return card.ShouldGlowRed && IsInPlayPhase(card);
    }

    private static bool IsInPlayPhase(CardModel card)
    {
        return card.Owner?.PlayerCombatState?.Phase == PlayerTurnPhase.Play;
    }

    /// <summary>
    /// NHandCardHolder.Flash: a quick pulse behind the card when it changes under the teammate's hands, such as being
    /// upgraded or having its cost or keywords changed.
    /// </summary>
    private void Flash(CardModel card)
    {
        if (!_flashes.TryGetValue(card, out Control? flash) || !GodotObject.IsInstanceValid(flash) || !IsInsideTree())
        {
            return;
        }
        flash.Scale = Vector2.One;
        flash.Modulate = ShouldGlowGold(card) ? NCardHighlight.gold
            : ShouldGlowRed(card) ? NCardHighlight.red
            : NCardHighlight.playableColor;
        if (_flashTweens.Remove(card, out Tween? running))
        {
            running.Kill();
        }
        Tween tween = flash.CreateTween();
        tween.TweenProperty(flash, "modulate:a", 0.6, 0.15);
        tween.TweenProperty(flash, "modulate:a", 0, 0.3);
        _flashTweens[card] = tween;
    }

    /// <summary>
    /// Our own copy of the pulse a hand card holder keeps behind its card. The holder scene is what the real hand
    /// builds each card on, so it is already loaded in combat; we take just its Flash node, as a plain copy with no
    /// script or signals, and let go of the rest.
    /// </summary>
    private static Control? CreateFlash()
    {
        Node? holder = null;
        try
        {
            holder = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath("cards/holders/hand_card_holder"))
                .Instantiate(PackedScene.GenEditState.Disabled);
            if (holder.GetNodeOrNull<Control>("Flash")?.Duplicate(0) is not Control flash)
            {
                return null;
            }
            flash.Name = "SpectateFlash";
            flash.Modulate = new Color(flash.Modulate, 0f);
            IgnoreMouse(flash);
            return flash;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to build a card flash for the mirrored hand: {e}");
            return null;
        }
        finally
        {
            holder?.Free();
        }
    }

    private static void WatchForFlash(CardModel card, Action flash)
    {
        card.Upgraded += flash;
        card.KeywordsChanged += flash;
        card.ReplayCountChanged += flash;
        card.AfflictionChanged += flash;
        card.EnergyCostChanged += flash;
        card.StarCostChanged += flash;
    }

    private static void StopWatchingForFlash(CardModel card, Action flash)
    {
        card.Upgraded -= flash;
        card.KeywordsChanged -= flash;
        card.ReplayCountChanged -= flash;
        card.AfflictionChanged -= flash;
        card.EnergyCostChanged -= flash;
        card.StarCostChanged -= flash;
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
            Depart(gone);
        }

        List<(CardModel card, Control slot, PileType? from)> arrivals = new();
        foreach (CardModel card in _order)
        {
            if (_slots.ContainsKey(card))
            {
                continue;
            }
            // Back before it was seen off: the old slot goes, and the card comes in like any other.
            foreach (Departure stale in _departing.Where(d => d.Card == card).ToList())
            {
                stale.Tween?.Kill();
                _departing.Remove(stale);
                Free(stale.Node, stale.Slot);
            }
            PileType? from = _cameFrom.Remove(card, out PileType left) ? left : null;
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
            // Ahead of the card, as in the holder scene, so the pulse shows round its edges and not over its face.
            Control? flash = CreateFlash();
            if (flash != null)
            {
                slot.AddChildSafely(flash);
                _flashes[card] = flash;
            }
            slot.AddChildSafely(node);
            UpdateCard(node);

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

            Action onChanged = () => Safe.Run("flash a changed card", () => Flash(captured));
            WatchForFlash(card, onChanged);
            _flashHandlers[card] = onChanged;
            arrivals.Add((card, slot, from));
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
        if (!_snap)
        {
            foreach ((CardModel card, Control slot, PileType? from) in arrivals)
            {
                PlaceArrival(card, slot, from);
            }
        }
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
        _drop = Mathf.Clamp(_realHand.Position.Y, 0f, disabledHandDrop);
        GlobalPosition = _realHand.CardHolderContainer.GlobalPosition + Vector2.Up * _drop;

        // Where the real hand puts its own prompt, lifted by the same amount as the cards.
        RichTextLabel? header = _realHand._selectionHeader;
        if (_selectionHeader.Visible && header != null && GodotObject.IsInstanceValid(header))
        {
            _selectionHeader.Size = header.Size;
            _selectionHeader.GlobalPosition = header.GlobalPosition + Vector2.Up * _drop;
        }
        Control? backstop = _realHand._selectModeBackstop;
        if (_selectionBackstop is { Visible: true } && backstop != null && GodotObject.IsInstanceValid(backstop))
        {
            _selectionBackstop.Size = backstop.Size;
            _selectionBackstop.GlobalPosition = backstop.GlobalPosition + Vector2.Up * _drop;
        }

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

        // While they pick from their hand, the game shows only the cards that qualify and lifts the ones they have
        // marked out of the fan into a row above it. Do the same.
        HandSelection.Choice? choice = _player == null ? null : HandSelection.GetChoice(_player);
        IReadOnlyList<CardModel> marked = _player == null ? Array.Empty<CardModel>() : HandSelection.GetMarked(_player);
        List<CardModel> visible = _order
            .Where(c => _slots.ContainsKey(c) && !_handedOff.Contains(c) && !marked.Contains(c)
                        && (choice == null || choice.Allows(c)))
            .ToList();
        HideLeftOut(visible, marked);
        LayoutMarked(marked);

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
                // so shows the whole card whichever slot it came from. Like RefreshLayout, the card stands up, grows
                // and rises at once, and only slides sideways into place.
                slot.Scale = Vector2.One;
                slot.Rotation = 0f;
                slot.Position = new Vector2(slot.Position.X, focusCentreY);
                MoveTo(visible[i], slot, new Vector2(position.X, focusCentreY), 0f, Vector2.One);
                slot.ZIndex = 2;
            }
            else
            {
                MoveTo(visible[i], slot,
                    position + (isSecondary ? Vector2.Up * secondaryRaise : Vector2.Zero),
                    isSecondary ? 0f : Mathf.RadToDeg(angle),
                    isSecondary ? Vector2.One : scale);
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

    /// <summary>Hides the cards that are neither in the fan nor marked: the ones a choice rules out.</summary>
    private void HideLeftOut(List<CardModel> inFan, IReadOnlyList<CardModel> marked)
    {
        foreach ((CardModel card, Control slot) in _slots)
        {
            if (inFan.Contains(card) || marked.Contains(card) || !GodotObject.IsInstanceValid(slot))
            {
                continue;
            }
            slot.Visible = false;
            if (_hitboxes.TryGetValue(card, out Control? hitbox) && GodotObject.IsInstanceValid(hitbox))
            {
                hitbox.Visible = false;
            }
        }
    }

    /// <summary>
    /// The cards the teammate has marked, in a row above the fan. Laid out the way NSelectedHandCardContainer does it:
    /// centred on the real container, one card width apart, each card shrunk to a holder's small scale. The one being
    /// read, by them or by us, comes up to full size.
    /// </summary>
    private void LayoutMarked(IReadOnlyList<CardModel> marked)
    {
        List<CardModel> shown = marked.Where(_slots.ContainsKey).ToList();
        if (shown.Count == 0)
        {
            return;
        }

        Vector2 centre = MarkedRowCentre();
        CardModel? focus = _localHovered ?? _remoteHovered;
        float x = -NCard.defaultSize.X * (shown.Count - 1) * 0.5f;
        foreach (CardModel card in shown)
        {
            Vector2 position = centre + new Vector2(x, 0f);
            x += NCard.defaultSize.X;
            Control slot = _slots[card];
            if (!GodotObject.IsInstanceValid(slot))
            {
                continue;
            }
            bool isFocus = card == focus;
            slot.Visible = true;
            MoveTo(card, slot, position, 0f, isFocus ? Vector2.One : NCardHolder.smallScale);
            slot.ZIndex = isFocus ? 2 : 1;

            if (_hitboxes.TryGetValue(card, out Control? hitbox) && GodotObject.IsInstanceValid(hitbox))
            {
                hitbox.Visible = true;
                hitbox.Position = position - NCard.defaultSize * 0.5f;
                hitbox.Scale = NCardHolder.smallScale;
                hitbox.Rotation = 0f;
            }
        }
    }

    /// <summary>
    /// The real hand's selected-card container, in our space. It sinks with the real hand, so lift it by the same
    /// amount we lift ourselves.
    /// </summary>
    private Vector2 MarkedRowCentre()
    {
        Control? container = _realHand?._selectedHandCardContainer;
        if (container == null || !GodotObject.IsInstanceValid(container))
        {
            return new Vector2(0f, -NCard.defaultSize.Y);
        }
        return container.GlobalPosition + Vector2.Up * _drop - GlobalPosition;
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
        ClearDepartures();
        _slots.Clear();
        _cards.Clear();
        _hitboxes.Clear();
        _targets.Clear();
        _handedOff.Clear();
        _order.Clear();
    }

    private void Release(CardModel card)
    {
        Forget(card);
        _cards.Remove(card, out NCard? node);
        _slots.Remove(card, out Control? slot);
        Free(node, slot);
    }

    /// <summary>
    /// Frees a card and the slot it hangs from. The card goes back to the pool first: QueueFreeSafely detaches an
    /// IPoolable from its parent immediately and defers only the pool hand-back. Freeing the slot first would take the
    /// card down with it and lose the node.
    /// </summary>
    private static void Free(NCard? node, Control? slot)
    {
        if (node != null && GodotObject.IsInstanceValid(node))
        {
            node.QueueFreeSafely();
        }
        if (slot != null && GodotObject.IsInstanceValid(slot))
        {
            slot.QueueFreeSafely();
        }
    }

    /// <summary>Everything we keep about a card in the hand apart from its slot and node.</summary>
    private void Forget(CardModel card)
    {
        if (_flashHandlers.Remove(card, out Action? onChanged))
        {
            StopWatchingForFlash(card, onChanged);
        }
        if (_flashTweens.Remove(card, out Tween? flashTween))
        {
            flashTween.Kill();
        }
        // The flash is ours, not pooled, and goes down with the slot.
        _flashes.Remove(card);
        _targets.Remove(card);
        _handedOff.Remove(card);

        if (_hitboxes.Remove(card, out Control? hitbox) && GodotObject.IsInstanceValid(hitbox))
        {
            hitbox.QueueFreeSafely();
        }
        if (_remoteHovered == card)
        {
            _remoteHovered = null;
        }
        if (_playCard == card)
        {
            _playCard = null;
            _playVisual = PlayVisual.None;
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

    /// <summary>Where a card's slot is heading: its place in the fan, or in the row of marked cards.</summary>
    private readonly record struct SlotTarget(Vector2 Position, float Degrees, Vector2 Scale);

    /// <summary>A card that has left the hand, still in its slot until it is sent on (or faded out).</summary>
    private sealed class Departure(CardModel card, Control slot, NCard node)
    {
        public CardModel Card { get; } = card;
        public Control Slot { get; } = slot;
        public NCard Node { get; } = node;
        public int Frames { get; set; }
        public Tween? Tween { get; set; }
    }
}
