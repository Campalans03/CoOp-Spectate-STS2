using BaseLib.Config;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// User-facing options. BaseLib generates the settings UI from the public properties and persists them to
/// <c>user://mod_configs/SpectateMod.cfg</c>. Labels come from <c>SPECTATEMOD-*.title</c> entries in
/// <c>SpectateMod/localization/&lt;lang&gt;/settings_ui.json</c>, falling back to the property name.
/// <para>BaseLib only picks up <b>static</b> properties; instance ones are logged as ignored and never appear.</para>
/// </summary>
public class SpectateConfig : SimpleModConfig
{
    private static SpectateConfig? _instance;

    /// <summary>
    /// Created lazily on first access rather than in a static field initializer. ModConfig's constructor snapshots
    /// every option's current value as its default, and static field initializers run in declaration order — so an
    /// eagerly created instance would be built before the options below were assigned and would record every default
    /// as false/0. Going through the property guarantees the type is fully initialized first.
    /// </summary>
    public static SpectateConfig Instance => _instance ??= new SpectateConfig();

    [ConfigSection("Spectating")]
    public static bool AutoSpectateAfterEndTurn { get; set; } = true;

    public static bool ManualSpectateOnClick { get; set; } = true;

    /// <summary>Eye button next to each player's icon in the top-left party list.</summary>
    public static bool ShowSpectateButtons { get; set; } = true;

    /// <summary>
    /// Start watching a teammate the moment you die, without waiting for the end-of-turn switch. Prefers someone who
    /// is still taking their turn, but will settle for any teammate who is alive.
    /// </summary>
    public static bool AutoSpectateOnDeath { get; set; } = true;

    /// <summary>
    /// Preferred teammate for auto-spectate, as a 1-based position in slot order. 0 means "first eligible teammate".
    /// If the preferred teammate is dead or has already ended their turn, we fall back to another eligible one.
    /// </summary>
    [ConfigSlider(0.0, 3.0)]
    public static int PreferredTeammateSlot { get; set; }

    [ConfigSection("What gets replaced")]
    public static bool ReplaceHand { get; set; } = true;

    public static bool ReplaceCombatUi { get; set; } = true;

    public static bool MirrorHover { get; set; } = true;

    /// <summary>Let our own mouse lift the teammate's cards so we can read them without waiting for them to hover.</summary>
    public static bool InspectTeammateCards { get; set; } = true;

    public static bool ShowCardPlayVisuals { get; set; } = true;

    [ConfigSection("Indicators")]
    public static bool ShowSpectatingLabel { get; set; } = true;

    public static bool ShowSpectatingBorder { get; set; } = true;

    /// <summary>A small copy of our own energy orb next to the teammate's, so we can see what we have left.</summary>
    public static bool ShowOwnEnergy { get; set; } = true;
}
