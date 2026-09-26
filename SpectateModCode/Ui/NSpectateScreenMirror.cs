using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Patches;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// A read-only copy of the screen the watched player has up, drawn in place of ours: their rewards, or the cards they
/// are picking one of. Their cursor is drawn on top, so you can see what they are pointing at.
/// <para>
/// Each screen is the game's own scene with its code switched off (see <see cref="MirrorScreenPatches" />), so it looks
/// exactly like theirs and shares its layout: their cursor lands on the same card it is on for them. The contents are
/// filled in by hand from pieces that are safe on their own: the game's reward buttons (clicks on a teammate's are
/// stopped by <see cref="RewardButtonPatches" />) and bare card nodes.
/// </para>
/// <para>
/// It sits in the global UI right above the overlay stack, where the real screens live: under the top bar, the party
/// list and menus, as theirs would be. While it is up our own overlays are faded out, and it takes the mouse so
/// nothing of ours can be clicked through it.
/// </para>
/// </summary>
public partial class NSpectateScreenMirror : Control
{
    private enum Mode
    {
        None,
        Rewards,
        RewardCards,
        ChooseCards,
        Shop
    }

    private const string merchantInventoryScene = "res://scenes/merchant/merchant_inventory.tscn";

    /// <summary>What NMerchantInventory does to its parts when it opens (its _openPosition and backstop fade).</summary>
    private const float rugOpenY = 80f;

    private const float rugBackstopAlpha = 0.8f;

    /// <summary>Distance between card centres in NCardRewardSelectionScreen.RefreshOptions.</summary>
    private const float rewardCardSpacing = 350f;

    /// <summary>Distance between card centres in NChooseACardSelectionScreen._Ready.</summary>
    private const float chooseCardSpacing = 340f;

    /// <summary>Where the real rewards screen parks its list when it isn't scrolled (its _targetDragPos).</summary>
    private const float rewardListTop = 35f;

    private Control _backstop = null!;
    private Control? _screen;
    private Control? _cardRow;
    private Control? _rewardList;
    private Control? _alternativeRow;
    private NRemoteMouseCursor? _cursor;
    private PeerInputSynchronizer? _sync;

    private Mode _mode;
    private Player? _player;
    private CardChoice.Choice? _choice;
    private MerchantInventory? _shop;
    private NMerchantInventory? _rug;
    private List<MerchantEntry> _shopEntries = new();
    private readonly HashSet<NMerchantSlot> _shownSold = new();
    private object? _failed;
    private readonly List<Reward> _shownRewards = new();
    private readonly List<CardModel> _shownCards = new();
    private readonly List<CardChoice.Alternative> _shownAlternatives = new();
    private readonly Dictionary<CardModel, Control> _slots = new();
    private readonly Dictionary<CardModel, NCard> _cards = new();
    private readonly Dictionary<CardModel, Control> _hitboxes = new();
    private CardModel? _localHovered;

    private bool _hidingOurs;
    private readonly Dictionary<CanvasItem, Color> _hidden = new();

    public static NSpectateScreenMirror Create()
    {
        NSpectateScreenMirror node = new()
        {
            Name = "SpectateScreenMirror",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        node.SetAnchorsPreset(LayoutPreset.FullRect);
        return node;
    }

    public override void _Ready()
    {
        base._Ready();
        _backstop = CreateBackstop();
        this.AddChildSafely(_backstop);
    }

    /// <summary>
    /// The same dark shade the overlay stack puts behind the real screens, copied without its script. It also stops the
    /// mouse, which is what keeps our own faded-out screens from being clicked through it.
    /// </summary>
    private static Control CreateBackstop()
    {
        Control? source = NOverlayStack.Instance?._backstop;
        Control backstop = source != null && GodotObject.IsInstanceValid(source) && source.Duplicate(0) is Control copy
            ? copy
            : new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f) };
        backstop.Name = "Backstop";
        backstop.Visible = true;
        backstop.Modulate = Colors.White;
        backstop.MouseFilter = MouseFilterEnum.Stop;
        backstop.SetAnchorsPreset(LayoutPreset.FullRect);
        backstop.Position = Vector2.Zero;
        return backstop;
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        _sync = RunManager.Instance.InputSynchronizer;
        if (_sync != null)
        {
            _sync.StateChanged += OnInputStateChanged;
        }
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        if (_sync != null)
        {
            _sync.StateChanged -= OnInputStateChanged;
            _sync = null;
        }
        CloseScreen();
        HideOurs(false);
        CardChoice.Clear();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        Player? viewed = SpectateController.Viewed;
        CardChoice.Choice? choice = null;
        RewardsSet? rewards = null;
        MerchantInventory? shop = null;
        try
        {
            choice = viewed == null ? null : CardChoice.Get(viewed);
            rewards = viewed == null || choice != null ? null : OpenRewardsSet(viewed);
            shop = viewed == null || choice != null || rewards != null ? null : OpenShop(viewed);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to read a teammate's screen: {e}");
        }
        Mode mode = choice?.Kind switch
        {
            CardChoice.Kind.Reward => Mode.RewardCards,
            CardChoice.Kind.ChooseACard => Mode.ChooseCards,
            _ => rewards != null ? Mode.Rewards : shop != null ? Mode.Shop : Mode.None
        };

