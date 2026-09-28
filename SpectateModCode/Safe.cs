using System;
using System.Collections.Generic;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// The wall between the mod and the game. Much of the mod runs inside the game's own code: event handlers raised in
/// the middle of a turn change, a draw or a discard, and Harmony patches on methods the game calls every frame. An
/// exception escaping from there aborts whatever the game was doing, and that can leave a turn that never ends or a
/// hand that is never drawn. Everything the mod runs from inside the game goes through here, so a failure only ever
/// costs the mod's own feature.
/// <para>
/// The work is always passed as a delegate, never inlined: a method that touches a game member missing from the
/// running build fails when it is compiled, which is before any try in that same method has started.
/// </para>
/// </summary>
public static class Safe
{
    /// <summary>How many times one spot may log before it goes quiet; some of them run every frame.</summary>
    private const int maxLogsPerSpot = 3;

    private static readonly Dictionary<string, int> logCounts = new();

    public static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log(what, e);
        }
    }

    /// <summary>Runs <paramref name="func" />, or hands back <paramref name="fallback" /> if it throws.</summary>
    public static T Run<T>(string what, Func<T> func, T fallback)
    {
        try
        {
            return func();
        }
        catch (Exception e)
        {
            Log(what, e);
            return fallback;
        }
    }

    public static void Log(string what, Exception e)
    {
        try
        {
            logCounts.TryGetValue(what, out int count);
            if (count >= maxLogsPerSpot)
            {
                return;
            }
            logCounts[what] = count + 1;
            string suffix = count + 1 == maxLogsPerSpot ? " (further errors here are not logged)" : "";
            MainFile.Logger.Error($"Failed to {what}{suffix}: {e}");
        }
        catch (Exception)
        {
            // Logging must never be the thing that breaks the game.
        }
    }
}
