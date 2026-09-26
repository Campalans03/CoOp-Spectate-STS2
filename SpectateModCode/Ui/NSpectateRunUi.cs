using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMod.SpectateModCode.Ui;

/// <summary>
/// The half of spectating that lives for the whole run, from the start of a multiplayer run to its end: the player
/// boxes, the "Spectating" label and red border, and the relics and potions in the top bar. Inside combat,
/// <see cref="SpectateView" /> adds the hand and the combat UI on top.
/// <para>
/// It sits last in the run's global UI, so it draws over rooms, overlays and the map alike. It never takes the mouse
/// itself; only the player boxes do.
/// </para>
/// </summary>
public partial class NSpectateRunUi : Control
{
    public const string NodeName = "SpectateRunUi";

    private const float borderThickness = 6f;

    private static readonly Color borderColor = new(0.85f, 0.15f, 0.15f, 0.75f);

    private readonly List<ColorRect> _border = new();

    private IRunState _run = null!;
    private Label _label = null!;
    private NSpectatePotionBelt? _potionBelt;
    private NSpectateScreenMirror? _mirror;
    private NetScreenType _localScreen;

    /// <summary>Set when we put our own map away to show somebody else, so it comes back when we do.</summary>
    private bool _reopenMap;

    /// <summary>Where we were when the map was put away; it only comes back if we are still there.</summary>
    private MapCoord? _reopenMapAt;

    public static NSpectateRunUi Create(IRunState run)
    {
        NSpectateRunUi node = new()
        {
            Name = NodeName,
            MouseFilter = MouseFilterEnum.Ignore
        };
        node._run = run;
        node.SetAnchorsPreset(LayoutPreset.FullRect);
        node.Build();
        return node;
    }

    /// <summary>Children are built up-front rather than in _Ready so the node is usable before it is in the tree.</summary>
    private void Build()
    {
        _label = new Label
        {
            Name = "SpectateLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false
        };
        _label.SetAnchorsPreset(LayoutPreset.TopWide);
        _label.OffsetTop = 24f;
        _label.OffsetBottom = 90f;
        _label.AddThemeFontSizeOverride("font_size", 40);
        _label.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.86f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
        _label.AddThemeConstantOverride("outline_size", 8);
        this.AddChildSafely(_label);

        foreach (LayoutPreset preset in new[]
                 {
                     LayoutPreset.TopWide, LayoutPreset.BottomWide, LayoutPreset.LeftWide, LayoutPreset.RightWide
                 })
        {
            ColorRect rect = new()
            {
                Color = borderColor,
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false
            };
            rect.SetAnchorsPreset(preset);
            bool horizontal = preset is LayoutPreset.TopWide or LayoutPreset.BottomWide;
            if (horizontal)
            {
                rect.OffsetTop = preset == LayoutPreset.TopWide ? 0f : -borderThickness;
                rect.OffsetBottom = preset == LayoutPreset.TopWide ? borderThickness : 0f;
            }
            else
            {
                rect.OffsetLeft = preset == LayoutPreset.LeftWide ? 0f : -borderThickness;
                rect.OffsetRight = preset == LayoutPreset.LeftWide ? borderThickness : 0f;
            }
            _border.Add(rect);
            this.AddChildSafely(rect);
        }

        // Built even when we are not spectating: somebody may be watching us, and what they draw lands on our screen.
        // Up here it covers every screen of the run, mirrored ones included.
        NSpectateDrawLayer drawings = NSpectateDrawLayer.Create();
        this.AddChildSafely(drawings);
        this.AddChildSafely(NSpectateInput.Create(drawings));
        this.AddChildSafely(NSpectateHotkeys.Create(_run));

        // Last, so the boxes sit above everything else here; the rest ignores the mouse, but the boxes have to get it.
        this.AddChildSafely(NSpectatePeekBar.Create(_run));

        this.AddChildSafely(NSpectateShopSync.Create());
        this.AddChildSafely(NSpectateGridSync.Create());
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        SpectateController.AttachToRun(_run);
        SpectateController.ViewChanged += OnViewChanged;
    }

