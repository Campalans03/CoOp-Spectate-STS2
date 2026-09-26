using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// The mirrored merchant rug is the game's own, filled with a teammate's stock, so its slots would try to buy for them
/// when clicked. Stop that before it starts; our own rug is untouched. Right-click previews still work.
/// </summary>
[HarmonyPatch(typeof(NMerchantSlot))]
public static class MerchantSlotPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("OnSelected")]
    public static bool BeforeSelected(NMerchantSlot __instance, ref Task __result)
    {
        if (__instance.Player == null || LocalContext.IsMe(__instance.Player))
        {
            return true;
        }
        __result = Task.CompletedTask;
        return false;
    }
}
