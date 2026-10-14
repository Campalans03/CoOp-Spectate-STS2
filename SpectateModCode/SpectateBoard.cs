using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Makes the combat board itself look like the watched player's: their health bar (and their pets', like Osty) stays up
/// the way ours normally does, they step into our spot at the front while we move back among the others, enemy
/// intents show the damage they would take, and enemies show what they hold against them rather than against us.
/// Everything is local and cosmetic, and handing the board back to ourselves undoes it.
/// </summary>
public static class SpectateBoard
{
    private const double moveDuration = 0.45;

    private static CombatState? _state;
    private static Player? _localPlayer;
    private static Tween? _moveTween;

    /// <summary>Whose health bars are shown as "ours". Null while nothing is installed.</summary>
    private static Player? _barOwner;

    /// <summary>
    /// Whose share of the enemies' per-player powers is on show (see <see cref="PowerViewer" />). Null for ours, which
    /// is the game's own rule.
    /// </summary>
    private static Player? _powersAimedAt;

    /// <summary>Marks the stolen cards we put in a Thieving Hopper's hand, as opposed to the one the game put there.</summary>
    private static readonly StringName stolenCardMeta = "spectate_stolen_card";

    /// <summary>Who stands in our spot at the front of the board. Null while nothing is installed.</summary>
    public static Player? LayoutOwner { get; private set; }

    /// <summary>True while somebody other than us is standing in our spot.</summary>
    public static bool IsLayoutSwapped => LayoutOwner != null && LayoutOwner != _localPlayer;

    public static void Install(CombatState state)
    {
        Uninstall();
        if (state.Players.Count <= 1)
        {
            return;
        }
        _state = state;
        _localPlayer = SpectateController.Me;
        _barOwner = _localPlayer;
        LayoutOwner = _localPlayer;
        SpectateController.ViewChanged += OnViewChanged;

        // Apply whatever is on show already, without making the board slide in.
        Apply(SpectateController.Viewed, animate: false);
    }

