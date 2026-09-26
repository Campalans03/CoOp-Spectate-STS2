using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Net;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Both ends of <see cref="zSpectateHandSelectionMessage" /> for one combat: tells the others which cards we have
/// marked while picking from our own hand, and files what they tell us into <see cref="HandSelection" />.
/// <para>
/// Our side is read off the real hand once a frame rather than patched into each of its select and deselect paths
/// (click, deselect, upgrade swap, revalidation after a state change). A message only goes out when the list changes.
/// </para>
/// </summary>
public partial class NSpectateSelectionSync : Node
{
    private NPlayerHand? _hand;
    private INetGameService? _netService;
    private readonly List<CardModel> _sent = new();

    public static NSpectateSelectionSync Create(NPlayerHand hand)
    {
        return new NSpectateSelectionSync { Name = "SpectateSelectionSync", _hand = hand };
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        try
        {
            _netService = RunManager.Instance.NetService;
            _netService.RegisterMessageHandler<zSpectateHandSelectionMessage>(OnSelectionReceived);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not listen for hand selections: {e}");
            _netService = null;
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        _netService?.UnregisterMessageHandler<zSpectateHandSelectionMessage>(OnSelectionReceived);
        _netService = null;
        _sent.Clear();
        HandSelection.Clear();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_hand == null || !GodotObject.IsInstanceValid(_hand))
        {
            return;
        }

        bool selecting = _hand.IsInCardSelection;
        if (selecting && SpectateController.Viewed != null)
        {
            // Something made us pick from our own hand while another was covering it. The choice needs our real hand,
            // so drop the view right away.
            SpectateController.Stop();
        }

        IReadOnlyList<CardModel> marked = selecting ? _hand._selectedCards : Array.Empty<CardModel>();
        if (!marked.SequenceEqual(_sent))
        {
            Send(marked);
        }
    }

    private void Send(IReadOnlyList<CardModel> marked)
    {
        _sent.Clear();
        _sent.AddRange(marked);
        if (_netService == null)
        {
            return;
        }

        zSpectateHandSelectionMessage message = new();
        try
        {
            foreach (CardModel card in marked.Take(zSpectateHandSelectionMessage.maxCards))
            {
                message.cards.Add(NetCombatCard.FromModel(card));
            }
            _netService.SendMessage(message);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to send the hand selection: {e}");
        }
    }

    private void OnSelectionReceived(zSpectateHandSelectionMessage message, ulong senderId)
    {
        if (!GodotObject.IsInstanceValid(this) || senderId == _netService?.NetId)
        {
            return;
        }
        List<CardModel> cards = message.cards
            .Select(c => c.ToCardModelOrNull())
            .OfType<CardModel>()
            .ToList();
        HandSelection.SetMarked(senderId, cards);
    }
}
