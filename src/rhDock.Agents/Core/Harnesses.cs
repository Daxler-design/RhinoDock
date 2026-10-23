using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace rhDock.Agents;

/// <summary>What an agent supports beyond prompts, so the panel shows only what works.</summary>
/// <param name="Steer">A message can join the running turn.</param>
/// <param name="Rewind">The conversation can go back to before a prompt.</param>
/// <param name="Permissions">The agent asks before edits and commands, so rhDock's permission modes apply. Pi never asks.</param>
/// <param name="AutoMode">The agent's own automatic review mode (a reviewer approves or blocks each action), by the name it uses; null when it has none. It is not Allow all.</param>
/// <param name="Fork">A side chat forks the conversation through the agent's own fork (<see cref="IAgentHarness.ForkAsync"/>). Agents that may
/// not fork keep it false and their side chat tries anyway, falling back to a seeded conversation: ACP agents fork only when they advertise
/// <c>sessionCapabilities.fork</c>, and omp's Pi <c>clone</c> is unverified.</param>
public sealed record HarnessFeatures(bool Steer, bool Rewind, bool Permissions, bool Compact = true, string? AutoMode = null, bool Fork = false);

/// <summary>One agent rhDock can drive: how to find it, how to install it, and its adapter.</summary>
/// <param name="Commands">Executable names, in lookup order.</param>
/// <param name="HelpMarker">Text its <c>--help</c> must contain, for names other tools also use (the fx JSON viewer).</param>
public sealed record HarnessInfo(string Id, string Name, string[] Commands, string Install, string Docs, string? HelpMarker, HarnessFeatures Features,
    Func<string, Action<string>, IAgentHarness> Create, bool Posix = false);

/// <summary>The agents rhDock knows, in MonoCode's order, and which are installed (MonoCode's core/registry.ts, register.ts, and availability.ts).</summary>
public static class Harnesses
{
    // OpenCode allows edits and commands unless told to ask; rhDock's permission mode then decides each request.
    private static readonly Dictionary<string, string> OpenCodeEnvironment = new()
        { ["OPENCODE_CONFIG_CONTENT"] = """{"permission":{"edit":"ask","bash":"ask","webfetch":"ask"}}""" };

    public static readonly IReadOnlyList<HarnessInfo> All =
    [
        new("claude", "Claude Code", ["claude"], "npm install -g @anthropic-ai/claude-code", "https://code.claude.com/docs", null,
            new(Steer: true, Rewind: true, Permissions: true, AutoMode: "Auto", Fork: true), (exe, log) => new ClaudeCode(exe, log)),
        new("codex", "Codex", ["codex"], "npm install -g @openai/codex", "https://developers.openai.com/codex/cli", null,
            new(Steer: true, Rewind: true, Permissions: true, AutoMode: "Auto-review", Fork: true), (exe, log) => new Codex(exe, log)),
        new("cursor", "Cursor", ["cursor-agent"], "curl https://cursor.com/install -fsS | bash", "https://cursor.com/cli", null,
            new(Steer: false, Rewind: false, Permissions: true, Compact: false), (exe, log) => new AcpAgent("Cursor", exe, ["acp"], log)),
        new("grok", "Grok Build", ["grok"], "curl -fsSL https://x.ai/cli/install.sh | bash", "https://x.ai/cli", "agent",
            new(Steer: false, Rewind: false, Permissions: true, Compact: false), (exe, log) => new AcpAgent("Grok Build", exe, ["--no-auto-update", "agent", "--no-leader", "stdio"], log)),
        new("opencode", "OpenCode", ["opencode"], "curl -fsSL https://opencode.ai/install | bash", "https://opencode.ai/v2/docs/", null,
            new(Steer: true, Rewind: true, Permissions: true, Fork: true), (exe, log) => new OpenCode(exe, log, OpenCodeEnvironment)),
        new("pi", "Pi", ["pi-coding-agent", "pi"], "npm install -g @earendil-works/pi-coding-agent", "https://pi.dev", null,
            new(Steer: true, Rewind: false, Permissions: false, Fork: true), (exe, log) => new Pi("Pi", exe, log)),
        new("omp", "omp", ["omp"], "curl -fsSL https://omp.sh/install | sh", "https://omp.sh", "rpc",
            new(Steer: true, Rewind: false, Permissions: false), (exe, log) => new Pi("omp", exe, log)),
        new("fx", "fx", ["fx"], "curl -fsSL https://fx.sh/setup.sh | bash", "https://fx.sh", "acp",
            new(Steer: false, Rewind: false, Permissions: true, Compact: false), (exe, log) => new AcpAgent("fx", exe, ["acp"], log)),
        new("hermes", "Hermes Agent", ["hermes"], "Install from hermes-agent.nousresearch.com, then run hermes model", "https://hermes-agent.nousresearch.com", null,
            new(Steer: false, Rewind: false, Permissions: true, Compact: false), (exe, log) => new AcpAgent("Hermes Agent", exe, ["acp"], log)),
        new("antigravity", "Antigravity", ["agy_acp_server.par"], "Install Antigravity, then run agy once in a terminal", "https://antigravity.google", null,
            new(Steer: false, Rewind: false, Permissions: true, Compact: false), (exe, log) => new AcpAgent("Antigravity", exe, [], log), Posix: true),
    ];

    public const string DefaultId = "opencode";

    public static HarnessInfo Get(string? id) => All.FirstOrDefault(h => h.Id == id) ?? All.First(h => h.Id == DefaultId);

    private static readonly ConcurrentDictionary<string, bool> Verified = new();

    /// <summary>The executable to run, or null when it is not installed. A user-set path wins and is never second-guessed.</summary>
    public static string? Find(HarnessInfo harness, string? userPath, string path)
    {
        if (harness.Posix && OperatingSystem.IsWindows()) return null;
        if (!string.IsNullOrWhiteSpace(userPath)) return File.Exists(userPath) ? userPath : null;
        foreach (var command in harness.Commands)
            if (AgentProcess.Find(command, null, path) is { } found && (harness.HelpMarker is not { } marker || Verified.GetOrAdd(found, f => AgentProcess.Capture(f, ["--help"], Path.GetTempPath(), TimeSpan.FromSeconds(5), path).Contains(marker, StringComparison.OrdinalIgnoreCase))))
                return found;
        return null;
    }

    /// <summary>Which agents are installed, by id. Lookup only checks files; the fx and Grok checks run <c>--help</c> once per path.</summary>
    public static Dictionary<string, string?> Installed(Func<string, string?> userPath)
    {
        var path = AgentProcess.ExtendedPath();
        return All.ToDictionary(h => h.Id, h => Find(h, userPath(h.Id), path));
    }

    /// <summary>The version an executable reports, such as 2.1.277, or null.</summary>
    public static string? Version(string executable) =>
        Regex.Match(AgentProcess.Capture(executable, ["--version"], Path.GetTempPath(), TimeSpan.FromSeconds(5)), @"\d+\.\d+\.\d+[\w.+-]*") is { Success: true } m ? m.Value : null;
}
