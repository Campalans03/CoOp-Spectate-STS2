using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Owns "who am I watching". Purely local: it never touches the action queue or the net services, so a spectating
/// client stays a normal client and the teammate keeps full control of their own turn.
/// </summary>
public static class SpectateController
{
    private static CombatState? _state;
    private static bool _subscribed;

    /// <summary>The teammate currently being watched, or null when we are looking at our own board.</summary>
    public static Player? Target { get; private set; }

    public static bool IsSpectating => Target != null;

    /// <summary>True when the current spectate was picked by the mod rather than by a click.</summary>
    public static bool IsAutoPicked { get; private set; }

    public static CombatState? State => _state;

    /// <summary>Raised after <see cref="Target"/> changes. The argument is the new target (null when stopping).</summary>
    public static event Action<Player?>? TargetChanged;

    public static void AttachToCombat(CombatState state)
    {
        DetachFromCombat();
        _state = state;
        if (state.Players.Count <= 1)
        {
            // Singleplayer (or a solo lobby): there is nobody to watch, so stay completely out of the way.
            _state = null;
            return;
        }

        CombatManager.Instance.PlayerEndedTurn += OnPlayerEndedTurn;
        CombatManager.Instance.PlayerUnendedTurn += OnPlayerUnendedTurn;
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        CombatManager.Instance.CreaturesChanged += OnCreaturesChanged;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        _subscribed = true;
    }

    public static void DetachFromCombat()
    {
        Stop();
        if (_subscribed)
        {
            CombatManager.Instance.PlayerEndedTurn -= OnPlayerEndedTurn;
            CombatManager.Instance.PlayerUnendedTurn -= OnPlayerUnendedTurn;
            CombatManager.Instance.TurnStarted -= OnTurnStarted;
            CombatManager.Instance.CreaturesChanged -= OnCreaturesChanged;
            CombatManager.Instance.CombatEnded -= OnCombatEnded;
            _subscribed = false;
        }
        _state = null;
    }

    public static Player? Me => _state == null ? null : LocalContext.GetMe(_state);

    /// <summary>Teammates in stable slot order, so the "preferred slot" option means the same thing every combat.</summary>
    public static IReadOnlyList<Player> Teammates()
    {
        if (_state == null)
        {
            return Array.Empty<Player>();
        }
        IPlayerCollection collection = _state.RunState;
        return _state.Players
            .Where(p => !LocalContext.IsMe(p))
            .OrderBy(collection.GetPlayerSlotIndex)
            .ToList();
    }

    /// <summary>Can this player be watched at all? (Alive, in combat, and not us.)</summary>
    public static bool CanSpectate(Player? player)
    {
        return player != null
               && _state != null
               && !LocalContext.IsMe(player)
               && player.PlayerCombatState != null
               && player.Creature is { IsAlive: true }
               && _state.Players.Contains(player);
    }

    /// <summary>Additionally requires that the teammate still has a turn to watch.</summary>
    public static bool IsAutoEligible(Player? player)
    {
        return CanSpectate(player) && !CombatManager.Instance.IsPlayerReadyToEndTurn(player!);
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
        if (!CanSpectate(player) || Target == player)
        {
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

    /// <summary>Picks the best teammate to auto-spectate, honouring the preferred-slot option.</summary>
    public static Player? PickAutoTarget()
    {
        IReadOnlyList<Player> teammates = Teammates();
        int preferred = SpectateConfig.PreferredTeammateSlot;
        if (preferred > 0 && preferred <= teammates.Count)
        {
            Player candidate = teammates[preferred - 1];
            if (IsAutoEligible(candidate))
            {
                return candidate;
            }
        }
        return teammates.FirstOrDefault(IsAutoEligible);
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
        Stop();
    }

    private static void OnTurnStarted(CombatState state)
    {
        Stop();
    }

    private static void OnCreaturesChanged(CombatState state)
    {
        if (Target != null && !CanSpectate(Target))
        {
            RepickOrStop();
        }
    }

    /// <summary>
    /// The current target stopped being watchable. Auto-spectate rolls on to the next eligible teammate;
    /// a manually chosen target just ends, because silently swapping who you clicked on would be confusing.
    /// </summary>
    private static void RepickOrStop()
    {
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
