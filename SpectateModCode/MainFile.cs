using System;
using System.Reflection;
using BaseLib.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using SpectateMod.SpectateModCode.Patches;

namespace SpectateMod.SpectateModCode;

//You're recommended but not required to keep all your code in this package and all your assets in the SpectateMod folder.
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "SpectateMod"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    /// <summary>
    /// The patches that keep a mirrored teammate's screen look-only: without them its own code would run for us, and
    /// its buttons would pick, buy or take things as if they were ours.
    /// </summary>
    private static readonly Type[] mirrorPatches =
    {
        typeof(MirrorScreenPatches), typeof(EventMirrorPatches), typeof(RewardButtonPatches),
        typeof(MerchantSlotPatches)
    };

    /// <summary>False when any of <see cref="mirrorPatches" /> failed to apply, which turns screen mirroring off.</summary>
    public static bool MirrorPatchesApplied { get; private set; } = true;

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();

        //If you want to use scripts defined in your mod for Godot scenes, uncomment the following line.
        //Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(assembly);

        ModConfigRegistry.Register(ModId, SpectateConfig.Instance);
        ModConfig.Load<SpectateConfig>();

        Harmony harmony = new(ModId);

        // One class at a time rather than PatchAll: PatchAll stops at the first patch that fails to apply (a game
        // update renaming a method, say) and leaves the rest undone. This way a broken patch only costs its own feature.
        foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to apply the patches in {type.Name}: {e}");
                if (Array.IndexOf(mirrorPatches, type) >= 0)
                {
                    MirrorPatchesApplied = false;
                }
            }
        }
    }
}
