# Security

## Reporting a vulnerability

Use this repository's GitHub **Security > Report a vulnerability** to submit a private report. Include the affected revision, platform, impact, and minimal reproduction. Do not put credentials, private models, or exploit details in a public issue. If private reporting is unavailable, open an issue requesting a private contact without disclosing the vulnerability.

Maintainers review reports on a best-effort basis; there is no guaranteed response time or long-term support schedule. Test reports against the latest default branch where practical.

## Trust boundaries

Loaded C# Scripts execute inside Rhino with the current user's permissions. They are not sandboxed. Only open repositories and dependencies you trust.

External agents use their own authentication, configuration, tools, and permissions. They can edit files and run commands according to those settings; some agents do not offer approval prompts. Review the selected agent's mode before sending a prompt.

The optional development mirror can operate the active Rhino document. Keep it bound to loopback and do not expose it to a network. Logs and saved Workspace data may contain file paths, prompts, diagnostics, or project values; sanitize them before sharing.
