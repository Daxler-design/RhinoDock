# Contributing to RhinoDock

Bug reports, focused fixes, documentation improvements, and reproducible examples are welcome. Use GitHub Issues for bugs and proposals, and pull requests for changes. Follow [SECURITY.md](SECURITY.md) for vulnerabilities.

## Before changing code

Read [development](docs/development.md) and [architecture](docs/architecture.md). Inspect the affected code and its callers. Discuss substantial behavior or architecture changes in an issue first; small fixes can go straight to a pull request.

Keep each change focused, match the surrounding style, and reuse existing code or platform features before adding a dependency. All committed documentation, comments, UI strings, tests, and commit messages must be in English.

## Checks

Run the applicable commands in the development guide. For a bug, add a check that reproduces it before fixing it. Keep tests at the public behavior boundary. Documentation changes need link and consistency checks, not a runtime rebuild.

A pull request should state the problem, resulting behavior, checks run, and any remaining limitations. Include Rhino version and operating system for live checks. Compilation and browser previews do not prove Rhino interaction works.

Do not commit build output, local agent settings, credentials, conversation logs, or `.rhdock/` data. Use small, sanitized fixtures that you have permission to share.

Update the user guide when behavior changes. Contributions are provided under the repository's MIT license; preserve notices for any third-party code.
