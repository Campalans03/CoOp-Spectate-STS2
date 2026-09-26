using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// The mirror shows a teammate's screen with the game's own scene for it, so it looks exactly like theirs and their
/// cursor lands where it does for them. What it must not have is the screen's own code: that runs the choice for the
/// local player, grabs focus, plays sounds and talks to the overlay stack. So a mirrored instance is registered here
/// and its lifecycle methods are skipped, leaving the nodes the scene is made of, which the mirror fills in by hand.
/// <para>
/// Child nodes (banners, buttons) keep their own scripts: they are self-contained widgets, and their clicks go to
/// handlers the skipped _Ready would have connected, so they do nothing.
/// </para>
/// </summary>
[HarmonyPatch]
public static class MirrorScreenPatches
{
    private static readonly ConditionalWeakTable<Node, object> mirrors = new();

    public static void Register(Node screen)
    {
        mirrors.AddOrUpdate(screen, screen);
    }

    private static bool IsLive(Node instance)
    {
        return !mirrors.TryGetValue(instance, out _);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen._Ready))]
    public static bool RewardsReady(NRewardsScreen __instance) => IsLive(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen._Process))]
    public static bool RewardsProcess(NRewardsScreen __instance) => IsLive(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen._GuiInput))]
    public static bool RewardsGuiInput(NRewardsScreen __instance) => IsLive(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen._EnterTree))]
    public static bool CardRewardEnterTree(NCardRewardSelectionScreen __instance) => IsLive(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen._Ready))]
    public static bool CardRewardReady(NCardRewardSelectionScreen __instance) => IsLive(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen._ExitTree))]
    public static bool CardRewardExitTree(NCardRewardSelectionScreen __instance) => IsLive(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen._Ready))]
    public static bool ChooseACardReady(NChooseACardSelectionScreen __instance) => IsLive(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen._ExitTree))]
    public static bool ChooseACardExitTree(NChooseACardSelectionScreen __instance) => IsLive(__instance);
}
