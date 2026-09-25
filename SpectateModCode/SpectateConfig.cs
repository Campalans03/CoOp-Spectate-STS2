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
    public static SpectateConfig Instance { get; } = new();

    [ConfigSection("Spectating")]
    public static bool AutoSpectateAfterEndTurn { get; set; } = true;

    public static bool ManualSpectateOnClick { get; set; } = true;

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

    public static bool ShowCardPlayVisuals { get; set; } = true;

    [ConfigSection("Indicators")]
    public static bool ShowSpectatingLabel { get; set; } = true;

    public static bool ShowSpectatingBorder { get; set; } = true;
}
