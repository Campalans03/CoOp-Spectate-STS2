using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Net;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// Both ends of <see cref="zSpectateShopMessage" /> for the whole run: tells the others whether our merchant rug is
/// out and what has gone from it, and files what they tell us into <see cref="ShopState" />. Our side is read off the
/// merchant room once a frame and only sent when it changes; leaving the shop reads as a closed, empty rug.
/// </summary>
public partial class NSpectateShopSync : Node
{
    private INetGameService? _netService;
    private ShopState.Rug _sent;

    public static NSpectateShopSync Create()
    {
        return new NSpectateShopSync { Name = "SpectateShopSync" };
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        try
        {
            _netService = RunManager.Instance.NetService;
            _netService.RegisterMessageHandler<zSpectateShopMessage>(OnShopReceived);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not listen for shops: {e}");
            _netService = null;
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        _netService?.UnregisterMessageHandler<zSpectateShopMessage>(OnShopReceived);
        _netService = null;
        ShopState.Clear();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        ShopState.Rug now = ReadLocalRug();
        if (now == _sent || _netService == null)
        {
            return;
        }
        _sent = now;
        try
        {
            _netService.SendMessage(new zSpectateShopMessage { open = now.Open, soldMask = now.SoldMask });
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to send the shop state: {e}");
        }
    }

    private static ShopState.Rug ReadLocalRug()
    {
        NMerchantInventory? rug = NMerchantRoom.Instance?.Inventory;
        MerchantInventory? inventory = rug?.Inventory;
        if (rug == null || !GodotObject.IsInstanceValid(rug) || inventory == null)
        {
            return default;
        }
        uint sold = 0;
        int index = 0;
        foreach (MerchantEntry entry in inventory.AllEntries.Take(32))
        {
            if (!entry.IsStocked)
            {
                sold |= 1u << index;
            }
            index++;
        }
        return new ShopState.Rug(rug.IsOpen, sold);
    }

    private void OnShopReceived(zSpectateShopMessage message, ulong senderId)
    {
        if (!GodotObject.IsInstanceValid(this) || senderId == _netService?.NetId)
        {
            return;
        }
        ShopState.Set(senderId, new ShopState.Rug(message.open, message.soldMask));
    }
}
