using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Ui;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Ties the run-wide half of the mod to the run's global UI: it is built when a run's UI is and goes away with it, so
/// spectating lasts from the first room to the last.
/// </summary>
[HarmonyPatch(typeof(NGlobalUi))]
public static class GlobalUiPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(NGlobalUi.Initialize))]
    public static void AfterInitialize(NGlobalUi __instance, RunState runState)
    {
        try
        {
            __instance.GetNodeOrNull(NSpectateRunUi.NodeName)?.QueueFreeSafely();
            if (runState.Players.Count <= 1)
            {
                // Singleplayer (or a solo lobby): there is nobody to watch, so stay completely out of the way.
                return;
            }
            __instance.AddChildSafely(NSpectateRunUi.Create(runState));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to set up spectating for this run: {e}");
        }
    }
}
