using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Teammates who are in the middle of picking cards from their hand (discard, exhaust, upgrade...), so the mirrored hand
/// can show the same thing they are looking at.
/// <para>
/// It comes from two places. The choice itself (the prompt and which cards may be picked) we learn locally: our client
/// runs the teammate's card too, and it stops in <c>CardSelectCmd.FromHand</c> waiting for their answer, see
/// <see cref="Patches.CardSelectPatches" />. The cards they have marked but not confirmed yet never leave their machine
/// in the base game, so those come over the mod's own <see cref="Net.zSpectateHandSelectionMessage" />, and only when
/// they have the mod too.
/// </para>
/// </summary>
public static class HandSelection
{
    public sealed class Choice
    {
        public required string Prompt { get; init; }

        public Func<CardModel, bool>? Filter { get; init; }

        /// <summary>
        /// Whether a card in their hand can be picked. The game hides the rest while choosing, and so do we. A filter
        /// that throws counts as "yes": showing one card too many beats losing the hand.
        /// </summary>
        public bool Allows(CardModel card)
        {
            try
            {
                return Filter?.Invoke(card) ?? true;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    private static readonly Dictionary<ulong, Choice> choices = new();
    private static readonly Dictionary<ulong, List<CardModel>> marked = new();

    /// <summary>Raised with the player's NetId whenever their choice or their marked cards change.</summary>
    public static event Action<ulong>? Changed;

    public static Choice? GetChoice(Player player)
    {
        return choices.GetValueOrDefault(player.NetId);
    }

    /// <summary>Cards the player has marked so far. Empty outside a choice, whatever arrived early.</summary>
    public static IReadOnlyList<CardModel> GetMarked(Player player)
    {
        if (!choices.ContainsKey(player.NetId) || !marked.TryGetValue(player.NetId, out List<CardModel>? cards))
        {
            return Array.Empty<CardModel>();
        }
        return cards;
    }

    public static void Begin(Player player, Choice choice)
    {
        choices[player.NetId] = choice;
        Raise(player.NetId);
    }

    /// <summary>
    /// <paramref name="choice" /> was made (or cancelled): back to a plain hand. Only that choice ends; a player who
    /// discards card after card can be into their next one by the time the last one's end reaches us.
    /// <para>
    /// Marked cards are left alone. Their client clears them itself with an empty list once it is done, and when ours
    /// is behind theirs, what is already here belongs to their next choice: wiping it here lost those marks until they
    /// changed them again.
    /// </para>
    /// </summary>
    public static void End(Player player, Choice choice)
    {
        if (choices.GetValueOrDefault(player.NetId) != choice)
        {
            return;
        }
        choices.Remove(player.NetId);
        Raise(player.NetId);
    }

    /// <summary>
    /// Kept even when no choice is open yet: their client can be ahead of ours, and the first card they mark may arrive
    /// before our copy of their card has reached the choice.
    /// </summary>
    public static void SetMarked(ulong netId, List<CardModel> cards)
    {
        if (cards.Count == 0)
        {
            if (!marked.Remove(netId))
            {
                return;
            }
        }
        else
        {
            marked[netId] = cards;
        }
        Raise(netId);
    }

    public static void Clear()
    {
        choices.Clear();
        marked.Clear();
    }

    private static void Raise(ulong netId)
    {
        try
        {
            Changed?.Invoke(netId);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Hand selection listener threw: {e}");
        }
    }
}
