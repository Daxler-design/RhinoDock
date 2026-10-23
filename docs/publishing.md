# Publishing a Yak package

The source snapshot and Yak registry are separate publication steps. Only a package owner can upload. These PowerShell commands build locally; upload happens only at `yak push`.

## Build from the public source

Run from a local checkout of this repository on Windows with Rhino 8 installed:

```powershell
$ErrorActionPreference = 'Stop'
$yak = 'C:\Program Files\Rhino 8\System\Yak.exe'

git switch main
if ($LASTEXITCODE -ne 0) { throw 'Git switch failed.' }
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw 'Git pull failed.' }

Push-Location frontend
try {
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency restore failed.' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
} finally { Pop-Location }

dotnet build rhdock.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
dotnet msbuild tools/package-yak.proj /t:Build
if ($LASTEXITCODE -ne 0) { throw 'Yak build failed.' }

$package = (Resolve-Path '.artifacts\yak\rhdock-0.6.10-rh8_31-any.yak').Path
Get-FileHash -Algorithm SHA256 $package
```

The package includes the freshly built frontend, plugin, CLI, and license notices. The manifest uses version 0.6.10 and requires Rhino 8.31 or later. Quit all Rhino instances before replacing an installed copy for a local check.

## Login and upload

Review the version before upload, then run:

```powershell
& $yak search --all rhdock
if ($LASTEXITCODE -ne 0) { throw 'Yak lookup failed.' }

& $yak login
if ($LASTEXITCODE -ne 0) { throw 'Yak login failed.' }

& $yak push $package
if ($LASTEXITCODE -ne 0) { throw 'Yak upload failed.' }

& $yak search --all rhdock
if ($LASTEXITCODE -ne 0) { throw 'Yak lookup failed.' }
```

Login opens Rhino Accounts in the browser. Use the account that owns `rhdock`. A successful push may print no output; the final search should include 0.6.10. Publishing a source snapshot does not automatically create a GitHub Release or upload to Yak.

Yak package versions/distributions cannot be overwritten. If this version is already published, stop and choose a new version in both the project and manifest before rebuilding; do not yank it as a way to replace it.

Official references: [publishing](https://developer.rhino3d.com/guides/yak/pushing-a-package-to-the-server/) and [CLI commands](https://developer.rhino3d.com/guides/yak/yak-cli-reference/).
