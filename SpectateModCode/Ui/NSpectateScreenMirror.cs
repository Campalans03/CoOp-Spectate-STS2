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
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpectateMod.SpectateModCode.Net;
using SpectateMod.SpectateModCode.Patches;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// A read-only copy of the screen the watched player has up, drawn in place of ours: their rewards, the cards they are
/// picking one of, their shop, their event or Crystal Sphere, their options at the campfire. Their cursor is drawn on
/// top, so you can see what they are pointing at.
/// <para>
/// Each screen is the game's own scene with its code switched off (see <see cref="MirrorScreenPatches" />), so it looks
/// exactly like theirs and shares its layout: their cursor lands on the same card it is on for them. The contents are
/// filled in by hand from pieces that are safe on their own: the game's reward buttons (clicks on a teammate's are
/// stopped by <see cref="RewardButtonPatches" />) and bare card nodes. The few copies that keep their own code (the
/// sphere, the campfire's buttons) get a sheet over them that keeps our mouse off.
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
        Shop,
        Grid,
        Event,
        Bundles,
        Sphere,
        RestSite
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
    /// <summary>What the open copy shows: a RewardsSet, a CardChoice or GridSelection choice, or a MerchantInventory.</summary>
    private object? _source;
    private NCardGrid? _grid;
    private readonly List<CardModel> _shownOptions = new();
    private readonly HashSet<CardModel> _shownMarked = new();
    private NEventLayout? _eventLayout;
    private bool _eventDirty;

    /// <summary>An event whose layout we found we can't copy, so it isn't tried again every frame.</summary>
    private EventModel? _unsupportedEvent;
    private Control? _bundleRow;
    private Control? _bundlePreview;
    private Control? _bundlePreviewCards;
    private int _shownBundle = -1;
    private NMerchantInventory? _rug;
    private List<MerchantEntry> _shopEntries = new();
    private readonly HashSet<NMerchantSlot> _shownSold = new();
    private object? _failed;
    private readonly List<Reward> _shownRewards = new();
    private readonly List<CardModel> _shownCards = new();
    private readonly List<CardChoice.Alternative> _shownAlternatives = new();
    private readonly Dictionary<CardModel, Control> _slots = new();
    private readonly Dictionary<CardModel, NCard> _cards = new();

    /// <summary>Whether our View Upgrades box on their card rewards is ticked. Ours alone; they never hear of it.</summary>
    private bool _showingUpgrades;

    /// <summary>The upgraded copy each offered card shows while <see cref="_showingUpgrades" /> is on.</summary>
    private readonly Dictionary<CardModel, CardModel> _upgradedCopies = new();
    private readonly Dictionary<CardModel, Control> _hitboxes = new();
    private CardModel? _localHovered;

    private Control? _inputBlocker;

    /// <summary>Where each card's tips hang from: the full-size card's rect, on the card's slot.</summary>
    private readonly Dictionary<CardModel, Control> _tipAnchors = new();
    private CardModel? _tipCard;

    private NCrystalSphereScreen? _sphere;
    private CrystalSphereMinigame? _sphereGame;
    private int _sphereHovered = -1;
    private bool _sphereFinished;

    private Control? _restChoicesScreen;
    private Control? _restChoices;
    private MegaRichTextLabel? _restDescription;
    private readonly List<RestSiteOption> _shownRestOptions = new();
    private readonly List<NRestSiteButton> _restButtons = new();
    private int _restHovered = -1;
    private bool _restChoosing;
    private RestSiteOption? _restChosen;
    private RestSiteOption? _restDescribed;
    private bool _restChoicesShown;
    private Tween? _restChoicesTween;
    private Tween? _restDescriptionTween;

    private readonly Dictionary<CanvasItem, Color> _hidden = new();
    private readonly HashSet<CanvasItem> _switchedOff = new();

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
        Mode mode = Mode.None;
        object? source = null;
        try
        {
            (mode, source) = viewed == null ? (Mode.None, null) : WhatIsUp(viewed);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to read a teammate's screen: {e}");
        }

        // A screen that failed to build stays down until something changes, rather than failing again every frame.
        (Player?, Mode, object?) key = (viewed, mode, source);
        if (_failed != null && _failed.Equals(key))
        {
            mode = Mode.None;
            source = null;
        }

        try
        {
            if (mode != _mode || viewed != _player || source != _source)
            {
                CloseScreen();
                _mode = mode;
                _player = mode == Mode.None ? null : viewed;
                _source = mode == Mode.None ? null : source;
                OpenScreen();
            }
            Visible = _mode != Mode.None;
            HideOurs(Visible);

            switch (_source)
            {
                case RewardsSet rewards:
                    FillRewards(rewards);
                    break;
                case CardChoice.Choice { Kind: CardChoice.Kind.Bundle } bundles:
                    FillBundles(bundles);
                    break;
                case CardChoice.Choice choice:
                    FillCards(choice);
                    break;
                case GridSelection.Choice grid:
                    FillGrid(grid);
                    break;
                case MerchantInventory:
                    FillShop();
                    break;
                case EventModel eventModel:
                    FillEvent(eventModel);
                    break;
                case SphereState.Session sphere:
                    FillSphere(sphere);
                    break;
                case IReadOnlyList<RestSiteOption> restOptions:
                    FillRestSite(restOptions);
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
            _source = null;
            Visible = false;
            HideOurs(false);
        }
    }

    /// <summary>
    /// The screen the player has up that we can copy, and what it is showing. In the order they stack for them: a card
    /// choice or a grid opens over their rewards, and all of those over the shop.
    /// </summary>
    private (Mode, object?) WhatIsUp(Player player)
    {
        if (CardChoice.Get(player) is { } choice)
        {
            Mode mode = choice.Kind switch
            {
                CardChoice.Kind.Reward => Mode.RewardCards,
                CardChoice.Kind.Bundle => Mode.Bundles,
                _ => Mode.ChooseCards
            };
            return (mode, choice);
        }
        if (GridSelection.GetChoice(player) is { } grid)
        {
            return (Mode.Grid, grid);
        }
        if (OpenRewardsSet(player) is { } rewards)
        {
            return (Mode.Rewards, rewards);
        }
        if (OpenShop(player) is { } shop)
        {
            return (Mode.Shop, shop);
        }
        // Before the event: the sphere is played inside one, and for us their event is already over.
        if (OpenSphere(player) is { } sphere)
        {
            return (Mode.Sphere, sphere);
        }
        if (OpenEvent(player) is { } eventModel)
        {
            return (Mode.Event, eventModel);
        }
        if (OpenRestSite(player) is { } restOptions)
        {
            return (Mode.RestSite, restOptions);
        }
        return (Mode.None, null);
    }

    /// <summary>
    /// The teammate's Crystal Sphere, while they are playing it. The minigame only runs on their machine: on ours their
    /// event jumps straight to its end. So everything comes from them (<see cref="SphereState" />), and only with the
    /// mod on their side. Not while something of theirs covers it, like their deck: the sphere's own screen reports no
    /// screen of its own, so it reads as the room.
    /// </summary>
    private SphereState.Session? OpenSphere(Player player)
    {
        return SpectateController.Run?.CurrentRoom is EventRoom && IsInRoom(player) ? SphereState.Get(player) : null;
    }

    /// <summary>
    /// The teammate's options at the campfire we are all sitting at, while they are looking at it. Every client builds
    /// every player's options on the way in and hears which one each player points at and picks, so this needs nothing
    /// from their side. The same list object lasts the whole visit, shrinking as they use options up.
    /// </summary>
    private IReadOnlyList<RestSiteOption>? OpenRestSite(Player player)
    {
        if (SpectateController.Run?.CurrentRoom is not RestSiteRoom || NRestSiteRoom.Instance == null
                                                                     || !IsInRoom(player))
        {
            return null;
        }
        try
        {
            return RunManager.Instance.RestSiteSynchronizer.GetOptionsForPlayer(player);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Asked before the campfire has handed out options.
            return null;
        }
    }

    /// <summary>
    /// The teammate's own copy of the event we are all standing in, while they are looking at it. Only for events each
    /// player goes through on their own (Neow, most "?" rooms): a shared one is voted on, and everybody already sees the
    /// same screen with everyone's votes on it.
    /// </summary>
    private EventModel? OpenEvent(Player player)
    {
        if (SpectateController.Run?.CurrentRoom is not EventRoom || NEventRoom.Instance == null
                                                                  || !IsInRoom(player))
        {
            return null;
        }
        EventSynchronizer events = RunManager.Instance.EventSynchronizer;
        EventModel eventModel;
        try
        {
            // Both throw between events rather than returning nothing; that just means there is no event to show.
            if (events.Events.Count == 0 || events.IsShared)
            {
                return null;
            }
            eventModel = events.GetEventForPlayer(player);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return eventModel == _unsupportedEvent ? null : eventModel;
    }

    /// <summary>
    /// The teammate's stock in the shop we are all standing in, while they have the rug rolled out. Every client builds
    /// every player's stock on entering the room, so it is already here; only the rug being open, and what has sold,
    /// comes from them (<see cref="ShopState" />). Not while something of theirs covers the rug, like their deck.
    /// </summary>
    private MerchantInventory? OpenShop(Player player)
    {
        IRunState? run = SpectateController.Run;
        if (run?.CurrentRoom is not MerchantRoom || !ShopState.Get(player).Open
                                                     || !IsInRoom(player))
        {
            return null;
        }
        int slot = run.GetPlayerSlotIndex(player);
        return slot >= 0 ? RunManager.Instance.MerchantSynchronizer.GetInventoryFor(player) : null;
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

    /// <summary>
    /// Whether the player is looking at the room itself, with nothing of theirs over it. The game only sends a player's
    /// screen when it changes, so until they first open something it is still None; at the start of a run, in Neow,
    /// that is what everyone has. None means the room.
    /// </summary>
    private bool IsInRoom(Player player)
    {
        return _sync?.GetScreenType(player.NetId) is NetScreenType.Room or NetScreenType.None;
    }

    private void OpenScreen()
    {
        if (_mode != Mode.None && _player != null)
        {
            MainFile.Logger.Info($"Mirroring {SpectateController.GetDisplayName(_player)}'s {_mode} screen");
        }
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
                if (SpectateConfig.ViewUpgradesBox)
                {
                    // The same box our own reward screen gets (ViewUpgradesPatches), here only ever for us to look with.
                    ViewUpgradesBox.AddTo(screen, ticked =>
                    {
                        _showingUpgrades = ticked;
                        foreach ((CardModel card, NCard node) in _cards)
                        {
                            ShowUpgrade(card, node);
                        }
                        // Taken down so the next frame puts up the tips of what the card now shows.
                        ShowCardTips(null);
                    });
                }
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
                if (((CardChoice.Choice)_source!).CanSkip)
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
            case Mode.Grid:
                OpenGrid((GridSelection.Choice)_source!);
                break;
            case Mode.Bundles:
                OpenBundles((CardChoice.Choice)_source!);
                break;
            case Mode.Event:
                if (!OpenEventLayout((EventModel)_source!))
                {
                    return;
                }
                break;
            case Mode.Sphere:
                OpenSphereScreen((SphereState.Session)_source!);
                break;
            case Mode.RestSite:
                OpenRestSiteChoices();
                break;
            default:
                return;
        }
        ResetCursor();
    }

    /// <summary>
    /// A clear sheet over a copy whose own code runs (the sphere, the rest site buttons), so our mouse never reaches it:
    /// a click there would play the copy as if it were ours. Added after the copy and before the cursor, so the cursor
    /// still draws on top.
    /// </summary>
    private void BlockInput()
    {
        Control sheet = new()
        {
            Name = "MirrorInputBlocker",
            MouseFilter = MouseFilterEnum.Stop
        };
        sheet.SetAnchorsPreset(LayoutPreset.FullRect);
        this.AddChildSafely(sheet);
        _inputBlocker = sheet;
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

        MerchantInventory shop = (MerchantInventory)_source!;
        rug.Initialize(shop, MerchantRoom.Dialogue);
        _shopEntries = shop.AllEntries.ToList();
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

    /// <summary>
    /// Parts of the grid screens that their own code hides or fills on the way in: the confirm previews, the combat piles
    /// and peek button that come up in combat. Looked up by name and skipped where a screen doesn't have one.
    /// </summary>
    private static readonly string[] gridPartsToHide =
    {
        "%PreviewContainer", "%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer",
        "%EnchantSinglePreviewContainer", "%EnchantMultiPreviewContainer", "%CombatPiles", "%PeekButton"
    };

    /// <summary>
    /// The grid screen they have up, dressed the way its own _Ready would: the prompt underneath, the close button only
    /// when they can back out, confirm off until something is picked, and for an enchant, the enchantment beside it.
    /// </summary>
    private void OpenGrid(GridSelection.Choice choice)
    {
        NCardGridSelectionScreen screen = choice.Screen switch
        {
            GridSelection.Screen.Simple => Instantiate<NSimpleCardSelectScreen>(NSimpleCardSelectScreen.ScenePath),
            GridSelection.Screen.CombatPile =>
                Instantiate<NCombatPileCardSelectScreen>(NCombatPileCardSelectScreen.ScenePath),
            GridSelection.Screen.DeckUpgrade =>
                Instantiate<NDeckUpgradeSelectScreen>(NDeckUpgradeSelectScreen.ScenePath),
            GridSelection.Screen.DeckTransform =>
                Instantiate<NDeckTransformSelectScreen>(NDeckTransformSelectScreen.ScenePath),
            GridSelection.Screen.DeckEnchant =>
                Instantiate<NDeckEnchantSelectScreen>(NDeckEnchantSelectScreen.ScenePath),
            _ => Instantiate<NDeckCardSelectScreen>(NDeckCardSelectScreen.ScenePath)
        };

        _grid = screen.GetNode<NCardGrid>("%CardGrid");
        _grid.InsetForTopBar();
        foreach (string part in gridPartsToHide)
        {
            if (screen.GetNodeOrNull<CanvasItem>(part) is { } node)
            {
                node.Visible = false;
            }
        }

        if (screen.GetNodeOrNull<RichTextLabel>("%BottomLabel") is { } prompt)
        {
            prompt.Text = choice.Screen == GridSelection.Screen.DeckEnchant
                ? $"[center]{choice.Prompt}[/center]"
                : choice.Prompt;
        }
        (screen.GetNodeOrNull<NClickableControl>("%Confirm") ?? screen.GetNodeOrNull<NClickableControl>("Confirm"))
            ?.Disable();
        if (screen.GetNodeOrNull<NClickableControl>("%Close") is { } close)
        {
            if (choice.Cancelable)
            {
                close.Enable();
            }
            else
            {
                close.Disable();
            }
        }
        if (screen.GetNodeOrNull<MegaLabel>("%ViewUpgradesLabel") is { } viewUpgrades)
        {
            viewUpgrades.SetTextAutoSize(new LocString("card_selection", "VIEW_UPGRADES").GetFormattedText());
        }
        if (screen.GetNodeOrNull<NTickbox>("%Upgrades") is { } showUpgrades)
        {
            // The one control on the copy that is ours to use: the screen's skipped _Ready would have wired it to its
            // grid, so wire it to ours. It only changes how we see their cards, and nothing about it goes over the
            // network, so either of us can look at the upgrades without the other.
            NCardGrid grid = _grid;
            showUpgrades.IsTicked = false;
            showUpgrades.Toggled += tickbox =>
            {
                if (GodotObject.IsInstanceValid(grid))
                {
                    grid.IsShowingUpgrades = tickbox.IsTicked;
                }
            };
        }
        if (choice.Enchantment is { } enchantment)
        {
            screen.GetNodeOrNull<MegaLabel>("%EnchantmentTitle")?.SetTextAutoSize(
                enchantment.Title.GetFormattedText());
            if (screen.GetNodeOrNull<RichTextLabel>("%EnchantmentDescription") is { } description)
            {
                description.Text = enchantment.DynamicDescription.GetFormattedText();
            }
            if (screen.GetNodeOrNull<TextureRect>("%EnchantmentIcon") is { } icon)
            {
                icon.Texture = enchantment.Icon;
            }
        }
    }

    /// <summary>
    /// Keeps the grid in step with theirs: the same cards (a combat pile can change under it), the ones they have
    /// marked lit up the way their screen lights them, and scrolled to where they are. Their scroll is fed to the grid
    /// as its target, so it glides there as it would for them, and a turn of our own wheel is undone next frame.
    /// </summary>
    private void FillGrid(GridSelection.Choice choice)
    {
        if (_grid == null || _player == null)
        {
            return;
        }
        IReadOnlyList<CardModel> options = choice.Options();
        if (!options.SequenceEqual(_shownOptions))
        {
            _grid.SetCards(options, choice.PileType, choice.Sorting);
            _shownOptions.Clear();
            _shownOptions.AddRange(options);
            _shownMarked.Clear();
        }

        GridSelection.Progress progress = GridSelection.GetProgress(_player);
        HashSet<CardModel> marked = progress.Marked
            .Where(i => i >= 0 && i < options.Count)
            .Select(i => options[i])
            .ToHashSet();
        foreach (CardModel card in _shownMarked.Except(marked).ToList())
        {
            _grid.UnhighlightCard(card);
            _shownMarked.Remove(card);
        }
        foreach (CardModel card in marked.Except(_shownMarked).ToList())
        {
            _grid.HighlightCard(card);
            _shownMarked.Add(card);
        }
        _grid._targetDrag = progress.Scroll;
    }

    /// <summary>
    /// Their event, in the layout scene the event itself asks for, filled in the way NEventRoom fills ours: portrait,
    /// title, the page they are on, their options. What NEventRoom does through SetEvent is done by hand instead, since
    /// SetEvent also runs the event's own room-entry code, which is not ours to run again for them.
    /// <para>
    /// An ancient (Neow) gets its banner and background, but not its music, which ours is already playing, and not its
    /// dialogue: each client picks those lines at random, so we can't know which ones they are reading. Its options are
    /// shown straight away.
    /// </para>
    /// <para>
    /// Events with a layout of their own (custom scenes, or a fight inside the event) aren't copied; the label still
    /// says where they are.
    /// </para>
    /// </summary>
    /// <returns>False when this event can't be copied; the mirror stays down for it.</returns>
    private bool OpenEventLayout(EventModel eventModel)
    {
        Control scene = eventModel.CreateScene().Instantiate<Control>(PackedScene.GenEditState.Disabled);
        if (scene is not NEventLayout layout || scene is NCombatEventLayout)
        {
            scene.QueueFreeSafely();
            _unsupportedEvent = eventModel;
            _mode = Mode.None;
            _player = null;
            _source = null;
            return false;
        }

        MirrorScreenPatches.Register(layout);
        layout.Name = "MirrorEventLayout";
        this.AddChildSafely(layout);
        _screen = layout;
        _eventLayout = layout;
        layout._event = eventModel;

        if (layout is NAncientEventLayout ancient && eventModel is AncientEventModel ancientModel)
        {
            ancient._ancientEvent = ancientModel;
            ancient._ancientNameBanner = NAncientNameBanner.Create(ancientModel);
            ancient.AddChildSafely(ancient._ancientNameBanner);
            ancient.UpdateBannerVisibility();
            foreach (Node child in ancient._ancientBgContainer.GetChildren())
            {
                ancient._ancientBgContainer.RemoveChildSafely(child);
                child.QueueFreeSafely();
            }
            ancient._ancientBgContainer.AddChildSafely(
                ancientModel.CreateBackgroundScene().Instantiate<Control>(PackedScene.GenEditState.Disabled));
        }
        else
        {
            layout.SetPortrait(eventModel.CreateInitialPortrait(),
                eventModel.HasPhobiaModePortrait ? eventModel.CreateInitialPhobiaModePortrait() : null);
        }
        layout.SetTitle(eventModel.Title.GetFormattedText());

        eventModel.StateChanged += OnEventStateChanged;
        _eventDirty = true;
        return true;
    }

    private void OnEventStateChanged(EventModel _)
    {
        _eventDirty = true;
    }

    /// <summary>The page they are on and its options, redone whenever their event moves on (NEventRoom.RefreshEventState).</summary>
    private void FillEvent(EventModel eventModel)
    {
        if (_eventLayout == null || !_eventDirty)
        {
            return;
        }
        _eventDirty = false;

        LocString? description = eventModel.Description;
        if (description != null && description.Exists())
        {
            eventModel.Owner!.Character.AddDetailsTo(description);
            description.Add("IsMultiplayer", eventModel.Owner.RunState.Players.Count > 1);
            eventModel.DynamicVars.AddTo(description);
            _eventLayout.SetDescription(description.GetFormattedText());
        }

        IReadOnlyList<EventOption> options = eventModel.IsFinished
            ? new[] { new EventOption(eventModel, NEventRoom.Proceed, "PROCEED", false, true) }
            : eventModel.CurrentOptions;
        _eventLayout.ClearOptions();
        _eventLayout.AddOptions(options);
        // Lays the content out with the options up, as it does once the last line of dialogue is read.
        if (_eventLayout is NAncientEventLayout ancient)
        {
            Callable.From(ancient.OnSetupComplete).CallDeferred();
        }
    }

    private const string crystalSphereScene = "res://scenes/events/custom/crystal_sphere/crystal_sphere_screen.tscn";

    /// <summary>
    /// Their sphere, in the game's own screen run on a stand-in minigame laid out like theirs: the screen builds its
    /// cells and what lies under them from the stand-in, and clears its fog as the stand-in's cells clear. The stand-in
    /// is only ever told which cells are clear and what the counters say; nothing on it is played, so nothing it holds
    /// is ever paid out (a curse coming to light the normal way adds itself to its owner's deck). The screen's own code
    /// runs, so our mouse is kept off it.
    /// </summary>
    private void OpenSphereScreen(SphereState.Session session)
    {
        // The constructor lays out things of its own with this throwaway generator, and clears the cells around the
        // corners, outside the sphere, just as theirs did. Its things are swapped for theirs below.
        CrystalSphereMinigame game = new(_player!, new Rng(0u), session.Divinations);
        CrystalSphereCell[,] cells = new CrystalSphereCell[session.Width, session.Height];
        for (int x = 0; x < session.Width; x++)
        {
            for (int y = 0; y < session.Height; y++)
            {
                cells[x, y] = new CrystalSphereCell(x, y)
                {
                    IsHidden = x >= game.GridSize.X || y >= game.GridSize.Y || game.cells[x, y].IsHidden
                };
            }
        }
        game._items.Clear();
        foreach (SpectateSphereItem placed in session.Items)
        {
            CrystalSphereItem item = CrystalSphereItem.FromSerializable(placed.item, _player!);
            item.Position = new Vector2I(placed.x, placed.y);
            for (int i = 0; i < item.Size.X; i++)
            {
                for (int j = 0; j < item.Size.Y; j++)
                {
                    int x = placed.x + i;
                    int y = placed.y + j;
                    if (x < session.Width && y < session.Height && cells[x, y].Item == null)
                    {
                        cells[x, y].SetItem(item);
                    }
                }
            }
            game._items.Add(item);
        }
        game.cells = cells;
        MirrorScreenPatches.Register(game);

        NCrystalSphereScreen screen = PreloadManager.Cache.GetScene(crystalSphereScene)
            .Instantiate<NCrystalSphereScreen>(PackedScene.GenEditState.Disabled);
        screen._entity = game;
        screen.Name = "MirrorCrystalSphere";
        this.AddChildSafely(screen);
        _screen = screen;
        _sphere = screen;
        _sphereGame = game;
        _sphereHovered = -1;
        _sphereFinished = false;
        // Their keys would switch the stand-in's tool, and with it what we show of theirs, and a controller could walk
        // into the cells; the mouse is kept off by the sheet.
        screen._bigDivinationButton.SetHotkeys(Array.Empty<string>());
        screen._smallDivinationButton.SetHotkeys(Array.Empty<string>());
        foreach (Control control in screen.FindChildren("*", owned: false).OfType<Control>())
        {
            control.FocusMode = FocusModeEnum.None;
        }
        BlockInput();
    }

    /// <summary>
    /// Brings the stand-in up to what they last sent: the cells they have cleared (the screen fades the fog off them),
    /// the divinations left, the tool in their hand and the cells it would reach from where they point. The crystal
    /// ball's lines are left out, as with an ancient's: each client picks them at random.
    /// </summary>
    private void FillSphere(SphereState.Session session)
    {
        if (_sphere == null || _sphereGame == null || !GodotObject.IsInstanceValid(_sphere))
        {
            return;
        }
        CrystalSphereMinigame game = _sphereGame;
        Vector2I size = game.GridSize;
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                if (session.Revealed[y * size.X + x] && game.cells[x, y].IsHidden)
                {
                    game.cells[x, y].IsHidden = false;
                }
            }
        }
        if (game.DivinationCount != session.Divinations)
        {
            game.DivinationCount = session.Divinations;
        }

        CrystalSphereMinigame.CrystalSphereToolType tool = session.SmallTool
            ? CrystalSphereMinigame.CrystalSphereToolType.Small
            : CrystalSphereMinigame.CrystalSphereToolType.Big;
        if (game.CrystalSphereTool != tool)
        {
            game.SetTool(tool);
        }
        _sphere._bigDivinationButton.SetActive(!session.SmallTool);
        _sphere._smallDivinationButton.SetActive(session.SmallTool);

        int hovered = game.IsFinished ? -1 : session.Hovered;
        if (hovered != _sphereHovered)
        {
            _sphereHovered = hovered;
            if (hovered < 0)
            {
                game.UnsetHoveredCell();
            }
            else
            {
                game.SetHoveredCell(game.cells[hovered % size.X, hovered / size.X]);
            }
        }

        if (game.IsFinished && !_sphereFinished)
        {
            // What the screen does when its minigame finishes, minus what belongs to its player: the proceed button
            // and travel on the map.
            _sphereFinished = true;
            _sphere._bigDivinationButton.Visible = false;
            _sphere._smallDivinationButton.Visible = false;
            _sphere._divinationsLeftLabel.Visible = false;
            _sphere._instructionsContainer.Visible = false;
        }
    }

    private const string restSiteScene = "res://scenes/rooms/rest_site_room.tscn";

    /// <summary>
    /// The choices half of the rest site, from the game's own room scene with its code switched off: the prompt, a row
    /// for their options and the line that describes the one they point at. The room itself, with the campfire and
    /// everyone around it, is the one we are already in, and shows through.
    /// </summary>
    private void OpenRestSiteChoices()
    {
        NRestSiteRoom room = Instantiate<NRestSiteRoom>(restSiteScene);
        room.GetNode<Control>("BgContainer").Visible = false;
        room.GetNode<Control>("%ProceedButton").Visible = false;
        room.GetNode<MegaLabel>("%Header").SetTextAutoSize(new LocString("rest_site_ui", "PROMPT").GetFormattedText());
        _restChoicesScreen = room.GetNode<Control>("%ChoicesScreen");
        _restChoices = room.GetNode<Control>("%ChoicesContainer");
        _restDescription = room.GetNode<MegaRichTextLabel>("%Description");
        _restDescription.Modulate = Colors.Transparent;
        _shownRestOptions.Clear();
        _restButtons.Clear();
        _restHovered = -1;
        _restChoosing = false;
        _restChosen = null;
        _restDescribed = null;
        _restChoicesShown = true;
        // The buttons run their own code, and a click would pick the option as if it were ours.
        BlockInput();

        RestSiteSynchronizer sync = RunManager.Instance.RestSiteSynchronizer;
        sync.BeforePlayerOptionChosen += OnRestSiteOptionChosen;
        sync.AfterPlayerOptionChosen += OnRestSiteOptionDone;
    }

    /// <summary>
    /// They picked an option: their choices fade out while it plays out, as on their screen. Raised from inside the
    /// game carrying out their pick, so it only takes notes; <see cref="FillRestSite" /> acts on them next frame.
    /// </summary>
    private void OnRestSiteOptionChosen(RestSiteOption option, ulong playerId)
    {
        if (_mode == Mode.RestSite && playerId == _player?.NetId)
        {
            _restChoosing = true;
            _restChosen = option;
        }
    }

    /// <summary>Done; what is left of their options comes back, if anything is.</summary>
    private void OnRestSiteOptionDone(RestSiteOption option, bool success, ulong playerId)
    {
        if (_mode == Mode.RestSite && playerId == _player?.NetId)
        {
            _restChoosing = false;
        }
    }

    private void CloseRestSiteChoices()
    {
        if (_restChoicesScreen != null)
        {
            try
            {
                RestSiteSynchronizer sync = RunManager.Instance.RestSiteSynchronizer;
                sync.BeforePlayerOptionChosen -= OnRestSiteOptionChosen;
                sync.AfterPlayerOptionChosen -= OnRestSiteOptionDone;
            }
            catch (Exception)
            {
                // The run can already be gone on the way out; then there is nothing left to listen to.
            }
        }
        _restChoicesTween?.Kill();
        _restDescriptionTween?.Kill();
        _restChoicesTween = null;
        _restDescriptionTween = null;
        _restChoicesScreen = null;
        _restChoices = null;
        _restDescription = null;
        _shownRestOptions.Clear();
        _restButtons.Clear();
        _restHovered = -1;
        _restChoosing = false;
        _restChosen = null;
        _restDescribed = null;
    }

    /// <summary>
    /// Keeps the row in step with theirs: the options they have left, the one they are pointing at raised the way a
    /// hovered button raises, and its description underneath.
    /// </summary>
    private void FillRestSite(IReadOnlyList<RestSiteOption> options)
    {
        if (_restChoices == null || _restChoicesScreen == null || _player == null
            || !GodotObject.IsInstanceValid(_restChoices))
        {
            return;
        }
        if (!options.SequenceEqual(_shownRestOptions))
        {
            foreach (NRestSiteButton old in _restButtons)
            {
                old.QueueFreeSafely();
            }
            _restButtons.Clear();
            foreach (RestSiteOption option in options)
            {
                NRestSiteButton button = NRestSiteButton.Create(option);
                _restChoices.AddChildSafely(button);
                // A controller could walk into it otherwise; the mouse is kept off by the sheet.
                button.FocusMode = FocusModeEnum.None;
                _restButtons.Add(button);
            }
            _shownRestOptions.Clear();
            _shownRestOptions.AddRange(options);
            _restHovered = -1;
        }

        bool showChoices = !_restChoosing && options.Count > 0;
        if (showChoices != _restChoicesShown)
        {
            // NRestSiteRoom.ShowChoices and HideChoices.
            _restChoicesShown = showChoices;
            _restChoicesTween?.Kill();
            _restChoicesTween = _restChoicesScreen.CreateTween();
            _restChoicesTween.TweenProperty(_restChoicesScreen, "modulate:a", showChoices ? 1f : 0f, 0.5)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        }

        int? pointedAt = _restChoosing ? null : RunManager.Instance.RestSiteSynchronizer.GetHoveredOptionIndex(_player.NetId);
        int hovered = pointedAt is { } index && index < _restButtons.Count ? index : -1;
        if (hovered != _restHovered)
        {
            if (_restHovered >= 0 && _restHovered < _restButtons.Count)
            {
                RaiseRestSiteButton(_restButtons[_restHovered], false);
            }
            _restHovered = hovered;
            if (hovered >= 0)
            {
                RaiseRestSiteButton(_restButtons[hovered], true);
            }
        }

        // The line under the row (NRestSiteButton.RefreshTextState): the option being carried out, else the one
        // pointed at, else nothing.
        RestSiteOption? described = _restChoosing ? _restChosen : hovered >= 0 ? _restButtons[hovered].Option : null;
        if (described != _restDescribed)
        {
            _restDescribed = described;
            ShowRestDescription(described);
        }
    }

    private void ShowRestDescription(RestSiteOption? option)
    {
        if (_restDescription == null || !GodotObject.IsInstanceValid(_restDescription))
        {
            return;
        }
        _restDescriptionTween?.Kill();
        if (option != null)
        {
            _restDescription.Modulate = Colors.White;
            _restDescription.Text = option.Description.GetFormattedText();
            return;
        }
        // NRestSiteRoom.FadeOutOptionDescription.
        _restDescriptionTween = _restDescription.CreateTween();
        _restDescriptionTween.TweenProperty(_restDescription, "modulate:a", 0f, 1.0)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
    }

    /// <summary>
    /// What a rest site button does to itself on focus and unfocus (NRestSiteButton.OnFocus and OnUnfocus), without the
    /// rest of focusing: no sound, and no writing into our own room's description.
    /// </summary>
    private static void RaiseRestSiteButton(NRestSiteButton button, bool raised)
    {
        if (!GodotObject.IsInstanceValid(button) || !button.IsNodeReady())
        {
            return;
        }
        button._currentTween?.Kill();
        Tween tween = button.CreateTween().SetParallel();
        button._currentTween = tween;
        if (raised)
        {
            tween.TweenProperty(button._visuals, "scale", Vector2.One * 1.1f, 0.05);
            tween.TweenProperty(button._outline, "scale", Vector2.One, 0.05);
            tween.TweenProperty(button._label, "position", button._labelPosition + new Vector2(0f, 6f), 0.05);
            if (!button._isUnclickable)
            {
                button._hsv.SetShaderParameter(NRestSiteButton._v, 1.2f);
            }
            return;
        }
        tween.TweenProperty(button._visuals, "scale", Vector2.One, 1.0)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tween.TweenProperty(button._label, "position", button._labelPosition, 1.0)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tween.TweenProperty(button._outline, "scale", Vector2.One * 0.9f, 1.0)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        if (!button._isUnclickable)
        {
            tween.TweenMethod(Callable.From<float>(button.UpdateShaderParam), 1.2f, 1f, 1.0)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
    }

    /// <summary>Distance between packs, and between the cards of an opened pack (NChooseABundleSelectionScreen).</summary>
    private const float bundleSpacing = 400f;

    /// <summary>
    /// The pack picker, laid out as its own _Ready lays it out: the packs in a row under the banner, the preview of an
    /// opened pack hidden and its buttons off until they open one.
    /// </summary>
    private void OpenBundles(CardChoice.Choice choice)
    {
        NChooseABundleSelectionScreen screen =
            Instantiate<NChooseABundleSelectionScreen>(NChooseABundleSelectionScreen.ScenePath);
        NCommonBanner banner = screen.GetNode<NCommonBanner>("Banner");
        banner.label.SetTextAutoSize(new LocString("gameplay_ui", "CHOOSE_A_PACK").GetRawText());
        banner.AnimateIn();
        screen.GetNode<Control>("%PeekButton").Visible = false;
        screen.GetNode<NClickableControl>("%Cancel").Disable();
        screen.GetNode<NClickableControl>("%Confirm").Disable();
        _bundlePreview = screen.GetNode<Control>("%BundlePreviewContainer");
        _bundlePreview.Visible = false;
        _bundlePreviewCards = screen.GetNode<Control>("%Cards");
        _bundleRow = screen.GetNode<Control>("%BundleRow");

        // Its clicks go to a handler the skipped _Ready would have connected, so a pack here only reacts to hovering.
        Vector2 first = Vector2.Left * (choice.Bundles.Count - 1) * bundleSpacing * 0.5f;
        for (int i = 0; i < choice.Bundles.Count; i++)
        {
            NCardBundle? bundle = NCardBundle.Create(choice.Bundles[i]);
            if (bundle == null)
            {
                continue;
            }
            _bundleRow.AddChildSafely(bundle);
            bundle.Position += first + Vector2.Right * bundleSpacing * i;
        }
        _shownBundle = -1;
    }

    /// <summary>
    /// Opens the pack they have opened, the way their screen does: the row makes way for that pack's cards laid out
    /// across the preview, with its buttons up. Which pack comes over the grid message, so only with the mod on their
    /// side; without it the row just stays up.
    /// </summary>
    private void FillBundles(CardChoice.Choice choice)
    {
        if (_bundleRow == null || _bundlePreview == null || _bundlePreviewCards == null || _player == null
            || _screen is not NChooseABundleSelectionScreen screen)
        {
            return;
        }
        IReadOnlyList<int> marked = GridSelection.GetProgress(_player).Marked;
        int open = marked.Count > 0 && marked[0] < choice.Bundles.Count ? marked[0] : -1;
        if (open == _shownBundle)
        {
            return;
        }
        _shownBundle = open;

        foreach (Node old in _bundlePreviewCards.GetChildren())
        {
            NHoverTipSet.Remove((Control)old);
            old.QueueFreeSafely();
        }
        bool previewing = open >= 0;
        _bundlePreview.Visible = previewing;
        _bundleRow.Visible = !previewing;
        NClickableControl cancel = screen.GetNode<NClickableControl>("%Cancel");
        NClickableControl confirm = screen.GetNode<NClickableControl>("%Confirm");
        NCommonBanner banner = screen.GetNode<NCommonBanner>("Banner");
        if (!previewing)
        {
            cancel.Disable();
            confirm.Disable();
            banner.AnimateIn();
            return;
        }
        cancel.Enable();
        confirm.Enable();
        banner.AnimateOut();

        IReadOnlyList<CardModel> cards = choice.Bundles[open];
        Vector2 first = Vector2.Right * (cards.Count - 1) * bundleSpacing * 0.5f;
        for (int i = 0; i < cards.Count; i++)
        {
            NCard? card = NCard.Create(cards[i]);
            if (card == null || NPreviewCardHolder.Create(card, showHoverTips: true, scaleOnHover: true) is not { } holder)
            {
                continue;
            }
            _bundlePreviewCards.AddChildSafely(holder);
            holder.Position = first + Vector2.Left * bundleSpacing * i;
            card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
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
            ForgetFreedIcons(reward);
            // The screen a button reports back to is only used when a reward is claimed, and claiming is exactly what
            // the button patch stops.
            Control button = reward is LinkedRewardSet linked
                ? NLinkedRewardSet.Create(linked, null!)
                : NRewardButton.Create(reward, null!);
            _rewardList.AddChildSafely(button);
        }
        _shownRewards.AddRange(remaining);
    }

    /// <summary>
    /// A potion reward makes its icon node once and hands the same one out every time. The last copy of this screen
    /// took it down along with its button, so the next button would be given a freed node and fail to build. Let it
    /// make a fresh one. Only we ever show a teammate's reward, so nothing else is holding on to that node.
    /// </summary>
    private static void ForgetFreedIcons(Reward reward)
    {
        if (reward is LinkedRewardSet linked)
        {
            foreach (Reward inner in linked.Rewards)
            {
                ForgetFreedIcons(inner);
            }
        }
        else if (reward is PotionReward potion && potion._icon != null && !GodotObject.IsInstanceValid(potion._icon))
        {
            potion._icon = null;
        }
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
        ShowCardTips(focus);
    }

    /// <summary>
    /// The keyword and card tips beside the card being read, as a hovered holder shows them (NCardHolder.CreateHoverTips).
    /// Our slots are bare, so nothing else puts them up.
    /// </summary>
    private void ShowCardTips(CardModel? card)
    {
        if (card == _tipCard)
        {
            return;
        }
        if (_tipCard != null && _tipAnchors.TryGetValue(_tipCard, out Control? old) && GodotObject.IsInstanceValid(old))
        {
            NHoverTipSet.Remove(old);
        }
        _tipCard = card;
        if (card != null && _tipAnchors.TryGetValue(card, out Control? anchor) && GodotObject.IsInstanceValid(anchor))
        {
            // The tips of what the card shows, which is its upgraded copy while View Upgrades is ticked.
            CardModel shown = _cards.TryGetValue(card, out NCard? node) && GodotObject.IsInstanceValid(node)
                                                                       && node.Model != null
                ? node.Model
                : card;
            NHoverTipSet.CreateAndShow(anchor, shown.HoverTips, HoverTip.GetHoverTipAlignment(anchor));
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

        // The full-size card's rect, which the tips line up against once the card is lifted.
        Control tipAnchor = new()
        {
            Name = "TipAnchor",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = -NCard.defaultSize * 0.5f,
            Size = NCard.defaultSize
        };
        slot.AddChildSafely(tipAnchor);
        _tipAnchors[card] = tipAnchor;

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
        if (_showingUpgrades)
        {
            ShowUpgrade(card, node);
        }
    }

    /// <summary>
    /// Shows a card upgraded or as it is, the way NGridCardHolder.SetIsPreviewingUpgrade does: by putting an upgraded
    /// copy on the card node. The node's model is one of the things the pool clears, so the copy can't outlive us.
    /// </summary>
    private void ShowUpgrade(CardModel card, NCard node)
    {
        if (!GodotObject.IsInstanceValid(node) || !node.IsNodeReady())
        {
            return;
        }
        if (_showingUpgrades && card.IsUpgradable)
        {
            if (!_upgradedCopies.TryGetValue(card, out CardModel? upgraded))
            {
                upgraded = (CardModel)card.MutableClone();
                upgraded.UpgradeInternal();
                _upgradedCopies[card] = upgraded;
            }
            node.Model = upgraded;
            node.ShowUpgradePreview();
        }
        else if (node.Model != card)
        {
            node.Model = card;
            node.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        }
    }

    /// <summary>
    /// Fades out our own screens that would show through or under theirs: our overlays, our own merchant rug, and in an
    /// event our own event screen, which sits right where theirs is drawn. Only the look changes, so nothing of ours
    /// notices. At the campfire our own options and proceed button are switched off instead (see below). Worked out
    /// every frame, since what is on show can change under an open copy (a grid opening over an event). Our map is
    /// never under here: the run UI puts it away while we watch somebody who is not on it.
    /// </summary>
    private void HideOurs(bool hide)
    {
        // The shade always takes the mouse, so nothing of ours can be clicked through a copy; it is only seen where the
        // real screen has the overlay shade too. A rug, an event, the sphere or the campfire brings its own look.
        Color shade = _backstop.Modulate;
        shade.A = _mode is Mode.Shop or Mode.Event or Mode.Sphere or Mode.RestSite ? 0f : 1f;
        _backstop.Modulate = shade;

        // Our campfire's options are faded in and out by our own room (after picking one, say), which would bring them
        // back over theirs; they are switched off instead. Nothing of the room's looks at whether they are visible.
        NRestSiteRoom? restSite = hide && _mode == Mode.RestSite ? NRestSiteRoom.Instance : null;
        HashSet<CanvasItem> off = new CanvasItem?[] { restSite?._choicesScreen, restSite?._proceedButton }
            .OfType<CanvasItem>()
            .Where(GodotObject.IsInstanceValid)
            .ToHashSet();
        foreach (CanvasItem item in _switchedOff.Where(s => !off.Contains(s)).ToList())
        {
            if (GodotObject.IsInstanceValid(item))
            {
                item.Visible = true;
            }
            _switchedOff.Remove(item);
        }
        foreach (CanvasItem item in off)
        {
            if (_switchedOff.Add(item))
            {
                item.Visible = false;
            }
        }

        HashSet<CanvasItem> wanted = hide
            ? new CanvasItem?[]
                {
                    NOverlayStack.Instance,
                    NMerchantRoom.Instance?.Inventory,
                    _mode is Mode.Event or Mode.Sphere ? NEventRoom.Instance?.Layout : null
                }
                .OfType<CanvasItem>()
                .Where(GodotObject.IsInstanceValid)
                .ToHashSet()
            : new HashSet<CanvasItem>();

        foreach ((CanvasItem item, Color modulate) in _hidden.Where(h => !wanted.Contains(h.Key)).ToList())
        {
            if (GodotObject.IsInstanceValid(item))
            {
                item.Modulate = modulate;
            }
            _hidden.Remove(item);
        }
        foreach (CanvasItem item in wanted.Where(w => !_hidden.ContainsKey(w)))
        {
            _hidden[item] = item.Modulate;
            item.Modulate = Colors.Transparent;
        }
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
        _showingUpgrades = false;
        _upgradedCopies.Clear();
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
        _grid = null;
        _shownOptions.Clear();
        _shownMarked.Clear();
        if (_eventLayout?._event is { } eventModel)
        {
            eventModel.StateChanged -= OnEventStateChanged;
        }
        _eventLayout = null;
        _eventDirty = false;
        _bundleRow = null;
        _bundlePreview = null;
        _bundlePreviewCards = null;
        _shownBundle = -1;
        _sphere = null;
        _sphereGame = null;
        _sphereHovered = -1;
        _sphereFinished = false;
        CloseRestSiteChoices();
        if (_inputBlocker != null && GodotObject.IsInstanceValid(_inputBlocker))
        {
            _inputBlocker.QueueFreeSafely();
        }
        _inputBlocker = null;
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
        ShowCardTips(null);
        _tipAnchors.Clear();
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
