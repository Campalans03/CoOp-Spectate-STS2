using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Owns "who am I watching". Purely local: it never touches the action queue or the net services, so a spectating
/// client stays a normal client and the teammate keeps full control of their own turn.
/// <para>
/// It lives for the whole run, so a teammate picked on the map or in the shop stays picked. Combat adds its own rules
/// on top (auto-spectate, back to our own board when our turn starts) while a combat is attached.
/// </para>
/// </summary>
public static class SpectateController
{
    private static IRunState? _run;
    private static CombatState? _combat;
    private static Creature? _localCreature;
    private static bool _subscribed;
    private static Player? _viewed;

    /// <summary>The teammate picked to watch, or null when we are looking at our own screen.</summary>
    public static Player? Target { get; private set; }

    /// <summary>
    /// The player a peek box is showing while the mouse rests on it. Wins over <see cref="Target" /> for as long as it
    /// lasts; our own player here means "show me my own screen for a moment".
    /// </summary>
    public static Player? Peek { get; private set; }

    /// <summary>Whose screen is on show right now: the peek if there is one, else the target. Null means our own.</summary>
    public static Player? Viewed => _viewed;

    public static bool IsSpectating => Target != null;

    /// <summary>True while a multiplayer run is attached, which is the only time any of this means anything.</summary>
    public static bool IsActive => _run != null;

    /// <summary>True when the current spectate was picked by the mod rather than by a click.</summary>
    public static bool IsAutoPicked { get; private set; }

    /// <summary>The combat in progress, or null outside combat.</summary>
    public static CombatState? State => _combat;

    public static IRunState? Run => _run;

    /// <summary>Raised after <see cref="Target"/> changes. The argument is the new target (null when stopping).</summary>
    public static event Action<Player?>? TargetChanged;

    /// <summary>Raised after <see cref="Viewed"/> changes, whether through the target or a peek.</summary>
    public static event Action<Player?>? ViewChanged;

    public static void AttachToRun(IRunState run)
    {
        DetachFromRun();
        if (run.Players.Count <= 1)
        {
            // Singleplayer (or a solo lobby): there is nobody to watch, so stay completely out of the way.
            return;
        }
        _run = run;
    }

    public static void DetachFromRun()
    {
        DetachFromCombat();
        Stop();
        SetPeek(null);
        _run = null;
    }

    public static void AttachToCombat(CombatState state)
    {
        DetachFromCombat();
        if (_run == null && state.Players.Count > 1)
        {
            // The run hook should have got here first; fall back rather than lose spectating for this combat.
            AttachToRun(state.RunState);
        }
        if (_run == null)
        {
            return;
        }
        _combat = state;

        // Every combat starts on our own board: our first turn is about to begin and it needs our hand.
        Stop();

        CombatManager.Instance.PlayerEndedTurn += OnPlayerEndedTurn;
        CombatManager.Instance.PlayerUnendedTurn += OnPlayerUnendedTurn;
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        CombatManager.Instance.CreaturesChanged += OnCreaturesChanged;
        CombatManager.Instance.CombatEnded += OnCombatEnded;

        // A Player keeps the same Creature for the whole run, so hold the reference we subscribed to and detach from
        // that exact one later rather than looking it up again.
        _localCreature = Me?.Creature;
        if (_localCreature != null)
        {
            _localCreature.Died += OnLocalCreatureDied;
        }
        _subscribed = true;
    }

    public static void DetachFromCombat()
    {
        if (_subscribed)
        {
            CombatManager.Instance.PlayerEndedTurn -= OnPlayerEndedTurn;
            CombatManager.Instance.PlayerUnendedTurn -= OnPlayerUnendedTurn;
            CombatManager.Instance.TurnStarted -= OnTurnStarted;
            CombatManager.Instance.CreaturesChanged -= OnCreaturesChanged;
            CombatManager.Instance.CombatEnded -= OnCombatEnded;
            _subscribed = false;
        }
        if (_localCreature != null)
        {
            _localCreature.Died -= OnLocalCreatureDied;
            _localCreature = null;
        }
        _combat = null;
        // The mod picked this one for the combat's sake; a teammate we clicked on stays picked.
        if (IsAutoPicked)
        {
            Stop();
        }
    }

    /// <summary>A dead player has no turn of their own to come back to, which relaxes what counts as a good target.</summary>
    public static bool IsLocalPlayerDead => _combat != null && Me?.Creature is { IsDead: true };

    public static Player? Me => _run == null ? null : LocalContext.GetMe(_run);

    /// <summary>Teammates in stable slot order, so the "preferred slot" option means the same thing every combat.</summary>
    public static IReadOnlyList<Player> Teammates()
    {
        if (_run == null)
        {
            return Array.Empty<Player>();
        }
        IPlayerCollection collection = _run;
        return _run.Players
            .Where(p => !LocalContext.IsMe(p))
            .OrderBy(collection.GetPlayerSlotIndex)
            .ToList();
    }

    /// <summary>
    /// Can this player be watched at all? Anyone else in the run, and in combat only while they are alive and still
    /// taking part in it.
    /// </summary>
    public static bool CanSpectate(Player? player)
    {
        if (player == null || _run == null || LocalContext.IsMe(player) || !_run.Players.Contains(player))
        {
            return false;
        }
        return _combat == null
               || (player.PlayerCombatState != null
                   && player.Creature is { IsAlive: true }
                   && _combat.Players.Contains(player));
    }

    /// <summary>Additionally requires that the teammate still has a turn to watch.</summary>
    public static bool IsAutoEligible(Player? player)
    {
        return _combat != null && CanSpectate(player) && !CombatManager.Instance.IsPlayerReadyToEndTurn(player!);
    }