        // A screen that failed to build stays down until something changes, rather than failing again every frame.
        (Player?, Mode, object?) key = (viewed, mode, (object?)choice ?? shop);
        if (_failed != null && _failed.Equals(key))
        {
            mode = Mode.None;
        }

        try
        {
            if (mode != _mode || viewed != _player || choice != _choice || shop != _shop)
            {
                CloseScreen();
                _mode = mode;
                _player = mode == Mode.None ? null : viewed;
                _choice = choice;
                _shop = shop;
                OpenScreen();
            }
            Visible = _mode != Mode.None;
            HideOurs(Visible);

            switch (_mode)
            {
                case Mode.Rewards:
                    FillRewards(rewards!);
                    break;
                case Mode.RewardCards:
                case Mode.ChooseCards:
                    FillCards(choice!);
                    break;
                case Mode.Shop:
                    FillShop();
                    break;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to mirror a teammate's screen: {e}");
            _failed = key;
            CloseScreen();
            _mode = Mode.None;
            _player = null;
            _choice = null;
            _shop = null;
            Visible = false;
            HideOurs(false);
        }
    }

    /// <summary>
    /// The teammate's stock in the shop we are all standing in, while they have the rug rolled out. Every client builds
    /// every player's stock on entering the room, so it is already here; only the rug being open, and what has sold,
    /// comes from them (<see cref="ShopState" />). Not while something of theirs covers the rug, like their deck.
    /// </summary>
    private MerchantInventory? OpenShop(Player player)
    {
        IRunState? run = SpectateController.Run;
        if (run?.CurrentRoom is not MerchantRoom room || !ShopState.Get(player).Open
                                                     || _sync?.GetScreenType(player.NetId) != NetScreenType.Room)
        {
            return null;
        }
        int slot = run.GetPlayerSlotIndex(player);
        return slot >= 0 && slot < room.Inventories.Count ? room.Inventories[slot] : null;
    }

    /// <summary>
    /// The rewards the player is looking at: the top of their stack in the game's reward bookkeeping, which every
    /// client keeps for every player. Only while their screen says rewards, so a set they have walked away from (to
    /// the map, say) isn't shown.
    /// </summary>
    private RewardsSet? OpenRewardsSet(Player player)
    {
        IRunState? run = SpectateController.Run;
        if (run == null || _sync?.GetScreenType(player.NetId) != NetScreenType.Rewards)
        {
            return null;
        }
        int slot = run.GetPlayerSlotIndex(player);
        var states = RunManager.Instance.RewardsSetSynchronizer?._rewardStates;
        if (states == null || slot < 0 || slot >= states.Count || states[slot].rewardsStack.Count == 0)
        {
            return null;
        }
        RewardsSet set = states[slot].rewardsStack[^1].set;
        return set.Rewards.All(r => r.SuccessfullySelected) ? null : set;
    }

