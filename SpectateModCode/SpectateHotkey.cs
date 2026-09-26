using Godot;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// The keys a spectate hotkey can be set to. BaseLib's settings have no "press a key" control, only dropdowns over an
/// enum, so this is the curated list the dropdown offers. Every value is the matching Godot <see cref="Key" />, so a
/// cast turns one into the other. Labels come from <c>SPECTATEMOD-HOTKEY.&lt;name&gt;</c> in settings_ui.json, falling
/// back to the name.
/// </summary>
public enum SpectateHotkey : long
{
    None = Key.None,

    Key1 = Key.Key1,
    Key2 = Key.Key2,
    Key3 = Key.Key3,
    Key4 = Key.Key4,
    Key5 = Key.Key5,
    Key6 = Key.Key6,
    Key7 = Key.Key7,
    Key8 = Key.Key8,
    Key9 = Key.Key9,
    Key0 = Key.Key0,

    F1 = Key.F1,
    F2 = Key.F2,
    F3 = Key.F3,
    F4 = Key.F4,
    F5 = Key.F5,
    F6 = Key.F6,
    F7 = Key.F7,
    F8 = Key.F8,
    F9 = Key.F9,
    F10 = Key.F10,
    F11 = Key.F11,
    F12 = Key.F12,

    Kp1 = Key.Kp1,
    Kp2 = Key.Kp2,
    Kp3 = Key.Kp3,
    Kp4 = Key.Kp4,
    Kp5 = Key.Kp5,
    Kp6 = Key.Kp6,
    Kp7 = Key.Kp7,
    Kp8 = Key.Kp8,
    Kp9 = Key.Kp9,
    Kp0 = Key.Kp0,

    A = Key.A,
    B = Key.B,
    C = Key.C,
    D = Key.D,
    E = Key.E,
    F = Key.F,
    G = Key.G,
    H = Key.H,
    I = Key.I,
    J = Key.J,
    K = Key.K,
    L = Key.L,
    M = Key.M,
    N = Key.N,
    O = Key.O,
    P = Key.P,
    Q = Key.Q,
    R = Key.R,
    S = Key.S,
    T = Key.T,
    U = Key.U,
    V = Key.V,
    W = Key.W,
    X = Key.X,
    Y = Key.Y,
    Z = Key.Z,

    Tab = Key.Tab,
    Backspace = Key.Backspace,
    Quoteleft = Key.Quoteleft
}

/// <summary>The key that has to be held with every spectate hotkey. One setting for all of them.</summary>
public enum SpectateHotkeyModifier
{
    None,
    Ctrl,
    Shift,
    Alt
}
