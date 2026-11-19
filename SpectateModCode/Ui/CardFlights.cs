using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// The flights a card makes between a hand and the piles, done the way CardPileCmd.Add does them for our own cards.
/// The game leaves them out for anybody else's: a teammate's cards only ever fly in and out of the play area on our
/// screen, and the rest just changes in their piles. While we watch them, their hand and piles are where ours are, so
/// the same moves are worth seeing.
/// </summary>
public static class CardFlights
{
    /// <summary>How far CardPileCmd.Add sinks a teammate's played card as it fades out.</summary>
    private const float fadeSink = 25f;

    /// <summary>Scale a card settles at in the play area (CardPileCmd.AppendPileLerpTween).</summary>
    public const float PlayScale = 0.8f;

    /// <summary>How long CardPileCmd.Add takes to fade a card out.</summary>
    public static float FadeSeconds => SaveManager.Instance.PrefsSave.FastMode == FastModeType.Fast ? 0.2f : 0.3f;

    /// <summary>
    /// Whether the draw, discard and exhaust buttons show <paramref name="player" />'s piles. They are where a flight
    /// lands; with ours on them, the teammate's card would look like it went into our pile.
    /// </summary>
    public static bool PilesShow(Player player)
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;
        return ui != null && GodotObject.IsInstanceValid(ui) && ui.DrawPile._pile == PileType.Draw.GetPile(player);
    }

    /// <summary>The middle of a pile's button, in global space. Only for the piles with a button.</summary>
    public static Vector2 PileCentre(PileType type)
    {
        return type.GetTargetPosition(null);
    }

    /// <summary>Where a played card settles, in global space (PileType.Play's target, for a card centred on itself).</summary>
    public static Vector2 PlayCentre()
    {
        Control? play = NCombatRoom.Instance?.Ui.PlayContainer;
        if (play == null || !GodotObject.IsInstanceValid(play))
        {
            return Vector2.Zero;
        }
        return play.GlobalPosition + play.Size * 0.5f + Vector2.Up * 100f;
    }

    /// <summary>
    /// Sends a card from where it is into a pile's button, trailing the owner's colour. The flight takes the node over
    /// and frees it at the end, as it does for the game's own cards.
    /// </summary>
    public static void FlyToPile(NCard node, PileType type)
    {
        Player? owner = node.Model?.Owner;
        Node? container = owner?.Creature.GetVfxContainer();
        if (owner == null || container == null)
        {
            node.QueueFreeSafely();
            return;
        }
        // Not adding to the pile: the button already counted the card when it arrived (SpectateView.SyncRemoteCount),
        // and a second bump on landing would read as a second card.
        NCardFlyVfx? flight = NCardFlyVfx.Create(node, type, isAddingToPile: false, owner.Character.TrailPath);
        if (flight == null)
        {
            node.QueueFreeSafely();
            return;
        }
        node.Reparent(container);
        container.AddChildSafely(flight);
    }

    /// <summary>The burst of embers a card goes up in as it is exhausted. The card itself is the caller's to fade.</summary>
    public static void Exhaust(NCard node)
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;
        NCardExhaustVfx? burst = NCardExhaustVfx.Create(node);
        if (burst == null)
        {
            return;
        }
        if (ui == null || !GodotObject.IsInstanceValid(ui))
        {
            burst.QueueFreeSafely();
            return;
        }
        ui.AddChildSafely(burst);
    }

    /// <summary>A card back flying from one pile to another, the way a shuffle sends the discard pile back.</summary>
    public static void Shuffle(CardPile from, CardPile to, Player owner)
    {
        Node? container = owner.Creature.GetVfxContainer();
        if (container == null)
        {
            return;
        }
        NCardFlyShuffleVfx? flight = NCardFlyShuffleVfx.Create(from, to, owner.Character.TrailPath);
        if (flight != null)
        {
            container.AddChildSafely(flight);
        }
    }

    /// <summary>
    /// A teammate's played card leaving the play area for the discard or draw pile. The game has its own card for it
    /// there, which it sinks and fades where it stands instead of flying it off as it does ours. That card is hidden
    /// for the game to finish with, and a copy takes the flight in its place.
    /// </summary>
    public static void LeavePlay(CardModel card, PileType to)
    {
        NCard? played = NCombatRoom.Instance?.Ui.GetCardFromPlayContainer(card);
        if (played == null || !GodotObject.IsInstanceValid(played) || !played.IsInsideTree())
        {
            return;
        }
        Node? container = card.Owner?.Creature.GetVfxContainer();
        NCard? copy = container == null ? null : NCard.Create(card);
        if (copy == null)
        {
            return;
        }
        container!.AddChildSafely(copy);
        copy.GlobalPosition = played.GlobalPosition;
        copy.Scale = played.Scale;
        copy.UpdateVisuals(to, CardPreviewMode.Normal);
        // Visible is one of the things the pool puts back when the game frees its card.
        played.Visible = false;
        FlyToPile(copy, to);
    }

    /// <summary>
    /// What CardPileCmd.Add does with a teammate's card it has no flight for: sinks it a little while it fades to
    /// nothing. Runs on <paramref name="holder" />, the node the card hangs from, and calls back once it is gone.
    /// </summary>
    public static Tween FadeOut(Control holder, bool sink, Action done)
    {
        Tween tween = holder.CreateTween().SetParallel();
        if (sink)
        {
            tween.TweenProperty(holder, "position", holder.Position + Vector2.Down * fadeSink, FadeSeconds);
        }
        tween.TweenProperty(holder, "modulate", StsColors.exhaustGray, FadeSeconds);
        tween.Chain().TweenCallback(Callable.From(done));
        return tween;
    }
}