    private void OpenScreen()
    {
        switch (_mode)
        {
            case Mode.Rewards:
            {
                NRewardsScreen screen = Instantiate<NRewardsScreen>(NRewardsScreen.ScenePath);
                screen.GetNode<Control>("%WaitingForOtherPlayers").Visible = false;
                screen.GetNode<Control>("%Scrollbar").Visible = false;
                screen.GetNode<Control>("Rewards").Modulate = Colors.White;
                screen.GetNode<MegaLabel>("%HeaderLabel").SetTextAutoSize(
                    new LocString("gameplay_ui", "COMBAT_REWARD_HEADER_LOOT").GetFormattedText());
                screen.GetNode<NProceedButton>("ProceedButton").UpdateText(NProceedButton.SkipLoc);
                _rewardList = screen.GetNode<Control>("%RewardsContainer");
                _rewardList.Position = new Vector2(_rewardList.Position.X, rewardListTop);
                break;
            }
            case Mode.RewardCards:
            {
                NCardRewardSelectionScreen screen =
                    Instantiate<NCardRewardSelectionScreen>(NCardRewardSelectionScreen.ScenePath);
                SetUpBanner(screen.GetNode<NCommonBanner>("UI/Banner"));
                screen.GetNode<Control>("%InspectPrompt").Visible = false;
                _cardRow = screen.GetNode<Control>("UI/CardRow");
                _alternativeRow = screen.GetNode<Control>("UI/RewardAlternatives");
                break;
            }
            case Mode.ChooseCards:
            {
                NChooseACardSelectionScreen screen =
                    Instantiate<NChooseACardSelectionScreen>(NChooseACardSelectionScreen.ScenePath);
                SetUpBanner(screen.GetNode<NCommonBanner>("Banner"));
                screen.GetNode<Control>("%CombatPiles").Visible = false;
                screen.GetNode<Control>("%InspectPrompt").Visible = false;
                screen.GetNode<Control>("%PeekButton").Visible = false;
                NChoiceSelectionSkipButton skip = screen.GetNode<NChoiceSelectionSkipButton>("SkipButton");
                if (_choice!.CanSkip)
                {
                    skip.AnimateIn();
                }
                else
                {
                    skip.Visible = false;
                }
                _cardRow = screen.GetNode<Control>("CardRow");
                break;
            }
            case Mode.Shop:
                OpenRug();
                break;
            default:
                return;
        }
        ResetCursor();
    }

    /// <summary>
    /// A registered, inert copy of the real screen's scene, added before the cursor so the cursor stays on top. Its
    /// children are ready once added; the screen itself never runs its own _Ready.
    /// </summary>
    private T Instantiate<T>(string scenePath) where T : Control
    {
        T screen = PreloadManager.Cache.GetScene(scenePath).Instantiate<T>(PackedScene.GenEditState.Disabled);
        MirrorScreenPatches.Register(screen);
        screen.Name = $"Mirror{typeof(T).Name}";
        this.AddChildSafely(screen);
        _screen = screen;
        return screen;
    }

    /// <summary>
    /// The merchant's rug, filled with the teammate's stock. Unlike the other screens it keeps its own code: that is
    /// what fills the slots, and nothing in it acts on its own, since buying only happens on a click, which
    /// <see cref="MerchantSlotPatches" /> stops. It is laid out open, the way Open() leaves it, minus the welcome line,
    /// the sound and the screen focus.
    /// </summary>
    private void OpenRug()
    {
        NMerchantInventory rug = PreloadManager.Cache.GetScene(merchantInventoryScene)
            .Instantiate<NMerchantInventory>(PackedScene.GenEditState.Disabled);
        rug.Name = "MirrorMerchantInventory";
        this.AddChildSafely(rug);
        _screen = rug;
        _rug = rug;

        // Its _Ready makes itself the screen-shake target; hand that back to the room we are really in.
        if (NMerchantRoom.Instance is { } room && GodotObject.IsInstanceValid(room))
        {
            NGame.Instance?.SetScreenShakeTarget(room);
        }

        rug.Initialize(_shop!, MerchantRoom.Dialogue);
        _shopEntries = _shop!.AllEntries.ToList();
        rug._slotsContainer.Position = new Vector2(rug._slotsContainer.Position.X, rugOpenY);
        Color shade = rug._backstop.Modulate;
        shade.A = rugBackstopAlpha;
        rug._backstop.Modulate = shade;
        rug.MouseFilter = MouseFilterEnum.Stop;
        foreach (NMerchantCard card in rug.GetCardSlots())
        {
            card.OnInventoryOpened();
        }
    }

    /// <summary>
    /// Takes what the teammate has bought off the rug. Our copy of their stock never hears of it, so it is done here
    /// from what they report, and done every frame: a slot puts itself back on show whenever the gold changes.
    /// </summary>
    private void FillShop()
    {
        if (_rug == null || _player == null)
        {
            return;
        }
        ShopState.Rug state = ShopState.Get(_player);
        foreach (NMerchantSlot slot in _rug.GetAllSlots())
        {
            if (!state.IsSold(_shopEntries.IndexOf(slot.Entry)))
            {
                continue;
            }
            if (slot is NMerchantCardRemoval removal)
            {
                // The removal service stays on the rug and shows itself as used instead.
                if (_shownSold.Add(slot))
                {
                    removal.OnCardRemovalUsed();
                }
                continue;
            }
            slot.Visible = false;
        }
    }

