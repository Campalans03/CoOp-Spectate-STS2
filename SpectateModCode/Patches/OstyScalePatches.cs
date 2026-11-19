using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Kept apart from <see cref="BoardPatches" /> so a game update that renames or removes the method only costs this
/// feature, not every board patch.
/// </summary>
[HarmonyPatch]
public static class OstyScalePatches
{
    /// <summary>Skips the patch quietly when the game no longer has the method.</summary>
    public static bool Prepare(MethodBase? original)
    {
        if (original != null || AccessTools.Method(typeof(NCreature), "ScaleAndPositionOsty") != null)
        {
            return true;
        }
        MainFile.Logger.Warn("NCreature.ScaleAndPositionOsty not found; Osty keeps the game's placement while spectating.");
        return false;
    }

    /// <summary>
    /// A growing or shrinking Osty walks to stay in front of its owner only when the owner is local. Let it do that for
    /// whoever stands in our spot, and not for ours while we are at the back.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCreature), "ScaleAndPositionOsty")]
    public static bool BeforeScaleAndPositionOsty(NCreature __instance, float ostyHealth, double duration)
    {
        if (_inOstyScale || !SpectateBoard.IsLayoutSwapped)
        {
            return true;
        }
        _inOstyScale = true;
        try
        {
            SpectateBoard.AsLayoutOwner(() => __instance.ScaleAndPositionOsty(ostyHealth, duration));
        }
        finally
        {
            _inOstyScale = false;
        }
        return false;
    }

    /// <summary>Set while the prefix above re-enters the method, so the second pass runs the game's code.</summary>
    private static bool _inOstyScale;
}
