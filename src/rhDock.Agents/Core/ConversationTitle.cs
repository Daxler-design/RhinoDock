using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace rhDock.Agents;

/// <summary>
/// A short title for a conversation the agent left unnamed, written by the agent's own model once, after the first turn (MonoCode's
/// sessionTitle.ts, MIT). Adapters run <see cref="Prompt"/> as an isolated one-shot call without tools and store the cleaned answer
/// where the agent keeps conversation names, so a stored conversation is never titled again.
/// </summary>
public static partial class ConversationTitle
{
    private const int Limit = 50, MessageLimit = 4000;

    /// <summary>The instruction for the agent's model, with the user's first prompt.</summary>
    public static string Prompt(string message) => $"""
        Generate a title that will help the user recognize this coding session weeks later.
        Title the subject and outcome of the request; ignore instructions about how the agent should work.
        Rules: 3-8 words, fewer than 40 characters, a compact noun phrase or action phrase. Do not copy and truncate the message.
        No quotes, labels, filler, or trailing punctuation. Do not call tools. Reply with the title only.

        User message:
        {(message.Length > MessageLimit ? message[..MessageLimit] : message)}
        """;

    /// <summary>The model's answer as a title: its first line without quotes, labels, or trailing punctuation; null when unusable.</summary>
    public static string? Clean(string? output)
    {
        var line = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        line = Label().Replace(line, "").Trim().Trim('"', '\'', '`', '*', '#', ' ').TrimEnd('.', '!', '?', ':', ';', ',');
        line = Regex.Replace(line, @"\s+", " ");
        if (line.Length == 0) return null;
        return line.Length <= Limit ? line : line[..(Limit - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^(\*\*)?title(\*\*)?\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label();
}
