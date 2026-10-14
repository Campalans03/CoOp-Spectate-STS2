using System;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Points the top bar's gold, hit points, character portrait and deck at somebody else and back, the way
/// <see cref="RelicView" /> does for the relic row. Like the relics they belong to the run, so they have to be handed
/// back to us whenever the watching ends.
/// <para>
/// Each of them subscribes in Initialize without letting go of the player it had, so the old player's handlers are
/// taken off first; a handler rebuilt from the same target and method is equal to the one subscribed, so -= finds it.
/// </para>
/// </summary>
public static class TopBarView
{
    private static NTopBar? TopBar => NRun.Instance?.GlobalUi?.TopBar;

    /// <summary>Shows <paramref name="owner" />'s gold, hit points and portrait. Pass our own player to put ours back.</summary>
    public static void Show(Player owner)
    {
        NTopBar? bar = TopBar;
        if (bar == null || !GodotObject.IsInstanceValid(bar))
        {
            return;
        }
        Guard("gold", () => ShowGold(bar.Gold, owner));
        Guard("hit points", () => ShowHp(bar.Hp, owner));
        Guard("portrait", () => ShowPortrait(bar.Portrait, owner));
    }

    /// <summary>
    /// Shows <paramref name="owner" />'s deck on the deck button: its card count, and their cards when it is opened.
    /// Pass our own player to put ours back.
    /// </summary>
    public static void ShowDeck(Player owner)
    {
        NTopBar? bar = TopBar;
        if (bar == null || !GodotObject.IsInstanceValid(bar))
        {
            return;
        }
        Guard("deck", () => ShowDeck(bar.Deck, owner));
    }

    /// <summary>Each part on its own, so a failure in one only costs that one.</summary>
    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to swap the top bar {what}: {e}");
        }
    }

    private static void ShowGold(NTopBarGold gold, Player owner)
    {
        if (!GodotObject.IsInstanceValid(gold) || gold._player == owner)
        {
            return;
        }
        if (gold._player != null)
        {
            gold._player.GoldChanged -= gold.UpdateGold;
        }
        gold.Initialize(owner);
    }

    private static void ShowHp(NTopBarHp hp, Player owner)
    {
        if (!GodotObject.IsInstanceValid(hp) || hp._player == owner)
        {
            return;
        }
        if (hp._player != null)
        {
            hp._player.Creature.CurrentHpChanged -= hp.UpdateHealth;
            hp._player.Creature.MaxHpChanged -= hp.UpdateHealth;
        }
        hp.Initialize(owner);
    }

    private static void ShowDeck(NTopBarDeckButton deck, Player owner)
    {
        if (!GodotObject.IsInstanceValid(deck) || deck._player == owner)
        {
            return;
        }
        StopCountingRemoteDeck();
        if (deck._pile != null)
        {
            deck._pile.CardAddFinished -= deck.OnPileContentsChanged;
            deck._pile.CardRemoveFinished -= deck.OnPileContentsChanged;
        }
        // Swapping decks is not a card arriving: start the count where the new deck stands, so it neither bumps now
        // nor misses the bump of the next card that does arrive.
        deck._count = PileType.Deck.GetPile(owner).Cards.Count;
        deck.Initialize(owner);
        if (!LocalContext.IsMe(owner))
        {
            // The button only counts a card once it has flown into the deck, and a teammate's cards never fly: the game
            // fades them out instead. Follow the pile itself.
            CardPile pile = deck._pile!;
            Action handler = () => Safe.Run("count a teammate's deck", () =>
            {
                if (GodotObject.IsInstanceValid(deck))
                {
                    deck.OnPileContentsChanged();
                }
            });
            pile.ContentsChanged += handler;
            _remoteDeck = (pile, handler);
        }

        // An open deck view shows the deck the button pointed at when it was opened; one of somebody no longer on show
        // would be passed off as theirs.
        NCapstoneContainer? capstone = NCapstoneContainer.Instance;
        if (capstone?.CurrentCapstoneScreen is NDeckViewScreen view && view._player != owner)
        {
            capstone.Close();
        }
    }

    /// <summary>The teammate's deck the button is following, and the handler that keeps its count honest.</summary>
    private static (CardPile pile, Action handler)? _remoteDeck;

    private static void StopCountingRemoteDeck()
    {
        if (_remoteDeck is { } entry)
        {
            entry.pile.ContentsChanged -= entry.handler;
            _remoteDeck = null;
        }
    }

    /// <summary>The portrait is only the character's icon, added as a child; swap the child.</summary>
    private static void ShowPortrait(NTopBarPortrait portrait, Player owner)
    {
        if (!GodotObject.IsInstanceValid(portrait))
        {
            return;
        }
        // Until we first swap it, it is the game's own: ours.
        ulong? shown = portrait.HasMeta(ownerMeta)
            ? portrait.GetMeta(ownerMeta).AsUInt64()
            : SpectateController.Me?.NetId;
        if (shown == owner.NetId)
        {
            return;
        }
        foreach (Node child in portrait.GetChildren())
        {
            portrait.RemoveChildSafely(child);
            child.QueueFreeSafely();
        }
        portrait.Initialize(owner);
        portrait.SetMeta(ownerMeta, owner.NetId);
    }

    /// <summary>The portrait doesn't remember whose it is, so we note it on the node.</summary>
    private static readonly StringName ownerMeta = "spectate_owner";
}
