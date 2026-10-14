using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Net;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Both ends of <see cref="zSpectateSphereMessage" /> for the whole run: tells the others what our Crystal Sphere looks
/// like while we are playing it, and files what they tell us into <see cref="SphereState" />. Our side is read off the
/// minigame once a frame and only sent when it changes. Cells cleared and divinations spent go out straight away; the
/// cell under the cursor and the tool in hand, which change as the mouse moves, at most every
/// <see cref="minSendIntervalMs" />.
/// </summary>
public partial class NSpectateSphereSync : Node
{
    private const ulong minSendIntervalMs = 100;

    private INetGameService? _netService;
    private CrystalSphereMinigame? _sentGame;
    private bool[] _sentRevealed = Array.Empty<bool>();
    private int _sentDivinations;
    private bool _sentSmallTool;
    private int _sentHovered = -1;
    private ulong _lastSendMs;

    public static NSpectateSphereSync Create()
    {
        return new NSpectateSphereSync { Name = "SpectateSphereSync" };
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        try
        {
            _netService = RunManager.Instance.NetService;
            _netService.RegisterMessageHandler<zSpectateSphereMessage>(OnSphereReceived);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not listen for Crystal Spheres: {e}");
            _netService = null;
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        _netService?.UnregisterMessageHandler<zSpectateSphereMessage>(OnSphereReceived);
        _netService = null;
        SphereState.Clear();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_netService != null)
        {
            Safe.Run("send our Crystal Sphere", SendIfChanged);
        }
    }

    private void SendIfChanged()
    {
        CrystalSphereMinigame? game = OpenGame();
        if (game == null)
        {
            if (_sentGame != null)
            {
                _sentGame = null;
                Send(new zSpectateSphereMessage { open = false });
            }
            return;
        }

        Vector2I size = game.GridSize;
        bool[] revealed = new bool[size.X * size.Y];
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                revealed[y * size.X + x] = !game.cells[x, y].IsHidden;
            }
        }
        int hovered = game.HoveredCell is { } cell ? cell.Y * size.X + cell.X : -1;
        bool smallTool = game.CrystalSphereTool == CrystalSphereMinigame.CrystalSphereToolType.Small;

        bool progressed = game != _sentGame || game.DivinationCount != _sentDivinations
                                            || !revealed.SequenceEqual(_sentRevealed);
        bool pointed = hovered != _sentHovered || smallTool != _sentSmallTool;
        if (!progressed && !pointed)
        {
            return;
        }
        ulong now = Time.GetTicksMsec();
        if (!progressed && now - _lastSendMs < minSendIntervalMs)
        {
            return;
        }

        _sentGame = game;
        _sentRevealed = revealed;
        _sentDivinations = game.DivinationCount;
        _sentSmallTool = smallTool;
        _sentHovered = hovered;
        _lastSendMs = now;
        Send(new zSpectateSphereMessage
        {
            open = true,
            width = size.X,
            height = size.Y,
            items = game.Items
                .Take(zSpectateSphereMessage.maxItems)
                .Select(item => new SpectateSphereItem
                {
                    item = item.ToSerializable(),
                    x = item.Position.X,
                    y = item.Position.Y
                })
                .ToList(),
            revealed = revealed,
            divinations = game.DivinationCount,
            smallTool = smallTool,
            hovered = hovered
        });
    }

    /// <summary>
    /// Our own sphere, while its screen is up. It sits in the overlay stack, and stays there under the rewards it pays
    /// out once it is done.
    /// </summary>
    private static CrystalSphereMinigame? OpenGame()
    {
        NOverlayStack? stack = NOverlayStack.Instance;
        if (stack == null || !GodotObject.IsInstanceValid(stack))
        {
            return null;
        }
        return stack._overlays
            .OfType<NCrystalSphereScreen>()
            .FirstOrDefault(s => GodotObject.IsInstanceValid(s) && s.IsInsideTree())
            ?._entity;
    }

    private void Send(zSpectateSphereMessage message)
    {
        try
        {
            _netService?.SendMessage(message);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to send the Crystal Sphere: {e}");
        }
    }

    private void OnSphereReceived(zSpectateSphereMessage message, ulong senderId)
    {
        if (!GodotObject.IsInstanceValid(this) || senderId == _netService?.NetId)
        {
            return;
        }
        SphereState.Set(senderId, message);
    }
}
