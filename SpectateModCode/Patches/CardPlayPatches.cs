using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// A teammate's played card gets a card of its own on our screen, which the game sets off from their character, since
/// their hand is nowhere on it. While we are showing their hand, that card should leave from it instead.
/// </summary>
[HarmonyPatch]
public static class CardPlayPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.CreateCardNodeAndUpdateVisuals))]
    public static void AfterCreateCardNode(CardModel card, PileType targetPileType, bool owningPlayerIsLocal,
        NCard __result)
    {
        if (owningPlayerIsLocal || targetPileType != PileType.Play || __result == null)
        {
            return;
        }
        Safe.Run("start a teammate's played card from their hand",
            () => SpectateView.MirroredHand?.HandOff(card, __result));
    }
}
