# Sentrychan release packager (Velopack).
# Produces a self-contained Windows installer + delta updates under .\releases.
#
# Prereqs (once):
#   dotnet tool install -g vpk
#
# Usage:
#   .\pack.ps1 1.0.0
#   .\pack.ps1 1.0.1        # any later build; Velopack computes the delta vs the last
#   .\pack.ps1 1.1.0-preview.1 -Preview -Public   # a preview release (separate app + channel)
#
# The version MUST increase each release — Velopack refuses to pack a version that
# isn't higher than what's already in .\releases, which is what makes auto-update work.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # -Public builds the source-less "RSS viewer" release (no manga/novel sources bundled).
    # Omit it for the beta/personal build, which bundles the source pack and auto-seeds it.
    [switch]$Public,

    # -Preview packs "Sentrychan Preview": its own app id (installs beside Sentrychan, own data
    # folder), the "preview" update channel, and its own output folder so a preview can never
    # land in .\releases. Publish it as a GitHub *pre-release*.
    [switch]$Preview
)

$ErrorActionPreference = "Stop"

$IncludeSources = if ($Public) { "false" } else { "true" }
$Flavor = if ($Public) { "PUBLIC (no baked-in sources)" } else { "beta (sources bundled + auto-seeded)" }
if ($Preview) {
    # Stable picks the newest full release; a preview version has to say it's a pre-release.
    if ($Version -notmatch '-preview\.\d+$') { throw "A preview version looks like 1.1.0-preview.1 (got '$Version')." }
    $Flavor = "PREVIEW, $Flavor"
}
$BuildFlavor = if ($Preview) { "Preview" } else { "Stable" }
$PackId      = if ($Preview) { "SentrychanPreview" } else { "Sentrychan" }
$PackTitle   = if ($Preview) { "Sentrychan Preview" } else { "Sentrychan" }

$App        = "Sentrychan.App"
$Project    = "$App/$App.csproj"
$Runtime    = "win-x64"
$PublishDir = if ($Preview) { "publish-preview" } else { "publish" }
$ReleaseDir = if ($Preview) { "releases-preview" } else { "releases" }

Write-Host "== Sentrychan packager - v$Version - $Flavor ==" -ForegroundColor Cyan

# 1. Clean, self-contained publish. Self-contained = the user needs NO .NET install.
Write-Host "`n[1/3] Publishing $Runtime (self-contained)..." -ForegroundColor Yellow
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

$publishArgs = @(
    "publish", $Project,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:IncludeSources=$IncludeSources",
    "-p:Flavor=$BuildFlavor",
    # The package version, so the app (About, problem reports) can say which release it is.
    "-p:Version=$Version",
    "-o", $PublishDir
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 1b. Beta flavor bundles the source pack into publish\sources so it seeds on first run.
#     Public flavor ships nothing here (users import a pack themselves).
#     The pack is kept outside the repository, at _local\source-pack (git-ignored).
if (-not $Public) {
    Write-Host "      Bundling source pack..." -ForegroundColor DarkGray
    $packProj = Get-ChildItem "_local/source-pack/src/*.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $packProj) { throw "No source pack at _local\source-pack - build with -Public, or restore the pack there." }
    & dotnet build $packProj.FullName -c Release --nologo | Out-Null
    $srcDll = Get-ChildItem "_local/source-pack/src/bin/Release/net9.0/*.Sources.dll" | Select-Object -First 1
    if (-not $srcDll) { throw "Source pack DLL not found after building $($packProj.Name)" }
    New-Item -ItemType Directory -Force -Path "$PublishDir/sources" | Out-Null
    Copy-Item $srcDll.FullName "$PublishDir/sources/" -Force
    Write-Host "      Bundled -> $PublishDir\sources\$($srcDll.Name)" -ForegroundColor DarkGray
}

# 2. Hand the published folder to Velopack.
#    --packId   stable identifier - NEVER change it, updates match on this.
#    --mainExe  the WinExe the shortcut launches.
Write-Host "`n[2/3] Packing with Velopack..." -ForegroundColor Yellow

$packArgs = @(
    "pack",
    "--packId", $PackId,
    "--packVersion", $Version,
    "--packDir", $PublishDir,
    "--mainExe", "$App.exe",
    "--packTitle", $PackTitle,
    "--packAuthors", "D4Ron",
    "--icon", "$App/Assets/icon.ico",
    "--outputDir", $ReleaseDir
)
# Must match VelopackUpdateService.PreviewChannel, or installed previews never see an update.
if ($Preview) { $packArgs += @("--channel", "preview") }
& vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

# 3. Report what landed.
Write-Host "`n[3/3] Done. Artifacts in .\$ReleaseDir :" -ForegroundColor Green
Get-ChildItem $ReleaseDir | Select-Object Name, @{N='Size';E={"{0:N1} MB" -f ($_.Length/1MB)}} | Format-Table -AutoSize

$kind = if ($Preview) { "GitHub PRE-release" } else { "GitHub Release" }
Write-Host "Upload the ENTIRE .\$ReleaseDir folder to a $kind tagged v$Version." -ForegroundColor Cyan
$setup = (Get-ChildItem $ReleaseDir -Filter "*-Setup.exe" | Select-Object -First 1).Name
Write-Host "Users install with $setup; existing users auto-update from the release feed." -ForegroundColor Cyan
