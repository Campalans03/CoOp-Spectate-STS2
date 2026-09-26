using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace SpectateMod.SpectateModCode.Patches;

/// <summary>
/// The game hard-wires "the local player" into how the board is drawn; these send those spots to
/// <see cref="SpectateBoard" /> so the watched player can stand in for us.
/// </summary>
[HarmonyPatch]
public static class BoardPatches
{
    /// <summary>
    /// Intent numbers are worked out against the local player. Work them out against the watched one instead, the same
    /// way, so their Vulnerable, block-ignoring powers and the like show in what we see coming at them.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AttackIntent), nameof(AttackIntent.GetSingleDamage))]
    public static bool BeforeGetSingleDamage(AttackIntent __instance, Creature owner, ref int __result)
    {
        Player? target = SpectateBoard.IntentTarget(owner.CombatState);
        if (target == null || __instance.DamageCalc == null)
        {
            return true;
        }
        try
        {
            decimal damage = Hook.ModifyDamage(target.RunState, target.Creature.CombatState, target.Creature, owner,
                __instance.DamageCalc(), ValueProp.Move, null, ModifyDamageHookType.All, CardPreviewMode.None,
                out IEnumerable<AbstractModel> _);
            __result = Math.Max(0, (int)damage);
            return false;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to work out an intent against the watched player: {e}");
            return true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCreature), nameof(NCreature.SetRemotePlayerFocused))]
    public static bool BeforeSetRemotePlayerFocused(bool remotePlayerFocused)
    {
        return !SpectateBoard.HandleRemoteFocus(remotePlayerFocused);
    }

    /// <summary>
    /// A growing or shrinking Osty walks to stay in front of its owner only when the owner is local. Let it do that for
    /// whoever stands in our spot, and not for ours while we are at the back.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCreature), nameof(NCreature.OstyScaleToSize))]
    public static bool BeforeOstyScaleToSize(NCreature __instance, float ostyHealth, double duration)
    {
        if (_inOstyScale || !SpectateBoard.IsLayoutSwapped)
        {
            return true;
        }
        _inOstyScale = true;
        try
        {
            SpectateBoard.AsLayoutOwner(() => __instance.OstyScaleToSize(ostyHealth, duration));
        }
        finally
        {
            _inOstyScale = false;
        }
        return false;
    }

    /// <summary>Set while the prefix above re-enters the method, so the second pass runs the game's code.</summary>
    private static bool _inOstyScale;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
    public static void AfterAddCreature(Creature creature)
    {
        SpectateBoard.OnCreatureAdded(creature);
    }
}