    private static void SetUpBanner(NCommonBanner banner)
    {
        banner.label.SetTextAutoSize(new LocString("gameplay_ui", "CHOOSE_CARD_HEADER").GetRawText());
        banner.AnimateIn();
    }

    private void FillRewards(RewardsSet set)
    {
        if (_rewardList == null)
        {
            return;
        }
        List<Reward> remaining = set.Rewards.Where(r => !r.SuccessfullySelected).ToList();
        if (remaining.SequenceEqual(_shownRewards))
        {
            return;
        }
        ClearRewards();
        foreach (Reward reward in remaining)
        {
            // The screen a button reports back to is only used when a reward is claimed, and claiming is exactly what
            // the button patch stops.
            Control button = reward is LinkedRewardSet linked
                ? NLinkedRewardSet.Create(linked, null!)
                : NRewardButton.Create(reward, null!);
            _rewardList.AddChildSafely(button);
        }
        _shownRewards.AddRange(remaining);
    }

    private void FillCards(CardChoice.Choice choice)
    {
        if (_cardRow == null)
        {
            return;
        }
        IReadOnlyList<CardModel> cards = choice.Cards();
        if (!cards.SequenceEqual(_shownCards))
        {
            ClearCards();
            float spacing = _mode == Mode.RewardCards ? rewardCardSpacing : chooseCardSpacing;
            Vector2 first = Vector2.Left * (cards.Count - 1) * spacing * 0.5f;
            for (int i = 0; i < cards.Count; i++)
            {
                AddCard(cards[i], first + Vector2.Right * spacing * i);
            }
            _shownCards.AddRange(cards);
        }

        if (_alternativeRow != null)
        {
            IReadOnlyList<CardChoice.Alternative> alternatives = choice.Alternatives();
            if (!alternatives.SequenceEqual(_shownAlternatives))
            {
                foreach (Node old in _alternativeRow.GetChildren().OfType<NCardRewardAlternativeButton>())
                {
                    old.QueueFreeSafely();
                }
                foreach (CardChoice.Alternative alternative in alternatives)
                {
                    _alternativeRow.AddChildSafely(NCardRewardAlternativeButton.Create(alternative.Title,
                        alternative.Hotkey));
                }
                _shownAlternatives.Clear();
                _shownAlternatives.AddRange(alternatives);
            }
        }

        // The card being read, by them or by us, comes up to full size the way a hovered holder does.
        Vector2? cursor = CursorPosition();
        CardModel? remoteHovered = cursor == null
            ? null
            : _hitboxes.FirstOrDefault(h => GodotObject.IsInstanceValid(h.Value)
                                            && h.Value.GetGlobalRect().HasPoint(cursor.Value)).Key;
        CardModel? focus = _localHovered ?? remoteHovered;
        foreach ((CardModel card, Control slot) in _slots)
        {
            if (GodotObject.IsInstanceValid(slot))
            {
                slot.Scale = card == focus ? Vector2.One : NCardHolder.smallScale;
                slot.ZIndex = card == focus ? 1 : 0;
            }
        }
    }

    /// <summary>
    /// A card in its own slot on the real card row, where the screen's own holder would be. Card nodes are pooled, so
    /// as in <see cref="NSpectateHand" /> every presentation property lives on a wrapper we own.
    /// </summary>
    private void AddCard(CardModel card, Vector2 position)
    {
        NCard? node = NCard.Create(card);
        if (node == null || _cardRow == null)
        {
            return;
        }
        Control slot = new()
        {
            Name = $"MirrorSlot-{card.Id}",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = position,
            Scale = NCardHolder.smallScale
        };
        _cardRow.AddChildSafely(slot);
        slot.AddChildSafely(node);
        if (node.IsNodeReady())
        {
            node.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        }

        // Stays on the small card's rect whatever the card is doing, so lifting it can't pull it out from under us.
        Vector2 size = NCard.defaultSize * NCardHolder.smallScale;
        CardModel captured = card;
        Control hitbox = new()
        {
            Name = $"MirrorHover-{card.Id}",
            MouseFilter = MouseFilterEnum.Stop,
            Position = position - size * 0.5f,
            Size = size
        };
        _cardRow.AddChildSafely(hitbox);
        hitbox.MouseEntered += () => _localHovered = captured;
        hitbox.MouseExited += () =>
        {
            if (_localHovered == captured)
            {
                _localHovered = null;
            }
        };

        _slots[card] = slot;
        _cards[card] = node;
        _hitboxes[card] = hitbox;
    }

