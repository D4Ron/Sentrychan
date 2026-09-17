# Sentrychan release packager (Velopack).
# Produces a self-contained Windows installer + delta updates under .\releases.
#
# Prereqs (once):
#   dotnet tool install -g vpk
#
# Usage:
#   .\pack.ps1 1.0.0
#   .\pack.ps1 1.0.1        # any later build; Velopack computes the delta vs the last
#
# The version MUST increase each release — Velopack refuses to pack a version that
# isn't higher than what's already in .\releases, which is what makes auto-update work.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # -Public builds the source-less "RSS viewer" release (no manga/novel sources bundled).
    # Omit it for the beta/personal build, which bundles the source pack and auto-seeds it.
    [switch]$Public
)

$ErrorActionPreference = "Stop"

$IncludeSources = if ($Public) { "false" } else { "true" }
$Flavor = if ($Public) { "PUBLIC (no baked-in sources)" } else { "beta (sources bundled + auto-seeded)" }

$App        = "Sentrychan.App"
$Project    = "$App/$App.csproj"
$Runtime    = "win-x64"
$PublishDir = "publish"
$ReleaseDir = "releases"

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
    "-o", $PublishDir
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 1b. Beta flavor bundles the source pack into publish\sources so it seeds on first run.
#     Public flavor ships nothing here (users import a pack themselves).
if (-not $Public) {
    Write-Host "      Bundling source pack..." -ForegroundColor DarkGray
    & dotnet build "Sentrychan.Sources/Sentrychan.Sources.csproj" -c Release --nologo | Out-Null
    $srcDll = "Sentrychan.Sources/bin/Release/net9.0/Sentrychan.Sources.dll"
    if (Test-Path $srcDll) {
        New-Item -ItemType Directory -Force -Path "$PublishDir/sources" | Out-Null
        Copy-Item $srcDll "$PublishDir/sources/" -Force
        Write-Host "      Bundled -> $PublishDir\sources\Sentrychan.Sources.dll" -ForegroundColor DarkGray
    } else { throw "Source pack DLL not found at $srcDll" }
}

# 2. Hand the published folder to Velopack.
#    --packId   stable identifier - NEVER change it, updates match on this.
#    --mainExe  the WinExe the shortcut launches.
Write-Host "`n[2/3] Packing with Velopack..." -ForegroundColor Yellow

$packArgs = @(
    "pack",
    "--packId", "Sentrychan",
    "--packVersion", $Version,
    "--packDir", $PublishDir,
    "--mainExe", "$App.exe",
    "--packTitle", "Sentrychan",
    "--packAuthors", "D4Ron",
    "--icon", "$App/Assets/icon.ico",
    "--outputDir", $ReleaseDir
)
& vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

# 3. Report what landed.
Write-Host "`n[3/3] Done. Artifacts in .\$ReleaseDir :" -ForegroundColor Green
Get-ChildItem $ReleaseDir | Select-Object Name, @{N='Size';E={"{0:N1} MB" -f ($_.Length/1MB)}} | Format-Table -AutoSize

Write-Host "Upload the ENTIRE .\$ReleaseDir folder to a GitHub Release tagged v$Version." -ForegroundColor Cyan
Write-Host "Users install with Sentrychan-win-Setup.exe; existing users auto-update from RELEASES." -ForegroundColor Cyan