    public static string GetDisplayName(Player player)
    {
        try
        {
            return PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform, player.NetId);
        }
        catch (Exception)
        {
            // Names come from the platform layer, which can be unavailable in offline/test lobbies.
            return player.Character.GetType().Name;
        }
    }

    public static void Toggle(Player player)
    {
        if (Target == player)
        {
            Stop();
        }
        else
        {
            Start(player, autoPicked: false);
        }
    }

    public static void Start(Player player, bool autoPicked)
    {
        if (!CanSpectate(player))
        {
            return;
        }
        if (Target == player)
        {
            // Clicking the teammate the mod had already picked makes it ours: it no longer ends with the combat.
            IsAutoPicked &= autoPicked;
            return;
        }
        Target = player;
        IsAutoPicked = autoPicked;
        MainFile.Logger.Info($"Spectating {GetDisplayName(player)} (auto: {autoPicked})");
        Raise();
    }

    public static void Stop()
    {
        if (Target == null)
        {
            return;
        }
        Target = null;
        IsAutoPicked = false;
        Raise();
    }

    /// <summary>Starts or ends a peek. Pass null when the mouse leaves the box.</summary>
    public static void SetPeek(Player? player)
    {
        if (player == Peek)
        {
            return;
        }
        Peek = player;
        RaiseViewIfChanged();
    }

    /// <summary>Picks the best teammate to auto-spectate, honouring the preferred-slot option.</summary>
    public static Player? PickAutoTarget()
    {
        Player? stillPlaying = PickBy(IsAutoEligible);
        if (stillPlaying != null)
        {
            return stillPlaying;
        }
        // Once we are dead there is nothing to come back to, so watching a teammate who has already ended their turn
        // still beats staring at our own corpse.
        return IsLocalPlayerDead ? PickBy(CanSpectate) : null;
    }

    private static Player? PickBy(Func<Player, bool> predicate)
    {
        IReadOnlyList<Player> teammates = Teammates();
        int preferred = SpectateConfig.PreferredTeammateSlot;
        if (preferred > 0 && preferred <= teammates.Count && predicate(teammates[preferred - 1]))
        {
            return teammates[preferred - 1];
        }
        return teammates.FirstOrDefault(predicate);
    }

    private static void Raise()
    {
        try
        {
            TargetChanged?.Invoke(Target);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Spectate listener threw: {e}");
        }
        RaiseViewIfChanged();
    }

    private static void RaiseViewIfChanged()
    {
        Player? shown = Peek ?? Target;
        if (shown != null && LocalContext.IsMe(shown))
        {
            shown = null;
        }
        if (shown == _viewed)
        {
            return;
        }
        _viewed = shown;
        try
        {
            ViewChanged?.Invoke(shown);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Spectate view listener threw: {e}");
        }
    }

    private static void OnPlayerEndedTurn(Player player, bool canBackOut)
    {
        if (LocalContext.IsMe(player))
        {
            if (SpectateConfig.AutoSpectateAfterEndTurn && !IsSpectating)
            {
                Player? target = PickAutoTarget();
                if (target != null)
                {
                    Start(target, autoPicked: true);
                }
            }
            return;
        }

        // The teammate we were auto-following just ended their turn; move on to someone still playing.
        if (IsAutoPicked && player == Target)
        {
            RepickOrStop();
        }
    }

    private static void OnPlayerUnendedTurn(Player player)
    {
        // We took our turn back (e.g. cancelled end turn), so hand the view back to us.
        if (LocalContext.IsMe(player))
        {
            Stop();
        }
    }

    private static void OnCombatEnded(CombatRoom room)
    {
        if (IsAutoPicked)
        {
            Stop();
        }
    }

    private static void OnTurnStarted(CombatState state)
    {
        if (IsLocalPlayerDead)
        {
            // No turn of ours is starting, so don't yank the camera back to a corpse.
            EnsureDeathSpectate();
            return;
        }
        Stop();
    }

    private static void OnCreaturesChanged(CombatState state)
    {
        if (Target != null && !CanSpectate(Target))
        {
            RepickOrStop();
        }
    }

    private static void OnLocalCreatureDied(Creature creature)
    {
        if (!SpectateConfig.AutoSpectateOnDeath)
        {
            return;
        }
        // Don't override a teammate the player deliberately picked; only fill an empty view.
        if (IsSpectating)
        {
            return;
        }
        Player? target = PickAutoTarget();
        if (target != null)
        {
            Start(target, autoPicked: true);
        }
    }

    /// <summary>
    /// Keeps a dead player looking at somebody worth watching: stays put while the current target is still taking
    /// their turn, otherwise moves on to whoever is left.
    /// </summary>
    private static void EnsureDeathSpectate()
    {
        if (!SpectateConfig.AutoSpectateOnDeath)
        {
            if (!CanSpectate(Target))
            {
                Stop();
            }
            return;
        }

        if (Target != null && IsAutoEligible(Target))
        {
            return;
        }

        Player? best = PickAutoTarget();
        if (best == null)
        {
            Stop();
            return;
        }
        if (best != Target)
        {
            Target = best;
            IsAutoPicked = true;
            Raise();
        }
    }

    /// <summary>
    /// The current target stopped being watchable. Auto-spectate rolls on to the next eligible teammate;
    /// a manually chosen target just ends, because silently swapping who you clicked on would be confusing.
    /// </summary>
    private static void RepickOrStop()
    {
        if (IsLocalPlayerDead)
        {
            EnsureDeathSpectate();
            return;
        }
        if (!IsAutoPicked || !SpectateConfig.AutoSpectateAfterEndTurn)
        {
            Stop();
            return;
        }
        Player? next = PickAutoTarget();
        if (next == null)
        {
            Stop();
            return;
        }
        if (next != Target)
        {
            Target = next;
            Raise();
        }
    }
}
