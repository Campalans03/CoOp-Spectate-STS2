using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Potions;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// A read-only copy of a teammate's potion belt, laid over ours in the top bar.
/// <para>
/// It deliberately does not rebind <see cref="NPotionContainer" /> the way the relic row is rebound: its holders are
/// usable, and clicking one would try to drink the teammate's potion on their behalf. Holders built with
/// <c>isUsable: false</c> still bounce and show their tooltip, but do nothing on click. They also have no hover
/// broadcast, which the real container wires up itself - and which would send the others the slot index of a potion
/// that isn't ours.
/// </para>
/// <para>
/// Our real belt stays in place underneath, only hidden, so it keeps tracking our own potions the whole time.
/// </para>
/// </summary>
public partial class NSpectatePotionBelt : HBoxContainer
{
    /// <summary>Where NPotionContainer.Add puts a potion inside its holder.</summary>
    private static readonly Vector2 potionOffset = new(-30f, -30f);

    private readonly List<NPotionHolder> _holders = new();
    private Player? _player;
    private Control? _realHolders;

    /// <summary>
    /// Shows <paramref name="player" />'s belt in place of ours; null puts ours back. Leaves
    /// <paramref name="belt" /> null if the top bar isn't there to hook into.
    /// </summary>
    public static void Show(ref NSpectatePotionBelt? belt, Player? player)
    {
        if (player == null)
        {
            belt?.Close();
            belt = null;
            return;
        }

        if (belt == null || !GodotObject.IsInstanceValid(belt))
        {
            belt = Open();
            if (belt == null)
            {
                return;
            }
        }
        belt.Bind(player);
    }

    private static NSpectatePotionBelt? Open()
    {
        NPotionContainer? container = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer;
        Control? realHolders = container?._potionHolders;
        if (realHolders == null || !GodotObject.IsInstanceValid(realHolders) || realHolders.GetParent() is not Control slot)
        {
            return null;
        }

        NSpectatePotionBelt belt = new()
        {
            Name = "SpectatePotionBelt",
            MouseFilter = MouseFilterEnum.Ignore
        };
        belt._realHolders = realHolders;
        // Match the real row's spacing, so the copy lines up slot for slot with the belt it covers.
        if (realHolders is BoxContainer box)
        {
            belt.AddThemeConstantOverride("separation", box.GetThemeConstant("separation"));
            belt.Alignment = box.Alignment;
        }
        // The real row sits in a MarginContainer, which stacks its children on the same rect: a sibling lands exactly
        // where the real one is.
        slot.AddChildSafely(belt);
        realHolders.Visible = false;
        return belt;
    }

    private void Close()
    {
        Unbind();
        if (_realHolders != null && GodotObject.IsInstanceValid(_realHolders))
        {
            _realHolders.Visible = true;
        }
        if (GodotObject.IsInstanceValid(this))
        {
            this.QueueFreeSafely();
        }
    }

    private void Bind(Player player)
    {
        if (player == _player)
        {
            return;
        }
        Unbind();
        _player = player;
        // Raised from inside the teammate's potion actions; see Safe.
        player.PotionProcured += SafePotionProcured;
        player.UsedPotionRemoved += SafeUsedPotionRemoved;
        player.PotionDiscarded += SafePotionDiscarded;
        player.MaxPotionCountChanged += SafeMaxPotionCountChanged;
        Rebuild();
    }

    private void Unbind()
    {
        if (_player != null)
        {
            _player.PotionProcured -= SafePotionProcured;
            _player.UsedPotionRemoved -= SafeUsedPotionRemoved;
            _player.PotionDiscarded -= SafePotionDiscarded;
            _player.MaxPotionCountChanged -= SafeMaxPotionCountChanged;
            _player = null;
        }
        ClearHolders();
    }

    /// <summary>Out of the row straight away, so the slots don't linger for a frame next to their replacements.</summary>
    private void ClearHolders()
    {
        foreach (NPotionHolder holder in _holders)
        {
            if (GodotObject.IsInstanceValid(holder))
            {
                this.RemoveChildSafely(holder);
                holder.QueueFreeSafely();
            }
        }
        _holders.Clear();
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        Unbind();
    }

    private void Rebuild()
    {
        ClearHolders();
        if (_player == null)
        {
            return;
        }

        for (int i = 0; i < _player.MaxPotionCount; i++)
        {
            NPotionHolder holder = NPotionHolder.Create(isUsable: false);
            _holders.Add(holder);
            // In the tree before a potion goes in: AddPotion needs the empty-slot icon that _Ready looks up.
            this.AddChildSafely(holder);
        }

        IReadOnlyList<PotionModel?> slots = _player.PotionSlots;
        for (int i = 0; i < slots.Count && i < _holders.Count; i++)
        {
            if (slots[i] is { } potion)
            {
                Fill(_holders[i], potion);
            }
        }
    }

    private static void Fill(NPotionHolder holder, PotionModel potion)
    {
        if (holder.HasPotion)
        {
            return;
        }
        NPotion node = NPotion.Create(potion);
        node.Position = potionOffset;
        holder.AddPotion(node);
    }

    private NPotionHolder? HolderOf(PotionModel potion)
    {
        return _holders.FirstOrDefault(h => h.Potion != null && h.Potion.Model == potion);
    }

    private void SafePotionProcured(PotionModel potion) =>
        Safe.Run("mirror a teammate's new potion", () => OnPotionProcured(potion));

    private void SafeUsedPotionRemoved(PotionModel potion) =>
        Safe.Run("mirror a teammate drinking a potion", () => OnUsedPotionRemoved(potion));

    private void SafePotionDiscarded(PotionModel potion) =>
        Safe.Run("mirror a teammate discarding a potion", () => OnPotionDiscarded(potion));

    private void SafeMaxPotionCountChanged(int count) =>
        Safe.Run("mirror a teammate's potion slots", () => OnMaxPotionCountChanged(count));

    private void OnPotionProcured(PotionModel potion)
    {
        int index = -1;
        IReadOnlyList<PotionModel?> slots = _player?.PotionSlots ?? [];
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] == potion)
            {
                index = i;
                break;
            }
        }
        if (index < 0 || index >= _holders.Count)
        {
            Rebuild();
            return;
        }
        Fill(_holders[index], potion);
    }

    /// <summary>Uses the holder's own shrink-away, so we see them drink it.</summary>
    private void OnUsedPotionRemoved(PotionModel potion)
    {
        NPotionHolder? holder = HolderOf(potion);
        if (holder == null)
        {
            Rebuild();
            return;
        }
        holder.RemoveUsedPotion();
    }

    private void OnPotionDiscarded(PotionModel potion)
    {
        NPotionHolder? holder = HolderOf(potion);
        if (holder == null)
        {
            Rebuild();
            return;
        }
        holder.DiscardPotion();
    }

    private void OnMaxPotionCountChanged(int _)
    {
        Rebuild();
    }
}