    /// <summary>
    /// The mirror of a teammate's screen goes in the global UI rather than in here, right above the overlay stack where
    /// the real screens live (and above the map, so it covers ours), so like theirs it stays under what comes later:
    /// the top bar, the party list, the menus.
    /// </summary>
    public override void _Ready()
    {
        base._Ready();
        if (GetParent() is not NGlobalUi globalUi)
        {
            return;
        }
        _mirror = NSpectateScreenMirror.Create();
        globalUi.AddChildSafely(_mirror);
        int above = Math.Max(globalUi.Overlays.GetIndex(), globalUi.MapScreen.GetIndex());
        globalUi.MoveChildSafely(_mirror, above + 1);
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        // Detach while still listening: letting go of the target is what puts our relics and potions back.
        try
        {
            SpectateController.DetachFromRun();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to detach spectating from the run: {e}");
        }
        SpectateController.ViewChanged -= OnViewChanged;
        ApplyRelics(null);
        ApplyTopBar(null);
        ApplyPotions(null);
        if (_mirror != null && GodotObject.IsInstanceValid(_mirror))
        {
            _mirror.QueueFreeSafely();
        }
        _mirror = null;
    }

    /// <summary>
    /// Screens we can only get to by opening them ourselves. Opening one while watching somebody means we want our own
    /// view back; leaving the spectate on would put our screen under their name. Menus (pause, settings) are left out:
    /// they sit over everything and say nothing about whose screen is under them.
    /// </summary>
    private static readonly HashSet<NetScreenType> ownScreens = new()
    {
        NetScreenType.Map,
        NetScreenType.DeckView,
        NetScreenType.Compendium,
        NetScreenType.RemotePlayerExpandedState
    };

    /// <summary>
    /// Watching somebody (for good or just for a look) puts our map away, so what is on screen is their view and not
    /// our map with theirs behind it. Nobody on the map can be watched, so they are never on it themselves. Opening the
    /// map ourselves while watching ends the watching first (<see cref="StopOnOwnScreen" />), so this never fights with
    /// it. The map comes back once we are looking at our own screen again (<see cref="ReopenMap" />).
    /// </summary>
    private void CloseMapWhileWatching()
    {
        if (SpectateController.Viewed == null || NMapScreen.Instance is not { IsOpen: true } map)
        {
            return;
        }
        map.Close();
        _reopenMap = true;
        _reopenMapAt = _run.CurrentMapCoord;
    }

    /// <summary>
    /// Puts back the map <see cref="CloseMapWhileWatching" /> put away. Opened as if from the top bar, so it skips the
    /// start-of-act animation and just shows the map where we left it.
    /// </summary>
    private void ReopenMap()
    {
        if (!_reopenMap)
        {
            return;
        }
        _reopenMap = false;
        NMapScreen? map = NMapScreen.Instance;
        if (map != null && GodotObject.IsInstanceValid(map) && !map.IsOpen && _run.CurrentMapCoord == _reopenMapAt)
        {
            map.Open(isOpenedFromTopBar: true);
        }
    }

    private void StopOnOwnScreen()
    {
        PeerInputSynchronizer? sync = RunManager.Instance.InputSynchronizer;
        Player? me = SpectateController.Me;
        if (sync == null || me == null)
        {
            return;
        }
        NetScreenType screen = sync.GetScreenType(me.NetId);
        if (screen == _localScreen)
        {
            return;
        }
        _localScreen = screen;
        if (SpectateController.IsSpectating && ownScreens.Contains(screen))
        {
            SpectateController.Stop();
        }
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        StopOnOwnScreen();
        SpectateController.StopWatchingPlayersOnMap();
        CloseMapWhileWatching();
        // Which screen the teammate is on changes without any event of ours, so the label is re-read every frame.
        Player? viewed = SpectateController.Viewed;
        bool showLabel = viewed != null && SpectateConfig.ShowSpectatingLabel;
        _label.Visible = showLabel;
        if (showLabel)
        {
            string text = Describe(viewed!);
            if (_label.Text != text)
            {
                _label.Text = text;
            }
        }
    }

