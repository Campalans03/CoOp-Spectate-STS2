using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Notices when a teammate starts picking cards from their hand. Every client runs every player's cards, so when a
/// teammate plays, say, a discard card, our copy of it reaches the same hand selection and sits in there until their
/// answer comes over the network. That wait is exactly how long they spend choosing, and the arguments say what they
/// are choosing: the prompt and which cards qualify.
/// <para>
/// These are async methods, so the postfix runs once the method first yields, with the task that finishes when the
/// choice is made. A task that is already done never got as far as asking (nothing to pick, or only one way to pick).
/// </para>
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd))]
public static class CardSelectPatches
{
    /// <summary>Also covers FromHandForDiscard, which dresses up the prefs and calls this.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromHand))]
    public static void AfterFromHand(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter,
        Task<IEnumerable<CardModel>> __result)
    {
        Track(player, prefs.Prompt, filter, prefs.ShouldGlowGold, __result);
    }

    /// <summary>Builds its own prompt and filter inside, so these are the same ones it hands to the real hand.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromHandForUpgrade))]
    public static void AfterFromHandForUpgrade(Player player, Task<CardModel?> __result)
    {
        Track(player, new LocString("gameplay_ui", "CHOOSE_CARD_UPGRADE_HEADER"), c => c.IsUpgradable, null,
            __result);
    }

    private static void Track(Player player, LocString prompt, Func<CardModel, bool>? filter,
        Func<CardModel, bool>? glowGold, Task task)
    {
        try
        {
            if (LocalContext.IsMe(player) || task.IsCompleted || !SpectateController.IsActive)
            {
                return;
            }
            HandSelection.Choice choice = new()
            {
                Prompt = prompt.GetFormattedText(),
                Filter = filter,
                GlowGold = glowGold
            };
            HandSelection.Begin(player, choice);
            TaskWatch.WhenDone(task, () => HandSelection.End(player, choice));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to follow a teammate's hand selection: {e}");
        }
    }
}
