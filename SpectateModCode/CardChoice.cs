using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Teammates who have a "pick one of these cards" screen up: a card reward, or a card that makes you choose one of a
/// few generated ones in combat. Filled in by <see cref="Patches.CardChoicePatches" /> from the copy of the choice our
/// own client is running for them; see <see cref="Ui.NSpectateScreenMirror" /> for how it is drawn.
/// </summary>
public static class CardChoice
{
    public enum Kind
    {
        /// <summary>NCardRewardSelectionScreen: a card reward, with its alternatives (skip, reroll...) underneath.</summary>
        Reward,

        /// <summary>NChooseACardSelectionScreen: in combat, with at most a skip button.</summary>
        ChooseACard,

        /// <summary>NChooseABundleSelectionScreen: a few packs of cards, of which one is kept (Scroll Boxes).</summary>
        Bundle
    }

    /// <summary>One of the buttons under a card reward, as NCardRewardAlternativeButton.Create takes it.</summary>
    public readonly record struct Alternative(string Title, string Hotkey);

    public sealed class Choice
    {
        public required Kind Kind { get; init; }

        /// <summary>Read live: a reroll swaps the cards of a reward in place. Empty for <see cref="Kind.Bundle" />.</summary>
        public Func<IReadOnlyList<CardModel>> Cards { get; init; } = Array.Empty<CardModel>;

        /// <summary>Only for <see cref="Kind.Bundle" />: the packs on offer, in the order the screen lines them up.</summary>
        public IReadOnlyList<IReadOnlyList<CardModel>> Bundles { get; init; } = Array.Empty<IReadOnlyList<CardModel>>();

        /// <summary>Read live for the same reason. Always empty for <see cref="Kind.ChooseACard" />.</summary>
        public Func<IReadOnlyList<Alternative>> Alternatives { get; init; } = Array.Empty<Alternative>;

        /// <summary>Only used by <see cref="Kind.ChooseACard" />, which has a skip button of its own.</summary>
        public bool CanSkip { get; init; }
    }

    private static readonly Dictionary<ulong, Choice> choices = new();

    public static Choice? Get(Player player)
    {
        return choices.GetValueOrDefault(player.NetId);
    }

    public static void Begin(Player player, Choice choice)
    {
        choices[player.NetId] = choice;
    }

    /// <summary>Only ends the choice it was started for, so a newer one opened right after is left alone.</summary>
    public static void End(Player player, Choice choice)
    {
        if (choices.TryGetValue(player.NetId, out Choice? current) && current == choice)
        {
            choices.Remove(player.NetId);
        }
    }

    public static void Clear()
    {
        choices.Clear();
    }
}
