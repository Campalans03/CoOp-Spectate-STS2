using BaseLib.Config;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// User-facing options. BaseLib generates the settings UI from the public properties and persists them to
/// <c>user://mod_configs/SpectateMod.cfg</c>. Labels come from <c>SPECTATEMOD-*.title</c> entries in
/// <c>SpectateMod/localization/&lt;lang&gt;/settings_ui.json</c>, falling back to the property name.
/// </summary>
public class SpectateConfig : SimpleModConfig
{
    public static SpectateConfig Instance { get; } = new();

    [ConfigSection("Spectating")]
    public bool AutoSpectateAfterEndTurn { get; set; } = true;

    public bool ManualSpectateOnClick { get; set; } = true;

    /// <summary>
    /// Preferred teammate for auto-spectate, as a 1-based player slot. 0 means "first eligible teammate".
    /// If the preferred teammate is dead or has already ended their turn, we fall back to any other eligible one.
    /// </summary>
    [ConfigSlider(0.0, 3.0)]
    public int PreferredTeammateSlot { get; set; }

    [ConfigSection("What gets replaced")]
    public bool ReplaceHand { get; set; } = true;

    public bool ReplaceCombatUi { get; set; } = true;

    public bool MirrorHover { get; set; } = true;

    public bool ShowCardPlayVisuals { get; set; } = true;

    [ConfigSection("Indicators")]
    public bool ShowSpectatingLabel { get; set; } = true;

    public bool ShowSpectatingBorder { get; set; } = true;
}
