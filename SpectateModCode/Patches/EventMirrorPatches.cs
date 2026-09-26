using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// What the mirrored event screen (see <see cref="Ui.NSpectateScreenMirror" />) needs from the game's event nodes.
/// </summary>
[HarmonyPatch]
public static class EventMirrorPatches
{
    /// <summary>
    /// An option button hands its click to the event room we are standing in, by index, so a click on a teammate's
    /// option would pick <i>our</i> option at that index. Only the owner's own buttons may go through.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NEventOptionButton), "OnRelease")]
    public static bool BeforeOptionRelease(NEventOptionButton __instance)
    {
        return __instance.Event?.Owner == null || LocalContext.IsMe(__instance.Event.Owner);
    }

    /// <summary>
    /// An ancient's layout stops the ancient's ambient loop on the way out. We are standing in the same event, so for a
    /// mirrored copy that would silence our own. Do everything else it does (its own unhooking and its base class's),
    /// but leave the music playing.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NAncientEventLayout), nameof(NAncientEventLayout._ExitTree))]
    public static bool BeforeAncientExitTree(NAncientEventLayout __instance)
    {
        if (!MirrorScreenPatches.IsMirror(__instance))
        {
            return true;
        }
        __instance._cts.Cancel();
        ActiveScreenContext.Instance.Updated -= __instance.UpdateBannerVisibility;
        NControllerManager.Instance.Disconnect(NControllerManager.SignalName.MouseDetected,
            Callable.From(__instance.UpdateHotkeyDisplay));
        NControllerManager.Instance.Disconnect(NControllerManager.SignalName.ControllerDetected,
            Callable.From(__instance.UpdateHotkeyDisplay));
        NInputManager.Instance.Disconnect(NInputManager.SignalName.InputRebound,
            Callable.From(__instance.UpdateHotkeyDisplay));
        RunManager.Instance.EventSynchronizer.PlayerVoteChanged -= __instance.OnPlayerVoteChanged;
        NGame.Instance?.Disconnect(NGame.SignalName.PhobiaModeToggled, Callable.From(__instance.UpdatePhobiaMode));
        return false;
    }
}
