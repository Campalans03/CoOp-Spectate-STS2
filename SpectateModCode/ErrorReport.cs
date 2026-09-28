using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;

namespace SpectateMod.SpectateModCode;

/// <summary>
/// Builds a short, pasteable bug report: versions, the mods loaded alongside, and the errors from this session's log
/// and the one before it (a player usually restarts the game after something breaks, then goes looking for the
/// button). Errors involving this mod come first and in full; others only as one line each, as hints of a clash.
/// Steam ids, the Windows user name and URL tokens are blanked out.
/// </summary>
public static class ErrorReport
{
    /// <summary>Most of the report's size goes to stack traces; this keeps it pasteable in one go.</summary>
    private const int maxChars = 12000;

    private const int maxLinesPerError = 25;

    private const int maxOtherErrors = 10;

    private sealed record Block(string Text, bool Ours)
    {
        public int Count { get; set; } = 1;
    }

    public static string Build()
    {
        StringBuilder report = new();
        report.AppendLine("=== Co-op Spectate error report ===");
        report.AppendLine($"Mod: {Safe.Run("read the mod version", ModVersion, "unknown")}");
        report.AppendLine($"Game: {Safe.Run("read the game version", GameVersion, "unknown")}");
        report.AppendLine($"Mods: {Safe.Run("list the loaded mods", ModList, "unknown")}");

        foreach ((string label, string path) in Safe.Run("find the logs", LogFiles, new List<(string, string)>()))
        {
            report.AppendLine();
            report.AppendLine($"=== {label} ===");
            report.Append(Safe.Run("read a log", () => Errors(path), "(could not read the log)\n"));
        }

        string text = Redact(report.ToString());
        return text.Length <= maxChars ? text : text[..maxChars] + "\n... (cut short)";
    }

    private static string ModVersion()
    {
        Mod? mod = ModManager.GetLoadedMods().FirstOrDefault(m => m.manifest?.id == MainFile.ModId);
        return mod?.manifest?.version ?? "unknown";
    }

    private static string GameVersion()
    {
        ReleaseInfo? info = ReleaseInfoManager.Instance.ReleaseInfo;
        return info == null ? "unknown" : $"{info.Version} (branch {info.Branch}, commit {info.Commit})";
    }

    private static string ModList()
    {
        return string.Join(", ", ModManager.GetLoadedMods()
            .Select(m => $"{m.manifest?.name ?? m.manifest?.id ?? "?"} {m.manifest?.version}".Trim()));
    }

    /// <summary>This session's log, and the newest one kept from an earlier session.</summary>
    private static List<(string label, string path)> LogFiles()
    {
        string dir = Path.Combine(OS.GetUserDataDir(), "logs");
        List<(string, string)> files = new();
        string current = Path.Combine(dir, "godot.log");
        if (File.Exists(current))
        {
            files.Add(("Errors this session", current));
        }
        string? previous = Directory.Exists(dir)
            ? new DirectoryInfo(dir).GetFiles("godot*.log")
                .Where(f => f.Name != "godot.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
            : null;
        if (previous != null)
        {
            files.Add(("Errors last session", previous));
        }
        return files;
    }

    /// <summary>
    /// Every [ERROR] entry with the indented lines after it (its stack trace), identical ones counted once. Ours in
    /// full, newest last; the rest trimmed to their first line.
    /// </summary>
    private static string Errors(string path)
    {
        List<Block> blocks = new();
        Dictionary<string, Block> seen = new();
        List<string>? current = null;

        void Finish()
        {
            if (current == null)
            {
                return;
            }
            string text = string.Join("\n", current.Take(maxLinesPerError));
            if (current.Count > maxLinesPerError)
            {
                text += $"\n    ... ({current.Count - maxLinesPerError} more lines)";
            }
            if (seen.TryGetValue(text, out Block? existing))
            {
                existing.Count++;
            }
            else
            {
                Block block = new(text, text.Contains("SpectateMod"));
                seen[text] = block;
                blocks.Add(block);
            }
            current = null;
        }

        foreach (string line in ReadShared(path))
        {
            if (line.StartsWith("[ERROR]"))
            {
                Finish();
                current = new List<string> { line };
            }
            else if (current != null && line.Length > 0 && !line.StartsWith("["))
            {
                // .NET's own async plumbing makes up most of a trace and says nothing about what went wrong.
                if (!line.TrimStart().StartsWith("at System."))
                {
                    current.Add(line);
                }
            }
            else
            {
                Finish();
            }
        }
        Finish();

        List<Block> ours = blocks.Where(b => b.Ours).ToList();
        List<Block> others = blocks.Where(b => !b.Ours).TakeLast(maxOtherErrors).ToList();
        if (ours.Count == 0 && others.Count == 0)
        {
            return "(no errors)\n";
        }

        StringBuilder text = new();
        text.AppendLine(ours.Count == 0 ? "(none from this mod)" : $"From this mod ({ours.Count}):");
        foreach (Block block in ours)
        {
            text.AppendLine(block.Count > 1 ? $"[x{block.Count}] {block.Text}" : block.Text);
        }
        if (others.Count > 0)
        {
            text.AppendLine($"Other errors (last {others.Count}, first line only):");
            foreach (Block block in others)
            {
                string first = block.Text.Split('\n')[0];
                text.AppendLine(block.Count > 1 ? $"[x{block.Count}] {first}" : first);
            }
        }
        return text.ToString();
    }

    /// <summary>The game still has its own log open for writing, so open it without asking for exclusive access.</summary>
    private static IEnumerable<string> ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static readonly Regex steamId = new(@"\b7656119\d{10}\b");
    private static readonly Regex userPath = new(@"(?i)([a-z]:[\\/]+users[\\/]+|/home/|/Users/)[^\\/\s]+");
    private static readonly Regex token = new(@"(?i)(token=)[^&\s]+");

    private static string Redact(string text)
    {
        text = steamId.Replace(text, "<steam id>");
        text = userPath.Replace(text, "$1<user>");
        return token.Replace(text, "$1<hidden>");
    }
}