    public static void Uninstall()
    {
        if (_state == null)
        {
            return;
        }
        SpectateController.ViewChanged -= OnViewChanged;
        try
        {
            // The room outlives the combat UI when a combat is won, so the board slides back; when the room itself is
            // leaving there is nothing left to put back.
            NCombatRoom? room = NCombatRoom.Instance;
            if (room != null && GodotObject.IsInstanceValid(room) && room.IsInsideTree())
            {
                Apply(null, animate: true);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to hand the combat board back: {e}");
        }
        _moveTween = null;
        _state = null;
        _localPlayer = null;
        _barOwner = null;
        _powersAimedAt = null;
        LayoutOwner = null;
    }

    /// <summary>
    /// The player enemy intents are worked out against, or null to leave the game's own sums (against us) alone.
    /// </summary>
    public static Player? IntentTarget(ICombatState? combatState)
    {
        if (_state == null || !SpectateConfig.IntentsForWatched || combatState != _state)
        {
            return null;
        }
        Player? viewed = SpectateController.Viewed;
        return viewed?.Creature is { IsAlive: true, CombatState: not null } ? viewed : null;
    }

    /// <summary>
    /// The player to look at an enemy's power as, or null to leave it to the game. A few enemies keep one copy of a
    /// power per player, each aimed at that player (The Insatiable's countdown, the card a Thieving Hopper or a Gremlin
    /// Merc made off with), and the game only ever shows the copy aimed at us. While watching somebody, theirs shows.
    /// </summary>
    public static Player? PowerViewer(PowerModel power)
    {
        if (_state == null || _powersAimedAt == null || power.Target?.Player == null || power.Owner is not { IsEnemy: true })
        {
            return null;
        }
        return _powersAimedAt;
    }

    /// <summary>Whether an enemy's power goes up on its bar, or null when the game's own rule applies.</summary>
    public static bool? IsPowerShown(PowerModel power)
    {
        Player? viewer = PowerViewer(power);
        return viewer == null ? null : AsLocal(viewer, () => power.IsVisible);
    }

    /// <summary>What NPowerContainer.Add does once it has decided that the power shows.</summary>
    public static void AddPowerNode(NPowerContainer container, PowerModel power)
    {
        NPower node = NPower.Create(power);
        node.Container = container;
        container._powerNodes.Add(node);
        container.AddChildSafely(node);
        container.UpdatePositions();
    }

    /// <summary>
    /// Runs <paramref name="action" /> with the game believing <see cref="LayoutOwner" /> is the local player, for the
    /// few synchronous layout routines that place "me" differently from everyone else. Only ever used for positioning.
    /// </summary>
    public static void AsLayoutOwner(Action action)
    {
        if (IsLayoutSwapped)
        {
            AsLocal(LayoutOwner!, () =>
            {
                action();
                return true;
            });
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// Runs <paramref name="func" /> with the game believing <paramref name="player" /> is the local player, for the
    /// synchronous routines that only ever place or show "my" copy of something. The id is put back before anything
    /// else can run.
    /// </summary>
    public static T AsLocal<T>(Player player, Func<T> func)
    {
        ulong? realId = LocalContext.NetId;
        try
        {
            LocalContext.NetId = player.NetId;
            return func();
        }
        finally
        {
            LocalContext.NetId = realId;
        }
    }

    /// <summary>
    /// The game hides our own health bar while the mouse is on a teammate so the two don't overlap. While the board
    /// belongs to someone else, it is their bar that steps aside. Returns false when the game's own handling applies.
    /// </summary>
    public static bool HandleRemoteFocus(bool remotePlayerFocused)
    {
        if (_barOwner == null || _barOwner == _localPlayer)
        {
            return false;
        }
        NCreature? node = NCombatRoom.Instance?.GetCreatureNode(_barOwner.Creature);
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            return true;
        }
        if (remotePlayerFocused)
        {
            node._stateDisplay.AnimateOut();
        }
        else if (node.Entity.IsAlive)
        {
            node._stateDisplay.AnimateIn(HealthBarAnimMode.FromHidden);
        }
        return true;
    }

    /// <summary>A pet or player joined mid-combat; give it the current board's rules.</summary>
    public static void OnCreatureAdded(Creature creature)
    {
        if (_state == null || (creature.PetOwner == null && !creature.IsPlayer))
        {
            return;
        }
        try
        {
            if (_barOwner != null && _barOwner != _localPlayer)
            {
                ApplyBars(_barOwner);
            }
            if (IsLayoutSwapped)
            {
                ApplyLayout(animate: true);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to fit a new creature into the spectated board: {e}");
        }
    }

    private static void OnViewChanged(Player? viewed)
    {
        try
        {
            Apply(viewed, animate: true);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to apply the spectated board: {e}");
        }
    }

    private static void Apply(Player? viewed, bool animate)
    {
        if (_localPlayer == null || NCombatRoom.Instance is not { } room || !GodotObject.IsInstanceValid(room))
        {
            return;
        }
        Player? onShow = viewed != null && viewed.Creature != null && GetNode(viewed.Creature) != null ? viewed : null;

        Player barOwner = SpectateConfig.ReplaceHealthBars ? onShow ?? _localPlayer : _localPlayer;
        if (barOwner != _barOwner)
        {
            _barOwner = barOwner;
            ApplyBars(barOwner);
        }

        Player layoutOwner = SpectateConfig.SwapCombatPositions ? onShow ?? _localPlayer : _localPlayer;
        if (layoutOwner != LayoutOwner)
        {
            LayoutOwner = layoutOwner;
            ApplyLayout(animate);
        }

        RefreshIntents();

        Player? powersAimedAt = SpectateConfig.EnemyPowersForWatched ? viewed : null;
        if (powersAimedAt != _powersAimedAt)
        {
            _powersAimedAt = powersAimedAt;
            // Each on its own: a failure in either only costs that one, not the rest of the board.
            Safe.Run("show the enemy powers aimed at the watched player", RefreshEnemyPowers);
            Safe.Run("show the cards stolen from the watched player", () =>
            {
                foreach (NCreature creature in room.CreatureNodes)
                {
                    ShowStolenCard(creature);
                }
            });
        }
    }

    /// <summary>Players and their pets, in the order the room made them.</summary>
    private static List<NCreature> AllyNodes()
    {
        List<NCreature> nodes = NCombatRoom.Instance!.CreatureNodes
            .Where(n => GodotObject.IsInstanceValid(n) && (n.Entity.IsPlayer || n.Entity.PetOwner != null))
            .ToList();
        // The layout routine files every pet under its owner's node, and throws for a pet whose owner isn't there.
        HashSet<Player> owners = nodes.Where(n => n.Entity.IsPlayer).Select(n => n.Entity.Player!).ToHashSet();
        return nodes.Where(n => n.Entity.IsPlayer || owners.Contains(n.Entity.PetOwner!)).ToList();
    }

    private static NCreature? GetNode(Creature creature) => NCombatRoom.Instance?.GetCreatureNode(creature);

    /// <summary>
    /// Flips which creatures count as "remote" to the health bar code: the board owner's bar and their pets' stay up,
    /// everyone else's only shows on hover. A creature under the mouse is left for its unfocus to settle.
    /// </summary>
    private static void ApplyBars(Player barOwner)
    {
        foreach (NCreature node in AllyNodes())
        {
            Player? owner = node.Entity.Player ?? node.Entity.PetOwner;
            bool remote = owner != barOwner;
            if (remote == node._isRemotePlayerOrPet)
            {
                continue;
            }
            node._isRemotePlayerOrPet = remote;
            if (node.IsFocused || !node.IsNodeReady())
            {
                continue;
            }
            if (remote)
            {
                node._stateDisplay.AnimateOut();
            }
            // Some pets never get a bar, like the immortal Byrdpip; AnimateIn would show theirs anyway. Not judged by
            // IsInteractable: the game makes every pet of a teammate non-interactable, Osty included, and theirs does
            // get a bar while they are the one on show.
            else if (node.Entity.IsAlive && node.Entity.Monster is not { IsHealthBarVisible: false })
            {
                node._stateDisplay.AnimateIn(HealthBarAnimMode.FromHidden);
            }
        }
    }

    /// <summary>
    /// Lets the game's own layout place everyone as if <see cref="LayoutOwner" /> were us, reads off where it put them,
    /// and slides them there from where they stand. Only the tint's colour is taken from the layout (the back row is
    /// dimmed), never its alpha, which death and summon animations own.
    /// </summary>
    private static void ApplyLayout(bool animate)
    {
        NCombatRoom room = NCombatRoom.Instance!;
        List<NCreature> nodes = AllyNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        var before = nodes.ToDictionary(n => n, n => (n.Position, n.Visuals.Modulate, n.Visuals.Bounds.Visible));
        foreach (NCreature node in nodes)
        {
            // The layout only ever darkens the back row and never lightens the front, so start everyone from full colour.
            Color tint = node.Visuals.Modulate;
            node.Visuals.Modulate = new Color(1f, 1f, 1f, tint.A);
        }

        float scaling = room._visuals.Encounter.GetCameraScaling();
        bool center = room._visuals.Encounter.FullyCenterPlayers;
        AsLayoutOwner(() => NCombatRoom.PositionPlayersAndPets(nodes, scaling, center));
        PlaceFrontOsty(nodes);

        _moveTween?.Kill();
        _moveTween = animate && room.IsInsideTree() ? room.CreateTween().SetParallel() : null;
        foreach (NCreature node in nodes)
        {
            (Vector2 position, Color tint, bool boundsVisible) = before[node];
            Vector2 targetPosition = node.Position;
            Color layoutTint = node.Visuals.Modulate;
            Color targetTint = new(layoutTint.R, layoutTint.G, layoutTint.B, tint.A);
            // Hitbox outlines of pets follow their owner being local, which is ours to keep, not the layout's.
            node.Visuals.Bounds.Visible = boundsVisible;

            if (_moveTween == null)
            {
                node.Position = targetPosition;
                node.Visuals.Modulate = targetTint;
                continue;
            }
            node.Position = position;
            node.Visuals.Modulate = tint;
            _moveTween.TweenProperty(node, "position", targetPosition, moveDuration)
                .SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Cubic);
            _moveTween.TweenProperty(node.Visuals, "modulate:r", targetTint.R, moveDuration);
            _moveTween.TweenProperty(node.Visuals, "modulate:g", targetTint.G, moveDuration);
            _moveTween.TweenProperty(node.Visuals, "modulate:b", targetTint.B, moveDuration);
        }
    }

    /// <summary>
    /// The combat-start layout puts the front player's Osty half a body plus 150px further out than
    /// <see cref="NCreature.OstyScaleToSize" /> does, and it is the latter that places Osty in play: every summon moves
    /// it there. Use that spot so Osty lands where it would stand if its owner were playing, not out in the enemies.
    /// </summary>
    private static void PlaceFrontOsty(List<NCreature> nodes)
    {
        NCreature? owner = nodes.FirstOrDefault(n => n.Entity.Player == LayoutOwner);
        NCreature? osty = nodes.FirstOrDefault(n => n.Entity.Monster is Osty && n.Entity.PetOwner == LayoutOwner);
        if (owner != null && osty != null)
        {
            osty.Position = owner.Position + NCreature.GetOstyOffsetFromPlayer(osty.Entity);
        }
    }

    /// <summary>Redraws every enemy intent so its numbers follow <see cref="IntentTarget" />.</summary>
    private static void RefreshIntents()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || creature.Entity.IsPlayer || creature.IntentContainer == null)
            {
                continue;
            }
            foreach (NIntent intent in creature.IntentContainer.GetChildren().OfType<NIntent>())
            {
                if (intent._intent != null && !intent._isFrozen)
                {
                    intent.UpdateVisuals();
                }
            }
        }
    }

    /// <summary>
    /// Swaps which of the enemies' per-player powers are up on their bars, once whose they are has changed. Ones
    /// applied later go through <see cref="IsPowerShown" /> as they arrive (see <see cref="Patches.EnemyPowerPatches" />).
    /// </summary>
    private static void RefreshEnemyPowers()
    {
        foreach (NCreature creature in NCombatRoom.Instance!.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || !creature.Entity.IsEnemy
                || creature._stateDisplay?._powerContainer is not { } container || !GodotObject.IsInstanceValid(container))
            {
                continue;
            }
            List<PowerModel> powers = creature.Entity.Powers.ToList();
            bool changed = false;
            foreach (PowerModel power in powers.Where(p => p.Target?.Player != null))
            {
                bool shown = IsPowerShown(power) ?? power.IsVisible;
                NPower? node = container._powerNodes.FirstOrDefault(n => n.Model == power);
                if (shown && node == null)
                {
                    AddPowerNode(container, power);
                    changed = true;
                }
                else if (!shown && node != null)
                {
                    // What NPowerContainer.Remove does.
                    container._powerNodes.Remove(node);
                    node.QueueFreeSafely();
                    changed = true;
                }
            }
            if (changed)
            {
                // New icons were added at the end; put them back in the order the powers were applied.
                container._powerNodes.Sort((a, b) => powers.IndexOf(a.Model).CompareTo(powers.IndexOf(b.Model)));
                container.UpdatePositions();
            }
        }
    }

    /// <summary>
    /// A Thieving Hopper holds up the card it stole, but the game only ever puts ours in its hand. While watching
    /// somebody it holds up theirs instead. Ours is only hidden, and comes back with our own view.
    /// </summary>
    public static void ShowStolenCard(NCreature hopper)
    {
        if (_localPlayer == null || !GodotObject.IsInstanceValid(hopper) || hopper.Entity.Monster is not ThievingHopper
            || hopper.GetSpecialNode<Marker2D>("%StolenCardPos") is not { } spot)
        {
            return;
        }
        Player holder = _powersAimedAt ?? _localPlayer;
        CardModel? wanted = hopper.Entity.Powers.OfType<SwipePower>()
            .FirstOrDefault(s => s.Target?.Player == holder)?.StolenCard;

        bool shown = false;
        foreach (NCard card in spot.GetChildren().OfType<NCard>())
        {
            bool show = !shown && card.Model == wanted;
            if (!show && card.HasMeta(stolenCardMeta))
            {
                // Card nodes go back to a pool to be used again, and the mark must not go with it.
                card.RemoveMeta(stolenCardMeta);
                card.QueueFreeSafely();
                continue;
            }
            card.Visible = show;
            shown |= show;
        }
        // Our own card is the game's to put up.
        if (shown || wanted == null || holder == _localPlayer)
        {
            return;
        }

        // The way ThievingHopper.ThieveryMove puts ours there.
        NCard? stolen = NCard.Create(wanted);
        if (stolen == null)
        {
            return;
        }
        stolen.SetMeta(stolenCardMeta, true);
        spot.AddChildSafely(stolen);
        stolen.Position += stolen.Size * 0.5f;
        stolen.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
    }
}
