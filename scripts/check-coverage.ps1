#!/usr/bin/env pwsh
#requires -Version 7

<#
.SYNOPSIS
    Aggregates Cobertura coverage reports and enforces the project's line-coverage floor.

.DESCRIPTION
    The three product test projects each emit their own Cobertura report (via the
    Microsoft.Testing.Platform CodeCoverage collector), and the SAME product assembly is
    exercised by more than one of them - e.g. SmtpGateway.Core is loaded by Core.Tests,
    Infrastructure.Tests AND IntegrationTests, and SmtpGateway.Infrastructure by two of them.

    Because of that overlap the reports must be MERGED at line granularity, not added up:
      * A given source line is counted toward "valid" exactly once per assembly (dedup by
        assembly + file + line number), never once per report - otherwise a shared assembly's
        line total would be counted two or three times.
      * That line is "covered" if ANY report recorded a hit on it (union of hits) - otherwise a
        line covered by a unit test but not by the integration run would be penalised.

    This is the same union/merge that ReportGenerator or `dotnet-coverage merge` would perform;
    it is done here in-process so CI needs no extra tool or package. The final figure is a
    weighted line rate (total covered lines / total valid lines), never an average of per-file
    percentages.

    The scope (which assemblies appear at all) is already constrained upstream by
    tests/coverage.settings.xml, whose ModulePaths Include list admits only the four product
    assemblies. This script asserts all four of those expected assemblies are actually present in the
    merged report set (the list is read from coverage.settings.xml so it stays single-sourced) and
    FAILS loudly if any is missing - otherwise an assembly that silently stopped emitting a coverage
    package would let the gate pass vacuously on a total computed from the rest.

.PARAMETER CoverageDirectory
    Directory searched (recursively) for *.cobertura.xml reports.

.PARAMETER Threshold
    Minimum acceptable total line-coverage percentage. The script exits non-zero below it.

.OUTPUTS
    A per-assembly + total table on stdout. Exit code 0 if total >= Threshold, else 1.
#>
[CmdletBinding()]
param(
    [string]$CoverageDirectory = (Join-Path $PSScriptRoot '..' 'artifacts' 'coverage'),
    [double]$Threshold = 75.0
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CoverageDirectory)) {
    Write-Error "Coverage directory not found: $CoverageDirectory"
    exit 2
}

$reports = @(Get-ChildItem -Path $CoverageDirectory -Filter '*.cobertura.xml' -Recurse -File)
if ($reports.Count -eq 0) {
    Write-Error "No *.cobertura.xml reports found under: $CoverageDirectory"
    exit 2
}

# assembly name -> hashtable( "file|line" -> [bool] covered ), merged across all reports.
$assemblies = [ordered]@{}

foreach ($report in $reports) {
    [xml]$xml = Get-Content -LiteralPath $report.FullName -Raw
    foreach ($package in $xml.coverage.packages.package) {
        $name = [string]$package.name
        if (-not $assemblies.Contains($name)) {
            $assemblies[$name] = @{}
        }
        $lines = $assemblies[$name]
        foreach ($class in $package.classes.class) {
            $fileName = [string]$class.filename
            if ($null -eq $class.lines) { continue }
            foreach ($line in $class.lines.line) {
                if ($null -eq $line) { continue }
                $key = "$fileName|$($line.number)"
                $hit = [int]$line.hits -gt 0
                if ($lines.ContainsKey($key)) {
                    $lines[$key] = $lines[$key] -or $hit
                }
                else {
                    $lines[$key] = $hit
                }
            }
        }
    }
}

# Fail loudly if any expected product assembly dropped out of the merged set. The expected names
# are the four ModulePath includes in tests/coverage.settings.xml (single-sourced there), reduced
# from their ECMAScript regex form (e.g. '.*SmtpGateway\.Core\.dll$') to the bare assembly name the
# cobertura 'package name' uses (e.g. 'SmtpGateway.Core'). Without this check the gate could pass
# vacuously on a total computed from the remaining assemblies if one silently stopped emitting a
# coverage package (a fully-missing test PROJECT is already caught by ci.yml's failing test step,
# but a missing package from a present project is not).
$settingsPath = Join-Path $PSScriptRoot '..' 'tests' 'coverage.settings.xml'
if (-not (Test-Path $settingsPath)) {
    Write-Error "Coverage settings file not found: $settingsPath"
    exit 2
}
[xml]$settingsXml = Get-Content -LiteralPath $settingsPath -Raw
$expectedAssemblies = @(
    $settingsXml.Configuration.CodeCoverage.ModulePaths.Include.ModulePath | ForEach-Object {
        ([string]$_) -replace '^\.\*', '' -replace '\\\.dll\$$', '' -replace '\\\.', '.'
    }
)
if ($expectedAssemblies.Count -eq 0) {
    Write-Error "No <ModulePath> includes found in $settingsPath; cannot verify the expected assembly set."
    exit 2
}

$missing = @($expectedAssemblies | Where-Object { -not $assemblies.Contains($_) })
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host ("FAIL: expected product assembly/assemblies missing from the coverage reports: {0}." -f ($missing -join ', ')) -ForegroundColor Red
    Write-Host ("Found: {0}." -f (($assemblies.Keys | Sort-Object) -join ', ')) -ForegroundColor Red
    Write-Host 'Refusing to compute a passing total from a partial assembly set (see tests/coverage.settings.xml for the expected set).' -ForegroundColor Red
    exit 1
}

$totalValid = 0
$totalCovered = 0
$rows = foreach ($name in ($assemblies.Keys | Sort-Object)) {
    $lines = $assemblies[$name]
    $valid = $lines.Count
    $covered = @($lines.Values | Where-Object { $_ }).Count
    $totalValid += $valid
    $totalCovered += $covered
    [pscustomobject]@{
        Assembly = $name
        Covered  = $covered
        Valid    = $valid
        'Line %' = if ($valid -gt 0) { [math]::Round(100.0 * $covered / $valid, 2) } else { 0 }
    }
}

$totalPct = if ($totalValid -gt 0) { [math]::Round(100.0 * $totalCovered / $totalValid, 2) } else { 0 }

$rows += [pscustomobject]@{
    Assembly = 'TOTAL (weighted)'
    Covered  = $totalCovered
    Valid    = $totalValid
    'Line %' = $totalPct
}

Write-Host ''
Write-Host 'Line coverage (merged across all product test projects, deduplicated per assembly):'
$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($totalPct -lt $Threshold) {
    Write-Host "FAIL: total line coverage $totalPct% is below the $Threshold% floor." -ForegroundColor Red
    exit 1
}

Write-Host "PASS: total line coverage $totalPct% meets the $Threshold% floor." -ForegroundColor Green
exit 0