    /// <summary>
    /// Fades out our own screens of the same kind while theirs is up, so ours don't show through the shade under
    /// theirs: our overlays, and our own merchant rug. Only the look changes, so nothing of ours notices. Our map is
    /// never under here: the run UI puts it away while we watch somebody who is not on it.
    /// </summary>
    private void HideOurs(bool hide)
    {
        // Their rug brings its own shade, as it does for them; the overlay one would darken it twice.
        _backstop.Visible = _mode != Mode.Shop;
        if (hide == _hidingOurs)
        {
            return;
        }
        if (hide)
        {
            _hidden.Clear();
            foreach (CanvasItem item in new CanvasItem?[] { NOverlayStack.Instance, NMerchantRoom.Instance?.Inventory }
                         .OfType<CanvasItem>()
                         .Where(GodotObject.IsInstanceValid))
            {
                _hidden[item] = item.Modulate;
                item.Modulate = Colors.Transparent;
            }
        }
        else
        {
            foreach ((CanvasItem item, Color modulate) in _hidden)
            {
                if (GodotObject.IsInstanceValid(item))
                {
                    item.Modulate = modulate;
                }
            }
            _hidden.Clear();
        }
        _hidingOurs = hide;
    }

    /// <summary>
    /// The watched player's cursor, in our space. This copy shares their screen's layout, so it points at the same
    /// thing it points at for them.
    /// </summary>
    private Vector2? CursorPosition()
    {
        if (_player == null || _sync == null)
        {
            return null;
        }
        return _sync.GetControlSpaceFocusPosition(_player.NetId, this);
    }

    /// <summary>The game's own remote cursor, owned by us so it shows whichever screen we are on.</summary>
    private void ResetCursor()
    {
        if (_cursor != null && GodotObject.IsInstanceValid(_cursor))
        {
            _cursor.QueueFreeSafely();
        }
        _cursor = null;
        if (_player == null)
        {
            return;
        }
        _cursor = NRemoteMouseCursor.Create(_player.NetId);
        this.AddChildSafely(_cursor);
        OnInputStateChanged(_player.NetId);
    }

    private void OnInputStateChanged(ulong playerId)
    {
        if (_player == null || playerId != _player.NetId || _sync == null || _cursor == null
            || !GodotObject.IsInstanceValid(_cursor) || !_cursor.IsNodeReady())
        {
            return;
        }
        _cursor.SetNextPosition(_sync.GetControlSpaceFocusPosition(playerId, this));
        _cursor.UpdateImage(_sync.GetMouseDown(playerId), DrawingMode.None);
    }

    private void CloseScreen()
    {
        ClearRewards();
        ClearCards();
        _shownAlternatives.Clear();
        if (_cursor != null && GodotObject.IsInstanceValid(_cursor))
        {
            _cursor.QueueFreeSafely();
        }
        _cursor = null;
        if (_rug != null && GodotObject.IsInstanceValid(_rug))
        {
            // Same as the reward buttons: a slot's tip only goes when the slot loses focus, and freeing it doesn't.
            foreach (NMerchantSlot slot in _rug.GetAllSlots())
            {
                NHoverTipSet.Remove(slot);
            }
        }
        if (_screen != null && GodotObject.IsInstanceValid(_screen))
        {
            _screen.QueueFreeSafely();
        }
        _screen = null;
        _cardRow = null;
        _rewardList = null;
        _alternativeRow = null;
        _rug = null;
        _shopEntries.Clear();
        _shownSold.Clear();
    }

    private void ClearRewards()
    {
        if (_rewardList != null && GodotObject.IsInstanceValid(_rewardList))
        {
            foreach (Node child in _rewardList.GetChildren())
            {
                // A tip shown for a button only goes away when the button loses focus, which freeing it never does.
                if (child is Control control)
                {
                    NHoverTipSet.Remove(control);
                    foreach (Control inner in control.FindChildren("*", owned: false).OfType<Control>())
                    {
                        NHoverTipSet.Remove(inner);
                    }
                }
                child.QueueFreeSafely();
            }
        }
        _shownRewards.Clear();
    }

    private void ClearCards()
    {
        // Cards first: QueueFreeSafely detaches a pooled node right away and hands it back later, so freeing the slot
        // first would take the card down with it.
        foreach (NCard node in _cards.Values)
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.QueueFreeSafely();
            }
        }
        foreach (Control control in _slots.Values.Concat(_hitboxes.Values))
        {
            if (GodotObject.IsInstanceValid(control))
            {
                control.QueueFreeSafely();
            }
        }
        _cards.Clear();
        _slots.Clear();
        _hitboxes.Clear();
        _shownCards.Clear();
        _localHovered = null;
    }
}
