#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the self-contained single-file Windows x64 release ZIP for SmtpGateway.

.DESCRIPTION
    Publishes SmtpGateway.Service and SmtpGateway.Admin.Tui as self-contained, single-file
    win-x64 executables (IncludeNativeLibrariesForSelfExtract=true so native dependencies such as
    Microsoft.Data.Sqlite's e_sqlite3.dll are extracted alongside the exe at first run instead of
    requiring a separate file next to it). These publish flags are passed on the command line only
    - the .csproj files are not modified - so a normal 'dotnet build'/'dotnet test' is unaffected.

    Also generates a CycloneDX SBOM (sbom.json) for the solution via the pinned local dotnet tool
    (see .config/dotnet-tools.json), and a THIRD-PARTY-NOTICES.txt listing every direct NuGet
    dependency of src/*.csproj (name, resolved version from Directory.Packages.props, and license
    id/URL taken from that same generated SBOM) - both derived from the actual dependency graph at
    build time rather than hand-maintained.

    Assembles the published outputs together with the install/uninstall/start/stop scripts, a
    sample appsettings.json, LICENSE, sbom.json, and THIRD-PARTY-NOTICES.txt into a staging
    directory, zips it into '<OutputDirectory>/SmtpGateway-<Version>-win-x64.zip', and writes a
    '<zip>.sha256' checksum file next to it in standard 'HASH  filename' format.

.PARAMETER Version
    Release version to embed in the output ZIP file name, e.g. "1.2.3". Defaults to "0.0.0-dev"
    for local/dry-run builds.

.PARAMETER OutputDirectory
    Directory the ZIP and its checksum file are written to (relative paths are resolved against
    the repo root). Defaults to "artifacts".

.EXAMPLE
    ./build-release.ps1 -Version 1.2.3
#>
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-dev",
    [string]$OutputDirectory = "artifacts"
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$outputDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
$publishDir = Join-Path $outputDir "publish"
$stagingDir = Join-Path $outputDir "staging"
$zipName = "SmtpGateway-$Version-win-x64.zip"
$zipPath = Join-Path $outputDir $zipName
$checksumPath = "$zipPath.sha256"

Write-Host "== SmtpGateway release build ($Version) =="

# --- Clean/create working directories -------------------------------------------------------
foreach ($dir in @($publishDir, $stagingDir)) {
    if (Test-Path $dir) {
        Remove-Item -Path $dir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}
if (Test-Path $checksumPath) {
    Remove-Item -Path $checksumPath -Force
}

# --- Publish service and TUI as self-contained single-file win-x64 executables ---------------
$publishTargets = @(
    @{ Project = Join-Path $repoRoot "src/SmtpGateway.Service/SmtpGateway.Service.csproj"; SubFolder = "service"; ExeName = "SmtpGateway.Service.exe" },
    @{ Project = Join-Path $repoRoot "src/SmtpGateway.Admin.Tui/SmtpGateway.Admin.Tui.csproj"; SubFolder = "tui"; ExeName = "SmtpGateway.Admin.Tui.exe" }
)

foreach ($target in $publishTargets) {
    $publishOut = Join-Path $publishDir $target.SubFolder
    Write-Host "-- Publishing $($target.Project) -> $publishOut"

    dotnet publish $target.Project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishOut

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($target.Project) (exit code $LASTEXITCODE)"
    }

    $exePath = Join-Path $publishOut $target.ExeName
    if (-not (Test-Path $exePath)) {
        throw "Expected published executable not found: $exePath"
    }

    $stagingSubDir = Join-Path $stagingDir $target.SubFolder
    New-Item -ItemType Directory -Path $stagingSubDir -Force | Out-Null

    # PublishSingleFile bundles the managed dependencies into the exe itself. The publish output
    # folder also contains appsettings.Development.json (dev-only, unused by the service) and
    # .pdb debug symbol files - neither is needed to run the release build, so only the exe is
    # staged.
    Copy-Item -Path $exePath -Destination $stagingSubDir -Force
}

# --- Copy scripts, sample config, license, SBOM/notices --------------------------------------

# The service resolves its content root (and therefore appsettings.json) from its own directory,
# so the sample config must sit next to SmtpGateway.Service.exe, not at the ZIP root.
Copy-Item -Path (Join-Path $repoRoot "src/SmtpGateway.Service/appsettings.json") -Destination (Join-Path $stagingDir "service") -Force

$filesToCopy = @(
    "install-service.ps1",
    "uninstall-service.ps1",
    "start-service.ps1",
    "stop-service.ps1",
    "LICENSE"
)
foreach ($file in $filesToCopy) {
    Copy-Item -Path (Join-Path $repoRoot $file) -Destination $stagingDir -Force
}

# --- Generate SBOM (CycloneDX) -----------------------------------------------------------------
# Uses the pinned local tool from .config/dotnet-tools.json (installed via
# 'dotnet tool install --tool-manifest .config/dotnet-tools.json CycloneDX --version <pinned>').
# 'dotnet tool restore' is idempotent/fast when already restored, so it's safe to call every run.
Write-Host "-- Restoring local dotnet tools"
dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool restore failed (exit code $LASTEXITCODE)"
}

$sbomFileName = "sbom.json"
Write-Host "-- Generating SBOM -> $(Join-Path $stagingDir $sbomFileName)"
dotnet tool run dotnet-CycloneDX -- (Join-Path $repoRoot "SmtpGateway.slnx") `
    -o $stagingDir `
    -F Json `
    -fn $sbomFileName `
    -c Release `
    --set-name "SmtpGateway" `
    --set-version $Version

if ($LASTEXITCODE -ne 0) {
    throw "CycloneDX SBOM generation failed (exit code $LASTEXITCODE)"
}
$sbomPath = Join-Path $stagingDir $sbomFileName
if (-not (Test-Path $sbomPath)) {
    throw "Expected SBOM file not found: $sbomPath"
}

# --- Generate THIRD-PARTY-NOTICES.txt ------------------------------------------------------------
# Derived from the actual dependency set rather than a hand-maintained list, so it can't drift
# from reality:
#   1. Direct package names come from parsing the <PackageReference Include="..."> elements that
#      actually appear in src/*.csproj (production code only - test-only packages are excluded).
#   2. Resolved versions come from Directory.Packages.props, the single authoritative source of
#      truth under Central Package Management.
#   3. License id/URL for each package comes from the SBOM generated above, which CycloneDX
#      populates from the package's own NuGet metadata (license expression).
Write-Host "-- Generating THIRD-PARTY-NOTICES.txt"

$directPackageNames = Get-ChildItem -Path (Join-Path $repoRoot "src") -Filter "*.csproj" -Recurse |
    ForEach-Object { Get-Content -Path $_.FullName -Raw } |
    ForEach-Object { [regex]::Matches($_, '<PackageReference\s+Include="([^"]+)"') } |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

[xml]$packageVersionsXml = Get-Content -Path (Join-Path $repoRoot "Directory.Packages.props") -Raw
$packageVersions = @{}
foreach ($node in $packageVersionsXml.Project.ItemGroup.PackageVersion) {
    if ($null -ne $node.Include) {
        $packageVersions[$node.Include] = $node.Version
    }
}

$sbomComponentsByKey = @{}
$sbomJson = Get-Content -Path $sbomPath -Raw | ConvertFrom-Json
foreach ($component in $sbomJson.components) {
    $sbomComponentsByKey["$($component.name)@$($component.version)"] = $component
}

$noticeLines = New-Object System.Collections.Generic.List[string]
$noticeLines.Add("Third-Party Notices for SmtpGateway $Version")
$noticeLines.Add("=" * 60)
$noticeLines.Add("")
$noticeLines.Add("This release bundles the following direct NuGet dependencies of the")
$noticeLines.Add("SmtpGateway source projects (src/*.csproj). Versions and license data are")
$noticeLines.Add("generated at release-build time from Directory.Packages.props and each")
$noticeLines.Add("package's own NuGet metadata (see sbom.json in this archive for the full")
$noticeLines.Add("bill of materials, including transitive dependencies).")
$noticeLines.Add("")

foreach ($packageName in $directPackageNames) {
    $version = $packageVersions[$packageName]
    if (-not $version) {
        throw "No <PackageVersion> entry found in Directory.Packages.props for direct dependency '$packageName'"
    }

    $component = $sbomComponentsByKey["$packageName@$version"]
    $licenseText = "UNKNOWN"
    $urlText = $null
    if ($component) {
        if ($component.licenses -and $component.licenses.Count -gt 0) {
            $license = $component.licenses[0].license
            if ($license.id) {
                $licenseText = $license.id
            }
            elseif ($license.name) {
                $licenseText = $license.name
            }
            if ($license.url) {
                $urlText = $license.url
            }
        }
        if (-not $urlText -and $component.externalReferences) {
            $website = $component.externalReferences | Where-Object { $_.type -eq "website" } | Select-Object -First 1
            $vcs = $component.externalReferences | Where-Object { $_.type -eq "vcs" } | Select-Object -First 1
            $urlText = if ($website) { $website.url } elseif ($vcs) { $vcs.url } else { $null }
        }
    }
    else {
        Write-Warning "No SBOM component found for $packageName@$version; license will be recorded as UNKNOWN."
    }

    $line = "$packageName $version - $licenseText"
    if ($urlText) {
        $line += " ($urlText)"
    }
    $noticeLines.Add($line)
}

$noticesPath = Join-Path $stagingDir "THIRD-PARTY-NOTICES.txt"
Set-Content -Path $noticesPath -Value $noticeLines

# --- Zip the staging directory -----------------------------------------------------------------
Write-Host "-- Creating $zipPath"
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

# --- Checksum the ZIP ---------------------------------------------------------------------------
$zipHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksumPath -Value "$zipHash  $zipName" -NoNewline:$false

Write-Host ""
Write-Host "== Release build complete =="
Write-Host "ZIP:      $zipPath"
Write-Host "Checksum: $checksumPath"
Write-Host "SHA256:   $zipHash"
