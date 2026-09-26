using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Ties the mod's lifetime to the combat UI: it only exists while a combat screen does, and it always tears itself
/// down again, including when combat is left through an error path.
/// </summary>
[HarmonyPatch(typeof(NCombatUi))]
public static class CombatUiPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(NCombatUi.Activate))]
    public static void AfterActivate(NCombatUi __instance, CombatState state)
    {
        try
        {
            SpectateController.AttachToCombat(state);
            SpectateView.Install(__instance, state);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to set up spectating for this combat: {e}");
            Teardown();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NCombatUi.Deactivate))]
    public static void AfterDeactivate()
    {
        Teardown();
    }

    [HarmonyPrefix]
    [HarmonyPatch("_ExitTree")]
    public static void BeforeExitTree()
    {
        Teardown();
    }

    /// <summary>
    /// Detach before uninstall: detaching ends an auto-picked spectate while the view is still listening, so the
    /// relics and potions follow. A teammate picked by hand stays picked, so the uninstall puts our own hand and
    /// combat UI back by itself either way.
    /// </summary>
    private static void Teardown()
    {
        try
        {
            SpectateController.DetachFromCombat();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to detach the spectate controller: {e}");
        }

        try
        {
            SpectateView.Uninstall();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to tear down the spectate view: {e}");
        }
    }
}
