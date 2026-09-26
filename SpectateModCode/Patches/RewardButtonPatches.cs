using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Rewards;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// The mirrored rewards screen is built from the game's own reward buttons, for their look and their tooltips. Clicking
/// one would ask the game to take a teammate's reward as ours, which it refuses by throwing; stop the click before it
/// gets that far. Our own buttons are untouched.
/// </summary>
[HarmonyPatch(typeof(NRewardButton))]
public static class RewardButtonPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("OnRelease")]
    public static bool BeforeRelease(NRewardButton __instance)
    {
        return __instance.Reward == null || LocalContext.IsMe(__instance.Reward.Player);
    }
}
