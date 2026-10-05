using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace rhDock.Agents;

/// <summary>One changed file between a checkpoint and the disk, with a bounded unified diff.</summary>
public sealed record FileChange(string Path, string Status, int Added, int Removed, string Diff);

/// <summary>
/// The text sources of a Loaded Repository at one moment, so agent edits can be reviewed and reverted
/// whatever agent made them. Bounded: generated and tool folders, binary files, and large files are skipped.
/// </summary>
public sealed class RepositoryCheckpoint
{
    // ponytail: whole-file copies in memory; switch to git objects or hashes plus disk copies for large repositories.
    private const int MaxFileBytes = 512 * 1024, MaxTotalBytes = 16 * 1024 * 1024, MaxFiles = 5000, MaxDiffLines = 400;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".rhdock", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "dist", ".artifacts" };

    private readonly Dictionary<string, string> files;

    private RepositoryCheckpoint(string root, Dictionary<string, string> files, bool complete)
    {
        Root = root;
        this.files = files;
        Complete = complete;
    }

    public string Root { get; }

    /// <summary>False when a limit stopped the capture; files added after it are then never deleted on restore.</summary>
    public bool Complete { get; }

    public static RepositoryCheckpoint Capture(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var complete = true;
        foreach (var path in Enumerate(root))
        {
            if (files.Count >= MaxFiles || total >= MaxTotalBytes) { complete = false; break; }
            if (Read(path) is not { } text) continue;
            files[Relative(root, path)] = text;
            total += text.Length;
        }
        return new RepositoryCheckpoint(root, files, complete);
    }

    /// <summary>Repository text files, relative and with forward slashes, for mentions.</summary>
    public static IEnumerable<string> List(string root) => Enumerate(root).Select(path => Relative(root, path));

    public IReadOnlyList<FileChange> Compare()
    {
        var changes = new List<FileChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Enumerate(Root))
        {
            var relative = Relative(Root, path);
            seen.Add(relative);
            var known = files.TryGetValue(relative, out var before);
            if (!known && !Complete) continue;
            var after = Read(path);
            if (after is null || after == before) continue;
            changes.Add(Change(relative, known ? "modified" : "added", before ?? "", after));
        }
        foreach (var (relative, before) in files)
            if (!seen.Contains(relative)) changes.Add(Change(relative, "deleted", before, ""));
        return changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Writes one file, or every file, back to its checkpoint state; files added since are deleted.</summary>
    public void Restore(string? relative = null)
    {
        foreach (var change in Compare())
        {
            if (relative is not null && change.Path != relative) continue;
            var path = Path.Combine(Root, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (change.Status == "added") File.Delete(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, files[change.Path], new UTF8Encoding(false));
            }
        }
    }

    private static FileChange Change(string relative, string status, string before, string after)
    {
        // An empty side has no lines; DiffPlex would count one empty line, and the text after a final newline as another.
        static string Body(string text) => text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
        static IEnumerable<DiffPiece> All(string text, ChangeType type) => Body(text).Split('\n').Select(l => new DiffPiece(l.TrimEnd('\r'), type));
        IReadOnlyList<DiffPiece> lines = before.Length == 0 ? All(after, ChangeType.Inserted).ToList()
            : after.Length == 0 ? All(before, ChangeType.Deleted).ToList()
            : InlineDiffBuilder.Diff(Differ.Instance, Body(before), Body(after), ignoreWhiteSpace: false).Lines;
        var added = lines.Count(l => l.Type == ChangeType.Inserted);
        var removed = lines.Count(l => l.Type == ChangeType.Deleted);
        return new FileChange(relative, status, added, removed, Unified(lines));
    }

    // Unified hunks with three lines of context, bounded for the panel.
    private static string Unified(IReadOnlyList<DiffPiece> lines)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Type != ChangeType.Unchanged)
                for (var j = Math.Max(0, i - 3); j <= Math.Min(lines.Count - 1, i + 3); j++) keep[j] = true;
        var text = new StringBuilder();
        var written = 0;
        for (var i = 0; i < lines.Count && written < MaxDiffLines; i++)
        {
            if (!keep[i]) { if (i > 0 && keep[i - 1]) { text.Append("@@\n"); written++; } continue; }
            text.Append(lines[i].Type switch { ChangeType.Inserted => '+', ChangeType.Deleted => '-', _ => ' ' }).Append(lines[i].Text).Append('\n');
            written++;
        }
        if (written >= MaxDiffLines) text.Append("…\n");
        return text.ToString();
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            string[] children, entries;
            try { children = Directory.GetDirectories(folder); entries = Directory.GetFiles(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in entries.OrderBy(f => f, StringComparer.Ordinal)) yield return file;
            foreach (var child in children.OrderByDescending(c => c, StringComparer.Ordinal))
                if (!SkippedFolders.Contains(Path.GetFileName(child))) pending.Push(child);
        }
    }

    // Text only: small files without NUL bytes in their first 8 KB.
    private static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte)0) >= 0) return null;
            return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
