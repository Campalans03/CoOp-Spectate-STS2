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
    /// <para>
    /// Every attack intent on screen comes through here, spectating or not, so any failure falls back to the game's
    /// own sum: an attack intent that throws is drawn with no picture and no number.
    /// </para>
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AttackIntent), nameof(AttackIntent.GetSingleDamage))]
    public static bool BeforeGetSingleDamage(AttackIntent __instance, Creature owner, ref int __result)
    {
        int? damage = Safe.Run("work out an intent against the watched player",
            () => DamageToWatched(__instance, owner), null);
        if (damage == null)
        {
            return true;
        }
        __result = damage.Value;
        return false;
    }

    /// <summary>The intent's damage against the watched player, or null to leave it to the game.</summary>
    private static int? DamageToWatched(AttackIntent intent, Creature owner)
    {
        Player? target = SpectateBoard.IntentTarget(owner.CombatState);
        if (target == null || intent.DamageCalc == null)
        {
            return null;
        }
        decimal damage = Hook.ModifyDamage(target.RunState, target.Creature.CombatState, target.Creature, owner,
            intent.DamageCalc(), ValueProp.Move, null, null, ModifyDamageHookType.All, CardPreviewMode.None,
            out IEnumerable<AbstractModel> _);
        return Math.Max(0, (int)damage);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCreature), nameof(NCreature.SetRemotePlayerFocused))]
    public static bool BeforeSetRemotePlayerFocused(bool remotePlayerFocused)
    {
        return !Safe.Run("keep the watched player's health bar up",
            () => SpectateBoard.HandleRemoteFocus(remotePlayerFocused), false);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
    public static void AfterAddCreature(Creature creature)
    {
        SpectateBoard.OnCreatureAdded(creature);
    }
}
