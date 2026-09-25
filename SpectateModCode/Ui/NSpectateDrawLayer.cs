using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Net;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Drawing on a teammate's screen: a spectator holds the mouse down and drags, and the line shows up on the watched
/// player's screen, on the spectator's own, and for anyone else watching the same player, since they all see the same
/// board. Lines wipe themselves away a moment later (see <see cref="NSpectateStroke" />).
/// <para>
/// Built on the same lines as the map's drawings (NMapDrawings): stroke events batched and sent at most every 50ms,
/// positions through <see cref="NetCursorHelper" /> so a line lands on the same thing whatever resolution or aspect
/// ratio each side plays at.
/// </para>
/// </summary>
public partial class NSpectateDrawLayer : Control
{
    /// <summary>Above the combat scene and the mirrored targeting arrow.</summary>
    private const int drawZIndex = 60;

    /// <summary>The map's own send rate for drawings.</summary>
    private const ulong minSendIntervalMs = 50;

    /// <summary>Far apart in hue and bright enough to stand out against any act's background.</summary>
    private static readonly Color[] slotColors =
    {
        new(0.25f, 0.8f, 1f), // cyan
        new(1f, 0.45f, 0.2f), // orange
        new(0.45f, 1f, 0.35f), // green
        new(1f, 0.35f, 0.85f) // magenta
    };

    private static readonly Color fallbackColor = Colors.White;

    private readonly Dictionary<ulong, NSpectateStroke> _remoteStrokes = new();

    private INetGameService? _netService;
    private NSpectateStroke? _localStroke;

    /// <summary>Whose screen the line in progress is on, so its end goes to the same place as its start.</summary>
    private ulong _localTarget;
    private zSpectateDrawMessage? _queued;
    private ulong _lastSendMs;

    public static NSpectateDrawLayer Create()
    {
        NSpectateDrawLayer node = new()
        {
            Name = "SpectateDrawings",
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = drawZIndex
        };
        node.SetAnchorsPreset(LayoutPreset.FullRect);
        return node;
    }

    public bool IsLocalDrawing => _localStroke != null;

    public override void _EnterTree()
    {
        base._EnterTree();
        try
        {
            _netService = RunManager.Instance.NetService;
            _netService.RegisterMessageHandler<zSpectateDrawMessage>(OnDrawReceived);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not listen for drawings: {e}");
            _netService = null;
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        _netService?.UnregisterMessageHandler<zSpectateDrawMessage>(OnDrawReceived);
        _netService = null;
        _localStroke = null;
        _queued = null;
        _remoteStrokes.Clear();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        // Whatever is still queued goes out once the interval allows, so the end of a line is never held back.
        TrySend(force: false);
    }

    public void BeginLocal(Player target, Vector2 globalPosition)
    {
        if (_netService == null)
        {
            return;
        }
        EndLocal();
        Vector2 normalized = NetCursorHelper.GetNormalizedPosition(globalPosition, this);
        // Drawn from the same round trip through normalised space the others will make, so what we see is where it
        // lands for them.
        _localStroke = AddStroke(normalized, _netService.NetId);
        _localTarget = target.NetId;
        Queue(target.NetId, new SpectateDrawEvent { type = SpectateDrawEventType.BeginLine, position = normalized });
    }

    public void ContinueLocal(Vector2 globalPosition)
    {
        if (_localStroke == null || !GodotObject.IsInstanceValid(_localStroke))
        {
            return;
        }
        Vector2 normalized = NetCursorHelper.GetNormalizedPosition(globalPosition, this);
        _localStroke.Extend(NetCursorHelper.GetControlSpacePosition(normalized, this));
        Queue(_localTarget, new SpectateDrawEvent { type = SpectateDrawEventType.ContinueLine, position = normalized });
    }

    public void EndLocal()
    {
        if (_localStroke == null)
        {
            return;
        }
        if (GodotObject.IsInstanceValid(_localStroke))
        {
            _localStroke.End();
        }
        _localStroke = null;
        Queue(_localTarget, new SpectateDrawEvent { type = SpectateDrawEventType.EndLine });
    }

    private void Queue(ulong targetNetId, SpectateDrawEvent ev)
    {
        if (_queued != null && (_queued.targetNetId != targetNetId || !_queued.TryAddEvent(ev)))
        {
            TrySend(force: true);
        }
        if (_queued == null)
        {
            _queued = new zSpectateDrawMessage { targetNetId = targetNetId };
            _queued.TryAddEvent(ev);
        }
        TrySend(force: false);
    }

    private void TrySend(bool force)
    {
        if (_queued == null || _netService == null)
        {
            return;
        }
        ulong now = Time.GetTicksMsec();
        if (!force && now - _lastSendMs < minSendIntervalMs)
        {
            return;
        }
        zSpectateDrawMessage message = _queued;
        _queued = null;
        _lastSendMs = now;
        try
        {
            _netService.SendMessage(message);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to send a drawing: {e}");
        }
    }

    private void OnDrawReceived(zSpectateDrawMessage message, ulong senderId)
    {
        if (!GodotObject.IsInstanceValid(this) || _netService == null || senderId == _netService.NetId)
        {
            return;
        }
        // Only the screen that was drawn on, and whoever else is looking at that screen, has what it points to.
        bool isForMe = message.targetNetId == _netService.NetId;
        bool isWatchingSame = SpectateController.Target?.NetId == message.targetNetId;
        if (!isForMe && !isWatchingSame)
        {
            EndRemote(senderId);
            return;
        }

        foreach (SpectateDrawEvent ev in message.Events)
        {
            switch (ev.type)
            {
                case SpectateDrawEventType.BeginLine:
                    EndRemote(senderId);
                    _remoteStrokes[senderId] = AddStroke(ev.position, senderId);
                    break;
                case SpectateDrawEventType.ContinueLine:
                    if (_remoteStrokes.TryGetValue(senderId, out NSpectateStroke? stroke)
                        && GodotObject.IsInstanceValid(stroke))
                    {
                        stroke.Extend(NetCursorHelper.GetControlSpacePosition(ev.position, this));
                    }
                    else
                    {
                        // Started watching them mid-line: pick it up from here.
                        _remoteStrokes[senderId] = AddStroke(ev.position, senderId);
                    }
                    break;
                case SpectateDrawEventType.EndLine:
                    EndRemote(senderId);
                    break;
            }
        }
    }

    private void EndRemote(ulong senderId)
    {
        if (_remoteStrokes.Remove(senderId, out NSpectateStroke? stroke) && GodotObject.IsInstanceValid(stroke))
        {
            stroke.End();
        }
    }

    private NSpectateStroke AddStroke(Vector2 normalizedStart, ulong senderId)
    {
        NSpectateStroke stroke = NSpectateStroke.Create(
            NetCursorHelper.GetControlSpacePosition(normalizedStart, this), ColorFor(senderId));
        this.AddChildSafely(stroke);
        return stroke;
    }

    /// <summary>
    /// One colour per player, picked by their slot in the run rather than their character, so two players on the same
    /// character still read apart. Slots are part of the shared run state, so every client agrees on who is which.
    /// </summary>
    private static Color ColorFor(ulong senderId)
    {
        CombatState? state = SpectateController.State;
        Player? sender = state?.Players.FirstOrDefault(p => p.NetId == senderId);
        if (state == null || sender == null)
        {
            return fallbackColor;
        }
        IPlayerCollection players = state.RunState;
        int slot = players.GetPlayerSlotIndex(sender);
        return slot >= 0 ? slotColors[slot % slotColors.Length] : fallbackColor;
    }
}
