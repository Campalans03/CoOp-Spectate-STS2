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
        Track(player, prefs.Prompt, filter, __result);
    }

    /// <summary>Builds its own prompt and filter inside, so these are the same ones it hands to the real hand.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CardSelectCmd.FromHandForUpgrade))]
    public static void AfterFromHandForUpgrade(Player player, Task<CardModel?> __result)
    {
        Track(player, new LocString("gameplay_ui", "CHOOSE_CARD_UPGRADE_HEADER"), c => c.IsUpgradable, __result);
    }

    private static void Track(Player player, LocString prompt, Func<CardModel, bool>? filter, Task task)
    {
        try
        {
            if (LocalContext.IsMe(player) || task.IsCompleted || !SpectateController.IsActive)
            {
                return;
            }
            HandSelection.Begin(player, new HandSelection.Choice
            {
                Prompt = prompt.GetFormattedText(),
                Filter = filter
            });
            EndWhenDone(player, task);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to follow a teammate's hand selection: {e}");
        }
    }

    /// <summary>
    /// Awaited rather than ContinueWith'd: started on the main thread, the await resumes there too, and Godot nodes
    /// react to <see cref="HandSelection.Changed" />.
    /// </summary>
    private static async void EndWhenDone(Player player, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Cancelled or failed; the game deals with that. All we do is stop showing the choice.
        }
        HandSelection.End(player);
    }
}
