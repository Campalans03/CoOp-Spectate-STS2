using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using SpectateMod.SpectateModCode.Ui;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// Adds the spectate eye to each row of the party list in the top-left corner.
/// </summary>
[HarmonyPatch(typeof(NMultiplayerPlayerState))]
public static class PlayerStatePatches
{
    /// <summary>
    /// Runs after the panel's own _Ready, which is where Player and the character icon are resolved. The panel lives
    /// for the whole run rather than per combat, so the button decides for itself when it is useful.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch("_Ready")]
    public static void AfterReady(NMultiplayerPlayerState __instance)
    {
        try
        {
            if (__instance.Player == null)
            {
                return;
            }
            NSpectateToggleButton button = NSpectateToggleButton.Create(__instance.Player);
            button.AttachTo(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to add the spectate button to the party list: {e}");
        }
    }
}
