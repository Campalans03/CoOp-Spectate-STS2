using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// The game only ever shows what an enemy holds against us: the copy of a per-player power aimed at us, the card a
/// Thieving Hopper took from us. These send those spots to <see cref="SpectateBoard" /> so the watched player's show
/// instead. Kept apart from <see cref="BoardPatches" /> so that one failing to apply costs only this.
/// </summary>
[HarmonyPatch]
public static class EnemyPowerPatches
{
    /// <summary>
    /// An enemy's power aimed at one player only goes up on its bar when that player is us; while watching somebody,
    /// put up theirs and leave ours off. Raised from inside the game applying the power, so any failure leaves it to
    /// the game.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NPowerContainer), nameof(NPowerContainer.Add))]
    public static bool BeforePowerContainerAdd(NPowerContainer __instance, PowerModel power)
    {
        bool? shown = Safe.Run("decide whether an enemy power aimed at a player shows",
            () => SpectateBoard.IsPowerShown(power), null);
        if (shown == null)
        {
            return true;
        }
        if (shown.Value)
        {
            Safe.Run("put up an enemy power aimed at the watched player",
                () => SpectateBoard.AddPowerNode(__instance, power));
        }
        return false;
    }

    /// <summary>
    /// A power not aimed at us describes itself as nothing at all, so hovering the watched player's would show no
    /// tip. Describe it as they see it.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.HoverTips), MethodType.Getter)]
    public static bool BeforePowerHoverTips(PowerModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (_inPowerHoverTips)
        {
            return true;
        }
        Player? viewer = Safe.Run("find who an enemy power is aimed at", () => SpectateBoard.PowerViewer(__instance),
            null);
        if (viewer == null)
        {
            return true;
        }
        _inPowerHoverTips = true;
        try
        {
            IEnumerable<IHoverTip>? tips = Safe.Run("describe an enemy power aimed at the watched player",
                () => SpectateBoard.AsLocal(viewer, () => __instance.HoverTips), null);
            if (tips == null)
            {
                return true;
            }
            __result = tips;
            return false;
        }
        finally
        {
            _inPowerHoverTips = false;
        }
    }

    /// <summary>Set while the prefix above re-enters the getter, so the second pass runs the game's code.</summary>
    private static bool _inPowerHoverTips;

    /// <summary>
    /// A Thieving Hopper has made off with somebody's card. The game has already put ours in its hand if it was ours;
    /// make sure it holds up the watched player's instead.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCreature), nameof(NCreature.OnPowerApplied))]
    public static void AfterCreaturePowerApplied(NCreature __instance, PowerModel power)
    {
        if (power is SwipePower)
        {
            Safe.Run("show the card a Thieving Hopper stole from the watched player",
                () => SpectateBoard.ShowStolenCard(__instance));
        }
    }
}
