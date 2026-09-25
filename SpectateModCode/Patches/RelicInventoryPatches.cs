using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// The relic row is written for our own relics only. These cover the places where that assumption breaks while
/// <see cref="RelicView" /> has it showing a teammate's.
/// </summary>
[HarmonyPatch(typeof(NRelicInventory))]
public static class RelicInventoryPatches
{
    /// <summary>
    /// Hovering a relic tells every peer "I am hovering relic #n". The index is resolved against the sender's own
    /// relics, so broadcasting the index of a teammate's relic would point the others at the wrong one of ours, or at
    /// one we don't have. Keep hovers of other people's relics to ourselves.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("OnRelicFocused")]
    public static bool BeforeRelicFocused(RelicModel model)
    {
        return LocalContext.IsMine(model);
    }

    /// <summary>
    /// The stock Remove ignores anything that isn't ours, so a teammate losing a relic would leave it on the row.
    /// While showing theirs, remove by their ownership instead.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("Remove")]
    public static bool BeforeRemove(NRelicInventory __instance, RelicModel relic)
    {
        if (!RelicView.IsShowingOther(__instance))
        {
            return true;
        }
        NRelicInventoryHolder? holder = __instance._relicNodes.FirstOrDefault(n => n.Relic.Model == relic);
        if (holder != null)
        {
            __instance._relicNodes.Remove(holder);
            __instance.RemoveChildSafely(holder);
            holder.QueueFreeSafely();
            __instance.EmitSignalRelicsChanged();
            if (__instance._relicNodes.Count > 0)
            {
                __instance.UpdateNavigation();
            }
        }
        return false;
    }

    /// <summary>
    /// Plays the "just picked this up" flight for one of our relics, looking it up with First(). While the row shows
    /// a teammate's relics ours isn't on it, and First() would throw. It is rebuilt with the new relic when we come
    /// back, so skipping the animation is all we lose.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(NRelicInventory.AnimateRelic))]
    public static bool BeforeAnimateRelic(NRelicInventory __instance, RelicModel relic)
    {
        return __instance._relicNodes.Any(n => n.Relic.Model == relic);
    }
}
