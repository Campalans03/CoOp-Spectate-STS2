using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Net;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Both ends of <see cref="zSpectateGridSelectionMessage" /> for the whole run: tells the others what we have marked in
/// a grid of cards we are picking from and how far we have scrolled it, and files what they tell us into
/// <see cref="GridSelection" />.
/// <para>
/// Our side is read off whatever grid screen is on top of the overlay stack, once a frame. Every one of them keeps the
/// cards picked so far in a private set called _selectedCards, so that is read by name rather than per screen type.
/// Scrolling is continuous, so updates go out at most every <see cref="minSendIntervalMs" />.
/// </para>
/// </summary>
public partial class NSpectateGridSync : Node
{
    private const ulong minSendIntervalMs = 100;

    /// <summary>Scroll changes smaller than this aren't worth a message.</summary>
    private const float scrollEpsilon = 1f;

    private INetGameService? _netService;
    private readonly List<int> _sentMarked = new();
    private float _sentScroll;
    private ulong _lastSendMs;

    public static NSpectateGridSync Create()
    {
        return new NSpectateGridSync { Name = "SpectateGridSync" };
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        try
        {
            _netService = RunManager.Instance.NetService;
            _netService.RegisterMessageHandler<zSpectateGridSelectionMessage>(OnGridReceived);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not listen for card grids: {e}");
            _netService = null;
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        _netService?.UnregisterMessageHandler<zSpectateGridSelectionMessage>(OnGridReceived);
        _netService = null;
        GridSelection.Clear();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_netService == null)
        {
            return;
        }

        List<int> marked;
        float scroll;
        try
        {
            (marked, scroll) = ReadLocalGrid();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to read our card grid: {e}");
            return;
        }

        bool markedChanged = !marked.SequenceEqual(_sentMarked);
        bool scrollChanged = Mathf.Abs(scroll - _sentScroll) > scrollEpsilon;
        if (!markedChanged && !scrollChanged)
        {
            return;
        }
        // Marks go out straight away; a scroll on its own waits its turn, the way the drawings are paced.
        ulong now = Time.GetTicksMsec();
        if (!markedChanged && now - _lastSendMs < minSendIntervalMs)
        {
            return;
        }

        _sentMarked.Clear();
        _sentMarked.AddRange(marked);
        _sentScroll = scroll;
        _lastSendMs = now;
        try
        {
            _netService.SendMessage(new zSpectateGridSelectionMessage
            {
                marked = marked.Take(zSpectateGridSelectionMessage.maxMarked).ToList(),
                scroll = scroll
            });
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to send the card grid: {e}");
        }
    }

    /// <summary>
    /// The marked cards as positions in the list the screen was given (for a combat pile, the pile as filtered right
    /// now, which is what that screen shows), and the grid's scroll target. Nothing open reads as nothing marked.
    /// </summary>
    private static (List<int>, float) ReadLocalGrid()
    {
        if (NOverlayStack.Instance?.Peek() is not NCardGridSelectionScreen screen || !GodotObject.IsInstanceValid(screen)
            || screen._grid == null || !GodotObject.IsInstanceValid(screen._grid))
        {
            return (new List<int>(), 0f);
        }

        List<CardModel> options = screen is NCombatPileCardSelectScreen pileScreen
            ? pileScreen._pile.Cards.Where(pileScreen._filter).ToList()
            : screen._cards.ToList();
        IEnumerable<CardModel> picked = SelectedCards(screen).Concat(screen._grid._highlightedCards);
        List<int> marked = picked
            .Select(c => options.IndexOf(c))
            .Where(i => i >= 0)
            .Distinct()
            .Order()
            .ToList();
        return (marked, screen._grid._targetDrag);
    }

    private static IEnumerable<CardModel> SelectedCards(NCardGridSelectionScreen screen)
    {
        return HarmonyLib.AccessTools.Field(screen.GetType(), "_selectedCards")?.GetValue(screen)
            as IEnumerable<CardModel> ?? Enumerable.Empty<CardModel>();
    }

    private void OnGridReceived(zSpectateGridSelectionMessage message, ulong senderId)
    {
        if (!GodotObject.IsInstanceValid(this) || senderId == _netService?.NetId)
        {
            return;
        }
        GridSelection.SetProgress(senderId, new GridSelection.Progress(message.marked, message.scroll));
    }
}
