using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Points the top bar's relic row at somebody else's relics and back.
/// <para>
/// Unlike the combat UI this row belongs to the run, not to the combat, so it outlives every combat screen and has to
/// be handed back to us whenever spectating ends, whatever the reason. <see cref="Patches.RelicInventoryPatches" />
/// covers the spots where the game assumes the row only ever holds our own relics.
/// </para>
/// </summary>
public static class RelicView
{
    private static NRelicInventory? Inventory => NRun.Instance?.GlobalUi?.RelicInventory;

    /// <summary>True while the row is showing a teammate's relics rather than ours.</summary>
    public static bool IsShowingOther(NRelicInventory inventory)
    {
        return inventory._player != null && !LocalContext.IsMe(inventory._player);
    }

    /// <summary>Fills the row with <paramref name="owner" />'s relics. Pass our own player to put ours back.</summary>
    public static void Show(Player owner)
    {
        NRelicInventory? inventory = Inventory;
        if (inventory == null || !GodotObject.IsInstanceValid(inventory) || inventory._player == null
            || owner == inventory._player)
        {
            return;
        }

        // What Initialize does, minus the lookup that always lands on us: drop the old row, then build one per relic
        // of the new owner and listen to them instead.
        inventory.DisconnectPlayerEvents();
        foreach (NRelicInventoryHolder holder in inventory._relicNodes.ToList())
        {
            inventory.RemoveChildSafely(holder);
            holder.QueueFreeSafely();
        }
        inventory._relicNodes.Clear();

        inventory._player = owner;
        inventory.ConnectPlayerEvents();
        foreach (RelicModel relic in owner.Relics)
        {
            inventory.Add(relic, startsShown: true);
        }
        if (inventory._relicNodes.Count > 0)
        {
            inventory.UpdateNavigation();
        }
    }
}