    private void OnViewChanged(Player? viewed)
    {
        if (viewed == null)
        {
            ReopenMap();
        }
        ApplyRelics(viewed);
        ApplyPotions(viewed);
        ApplyTopBar(viewed);
        bool showBorder = viewed != null && SpectateConfig.ShowSpectatingBorder;
        foreach (ColorRect rect in _border)
        {
            rect.Visible = showBorder;
        }
    }

    /// <summary>
    /// The relic row sits in the run's top bar, so it is guarded on its own: a failure here only costs the relics, not
    /// the spectate.
    /// </summary>
    private static void ApplyRelics(Player? viewed)
    {
        Player? me = SpectateController.Me;
        Player? owner = viewed != null && SpectateConfig.ReplaceRelics ? viewed : me;
        if (owner == null)
        {
            return;
        }
        try
        {
            RelicView.Show(owner);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to swap the relic row: {e}");
        }
    }


    /// <summary>Gold, hit points and portrait; <see cref="TopBarView" /> guards each part on its own.</summary>
    private static void ApplyTopBar(Player? viewed)
    {
        Player? owner = viewed != null && SpectateConfig.ReplaceGoldAndHp ? viewed : SpectateController.Me;
        if (owner != null)
        {
            TopBarView.Show(owner);
        }
    }
    /// <summary>Same idea as <see cref="ApplyRelics" />, but through a read-only copy: see <see cref="NSpectatePotionBelt" />.</summary>
    private void ApplyPotions(Player? viewed)
    {
        try
        {
            NSpectatePotionBelt.Show(ref _potionBelt, viewed != null && SpectateConfig.ReplacePotions ? viewed : null);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to swap the potion belt: {e}");
            _potionBelt = null;
        }
    }

    private string Describe(Player viewed)
    {
        string text = $"Spectating {SpectateController.GetDisplayName(viewed)}";
        string? where = DescribeScreen(viewed);
        return where == null ? text : $"{text} - {where}";
    }

    /// <summary>
    /// Where the player is, from the screen the game already syncs for every peer. Null when there is nothing to add:
    /// in combat their screen is our screen.
    /// </summary>
    private string? DescribeScreen(Player player)
    {
        PeerInputSynchronizer? sync = RunManager.Instance.InputSynchronizer;
        NetScreenType screen = sync?.GetScreenType(player.NetId) ?? NetScreenType.None;
        return screen switch
        {
            NetScreenType.Map => "on the map",
            NetScreenType.Rewards => "choosing rewards",
            NetScreenType.CardSelection => "choosing cards",
            NetScreenType.DeckView => "looking at their deck",
            NetScreenType.CardPile or NetScreenType.SimpleCardsView => "looking at cards",
            NetScreenType.SharedRelicPicking => "picking a relic",
            NetScreenType.Settings or NetScreenType.PauseMenu or NetScreenType.Feedback => "in the menu",
            NetScreenType.Compendium => "in the compendium",
            NetScreenType.RemotePlayerExpandedState => "looking at a teammate",
            NetScreenType.GameOver => null,
            _ => SpectateController.State != null ? null : DescribeRoom()
        };
    }

    /// <summary>Everybody is always in the same room, so where they are is where we are.</summary>
    private string? DescribeRoom()
    {
        return _run.CurrentRoom?.RoomType switch
        {
            RoomType.Shop => "in the shop",
            RoomType.Treasure => "at the treasure",
            RoomType.Event => "in an event",
            RoomType.RestSite => "at the rest site",
            _ => null
        };
    }
}
