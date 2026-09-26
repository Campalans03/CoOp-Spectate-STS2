using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Teammates who have a grid of cards up to pick from: their deck (smith, removal, transform, enchant) or a combat pile
/// or a generated set. Works like <see cref="HandSelection" />: what they are choosing from we learn locally, from our
/// copy of the choice (<see cref="Patches.GridSelectPatches" />); what they have marked, and how far they have scrolled,
/// comes over <see cref="Net.zSpectateGridSelectionMessage" /> when they have the mod.
/// </summary>
public static class GridSelection
{
    /// <summary>Which of the game's grid screens it is, which decides the scene the mirror dresses up.</summary>
    public enum Screen
    {
        Simple,
        CombatPile,
        DeckGeneric,
        DeckUpgrade,
        DeckTransform,
        DeckEnchant
    }

    public sealed class Choice
    {
        public required Screen Screen { get; init; }

        /// <summary>
        /// The cards on offer, in the order the real screen is handed them. Read live: a combat pile can change while it
        /// is open. Marked cards travel as indexes into this list, so it has to match the chooser's exactly.
        /// </summary>
        public required Func<IReadOnlyList<CardModel>> Options { get; init; }

        public required string Prompt { get; init; }

        public bool Cancelable { get; init; }

        /// <summary>What the grid draws the cards as coming from; only a combat pile is anything but None.</summary>
        public PileType PileType { get; init; } = PileType.None;

        /// <summary>The draw pile is shown sorted so it doesn't give away the draw order; everything else is not.</summary>
        public List<SortingOrders> Sorting { get; init; } = new() { SortingOrders.Ascending };

        /// <summary>Only for <see cref="Screen.DeckEnchant" />: shown beside the grid, ready to apply.</summary>
        public EnchantmentModel? Enchantment { get; init; }
    }

    /// <summary>
    /// Where they are in the grid: the marked positions and the scroll. The pack picker (<see cref="CardChoice.Kind.Bundle" />)
    /// has no grid but uses the same message, with the pack they have opened as its one marked position.
    /// </summary>
    public readonly record struct Progress(IReadOnlyList<int> Marked, float Scroll);

    private static readonly Dictionary<ulong, Choice> choices = new();
    private static readonly Dictionary<ulong, Progress> progress = new();

    public static Choice? GetChoice(Player player)
    {
        return choices.GetValueOrDefault(player.NetId);
    }

    /// <summary>What they have marked and where they have scrolled. Empty until they say, or if they don't have the mod.</summary>
    public static Progress GetProgress(Player player)
    {
        return progress.TryGetValue(player.NetId, out Progress value)
            ? value
            : new Progress(Array.Empty<int>(), 0f);
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
            progress.Remove(player.NetId);
        }
    }

    public static void SetProgress(ulong netId, Progress value)
    {
        progress[netId] = value;
    }

    public static void Clear()
    {
        choices.Clear();
        progress.Clear();
    }
}
