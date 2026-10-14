using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpectateMod.SpectateModCode.Ui;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Gives the screens that lack it the "View Upgrades" box the forge and the deck view have: the card reward screen
/// and the draw, discard and exhaust piles in combat. It only changes which picture each card shows.
/// <para>
/// A card holder previews an upgrade by showing an upgraded copy of its card, and keeps its own card for everything
/// else. The reward screen picks by that card, and what goes over the network is only its place in the offer, so
/// what is ticked can't change what is taken.
/// </para>
/// </summary>
[HarmonyPatch]
public static class ViewUpgradesPatches
{
    private static readonly ConditionalWeakTable<NCardRewardSelectionScreen, NTickbox> rewardBoxes = new();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen._Ready))]
    public static void AfterRewardReady(NCardRewardSelectionScreen __instance)
    {
        // Postfixes run even when MirrorScreenPatches skipped the screen's own _Ready; the mirror has its own cards.
        if (!SpectateConfig.ViewUpgradesBox || MirrorScreenPatches.IsMirror(__instance))
        {
            return;
        }
        Safe.Run("add View Upgrades to the card reward screen", () =>
        {
            NTickbox? box = ViewUpgradesBox.AddTo(__instance, ticked => ShowRewardUpgrades(__instance, ticked));
            if (box != null)
            {
                rewardBoxes.AddOrUpdate(__instance, box);
            }
        });
    }

    /// <summary>New cards in the offer (a reroll, say) come in as plain cards; give them the upgrade view if it's on.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.RefreshOptions))]
    public static void AfterRefreshOptions(NCardRewardSelectionScreen __instance)
    {
        if (rewardBoxes.TryGetValue(__instance, out NTickbox? box) && GodotObject.IsInstanceValid(box) && box.IsTicked)
        {
            Safe.Run("show upgrades on new card rewards", () => ShowRewardUpgrades(__instance, true));
        }
    }

    /// <summary>
    /// The picked card flies into the deck from the reward screen, so put every card back to what it really is first:
    /// the upgraded picture flying off would look like an upgraded card was taken.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.SelectCard))]
    public static void BeforeSelectCard(NCardRewardSelectionScreen __instance)
    {
        if (!rewardBoxes.TryGetValue(__instance, out NTickbox? box) || !GodotObject.IsInstanceValid(box))
        {
            return;
        }
        Safe.Run("show the picked card as it is", () =>
        {
            box.IsTicked = false;
            ShowRewardUpgrades(__instance, false);
        });
    }

    private static void ShowRewardUpgrades(NCardRewardSelectionScreen screen, bool show)
    {
        if (!GodotObject.IsInstanceValid(screen) || screen._cardRow == null)
        {
            return;
        }
        foreach (NGridCardHolder holder in screen._cardRow.GetChildren().OfType<NGridCardHolder>())
        {
            if (!holder.IsQueuedForDeletion())
            {
                // A holder refuses to preview a card that has no upgrade.
                holder.SetIsPreviewingUpgrade(show && holder.CardModel.IsUpgradable);
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCardPileScreen), nameof(NCardPileScreen._Ready))]
    public static void AfterPileReady(NCardPileScreen __instance)
    {
        if (!SpectateConfig.ViewUpgradesBox)
        {
            return;
        }
        // The grid keeps the setting for the cards it lays out later, as the pile changes under it.
        Safe.Run("add View Upgrades to the pile screen", () => ViewUpgradesBox.AddTo(__instance, ticked =>
        {
            if (GodotObject.IsInstanceValid(__instance._grid))
            {
                __instance._grid.IsShowingUpgrades = ticked;
            }
        }));
    }
}
