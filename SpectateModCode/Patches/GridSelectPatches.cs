using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Notices when a teammate opens a grid of cards to pick from, the same way <see cref="CardSelectPatches" /> does for
/// the hand. Each postfix rebuilds the list the real screen is given exactly as the method builds it (same filter, same
/// order), because the cards they mark come over as positions in that list.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd))]
public static class GridSelectPatches
{
    private static readonly List<SortingOrders> drawPileSorting =
        new() { SortingOrders.RarityAscending, SortingOrders.AlphabetAscending };

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromSimpleGrid))]
    public static void AfterSimpleGrid(IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs,
        Task<IEnumerable<CardModel>> __result)
    {
        List<CardModel> cards = cardsIn.ToList();
        Track(player, __result, new GridSelection.Choice
        {
            Screen = GridSelection.Screen.Simple,
            Options = () => cards,
            Prompt = prefs.Prompt.GetFormattedText()
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromSimpleGridForRewards))]
    public static void AfterSimpleGridForRewards(List<CardCreationResult> cards, Player player, CardSelectorPrefs prefs,
        Task<IEnumerable<CardModel>> __result)
    {
        List<CardModel> options = cards.Select(c => c.Card).ToList();
        Track(player, __result, new GridSelection.Choice
        {
            Screen = GridSelection.Screen.Simple,
            Options = () => options,
            Prompt = prefs.Prompt.GetFormattedText()
        });
    }

    /// <summary>
    /// The five-argument overload; the other one calls it. Read live, the way NCombatPileCardSelectScreen re-reads the
    /// pile whenever it changes under it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromCombatPile), typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player),
        typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>))]
    public static void AfterCombatPile(CardPile pile, Player player, CardSelectorPrefs prefs,
        Func<CardModel, bool> filter, Task<IEnumerable<CardModel>> __result)
    {
        Track(player, __result, new GridSelection.Choice
        {
            Screen = GridSelection.Screen.CombatPile,
            Options = () => pile.Cards.Where(filter).ToList(),
            Prompt = prefs.Prompt.GetFormattedText(),
            PileType = pile.Type,
            Sorting = pile.Type == PileType.Draw ? drawPileSorting : new List<SortingOrders> { SortingOrders.Ascending }
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromDeckForUpgrade))]
    public static void AfterDeckForUpgrade(Player player, CardSelectorPrefs prefs,
        Task<IEnumerable<CardModel>> __result)
    {
        List<CardModel> cards = PileType.Deck.GetPile(player).Cards.Where(c => c.IsUpgradable).ToList();
        Track(player, __result, new GridSelection.Choice
        {
            Screen = GridSelection.Screen.DeckUpgrade,
            Options = () => cards,
            Prompt = prefs.Prompt.GetFormattedText(),
            Cancelable = prefs.Cancelable
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromDeckForTransformation))]
    public static void AfterDeckForTransformation(Player player, CardSelectorPrefs prefs,
        Task<IEnumerable<CardModel>> __result)
    {
        List<CardModel> cards = PileType.Deck.GetPile(player).Cards
            .Where(c => c.Type != CardType.Quest && c.IsTransformable)
            .ToList();
        Track(player, __result, new GridSelection.Choice
        {
            Screen = GridSelection.Screen.DeckTransform,
            Options = () => cards,
            Prompt = prefs.Prompt.GetFormattedText(),
            Cancelable = prefs.Cancelable
        });
    }

    /// <summary>The overload the other two end up in, with the cards already picked out and ordered as in the deck.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromDeckForEnchantment), typeof(IReadOnlyList<CardModel>),
        typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs))]
    public static void AfterDeckForEnchantment(IReadOnlyList<CardModel> cards, EnchantmentModel enchantment,
        int amount, CardSelectorPrefs prefs, Task<IEnumerable<CardModel>> __result)
    {
        if (cards.Count == 0)
        {
            return;
        }
        Player player = cards[0].Owner;
        List<CardModel> deck = PileType.Deck.GetPile(player).Cards.ToList();
        List<CardModel> ordered = cards.OrderBy(c => deck.IndexOf(c)).ToList();
        EnchantmentModel shown = enchantment.ToMutable();
        shown.Amount = amount;
        shown.RecalculateValues();
        Track(player, __result, new GridSelection.Choice
        {
            Screen = GridSelection.Screen.DeckEnchant,
            Options = () => ordered,
            Prompt = prefs.Prompt.GetFormattedText(),
            Cancelable = prefs.Cancelable,
            Enchantment = shown
        });
    }

    /// <summary>Also covers FromDeckForRemoval, which is this with a filter and a curses-first order.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromDeckGeneric))]
    public static void AfterDeckGeneric(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter,
        Func<CardModel, int>? sortingOrder, Task<IEnumerable<CardModel>> __result)
    {
        List<CardModel> cards = PileType.Deck.GetPile(player).Cards.ToList();
        if (filter != null)
        {
            cards = cards.Where(filter).ToList();
        }
        if (sortingOrder != null)
        {
            cards = cards.OrderBy(sortingOrder).ToList();
        }
        Track(player, __result, new GridSelection.Choice
        {
            Screen = GridSelection.Screen.DeckGeneric,
            Options = () => cards,
            Prompt = prefs.Prompt.GetFormattedText(),
            Cancelable = prefs.Cancelable
        });
    }

    private static void Track(Player player, Task task, GridSelection.Choice choice)
    {
        try
        {
            if (LocalContext.IsMe(player) || task.IsCompleted || !SpectateController.IsActive)
            {
                return;
            }
            GridSelection.Begin(player, choice);
            TaskWatch.WhenDone(task, () => GridSelection.End(player, choice));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to follow a teammate's card grid: {e}");
        }
    }
}
