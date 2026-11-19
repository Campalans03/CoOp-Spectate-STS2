using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Notices when a teammate is picking one card out of a few, the same way <see cref="CardSelectPatches" /> does for the
/// hand: our client runs their side of it too and waits for their answer, and that wait is the time they spend on the
/// screen.
/// </summary>
[HarmonyPatch]
public static class CardChoicePatches
{
    /// <summary>
    /// The alternatives a card reward last offered. CardReward.OnSelect builds them in a local, so catch them on the way
    /// out of Generate, which it calls for remote players too. Weak, so rewards are not kept alive by it.
    /// </summary>
    private static readonly ConditionalWeakTable<CardReward, IReadOnlyList<CardRewardAlternative>> alternatives = new();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CardRewardAlternative), nameof(CardRewardAlternative.Generate))]
    public static void AfterGenerate(CardReward cardReward, IReadOnlyList<CardRewardAlternative> __result)
    {
        alternatives.AddOrUpdate(cardReward, __result);
    }

    /// <summary>
    /// Runs when the owner of a card reward opens it. For a teammate that is us replaying their click, and the task
    /// finishes when they are done with the reward, whether they took a card, skipped, or left it for later.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CardReward), "OnSelect")]
    public static void AfterCardRewardSelect(CardReward __instance, Task<bool> __result)
    {
        CardReward reward = __instance;
        Track(reward.Player, __result, new CardChoice.Choice
        {
            Kind = CardChoice.Kind.Reward,
            Cards = () => reward._cards.Select(c => c.Card).ToList(),
            Alternatives = () => alternatives.TryGetValue(reward, out IReadOnlyList<CardRewardAlternative>? list)
                ? list.Select(a => new CardChoice.Alternative(a.Title.GetFormattedText(), a.Hotkeys.FirstOrDefault() ?? "")).ToList()
                : Array.Empty<CardChoice.Alternative>()
        });
    }

    /// <summary>In combat: potions and cards that make you pick one of a few generated cards.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    public static void AfterChooseACard(IReadOnlyList<CardModel> cards, Player player, bool canSkip,
        Task<CardModel?> __result)
    {
        Track(player, __result, new CardChoice.Choice
        {
            Kind = CardChoice.Kind.ChooseACard,
            Cards = () => cards,
            CanSkip = canSkip
        });
    }

    /// <summary>Scroll Boxes: a few packs of cards, of which they keep one.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseABundleScreen))]
    public static void AfterChooseABundle(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        Task<IEnumerable<CardModel>> __result)
    {
        Track(player, __result, new CardChoice.Choice
        {
            Kind = CardChoice.Kind.Bundle,
            Bundles = bundles
        });
    }

    private static void Track(Player player, Task task, CardChoice.Choice choice)
    {
        try
        {
            if (LocalContext.IsMe(player) || task.IsCompleted || !SpectateController.IsActive)
            {
                return;
            }
            CardChoice.Begin(player, choice);
            TaskWatch.WhenDone(task, () => CardChoice.End(player, choice));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to follow a teammate's card choice: {e}");
        }
    }
}
