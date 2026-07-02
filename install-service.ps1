#Requires -Version 7.0
<#
.SYNOPSIS
    Registers the published SmtpGateway.Service executable as a Windows Service.

.DESCRIPTION
    Registers a Windows Service pointing at the given published executable via New-Service, with
    startup type Automatic. Does NOT configure any service recovery/failure actions (sc.exe
    failure actions) - that is left at Windows defaults by design.

.PARAMETER ExePath
    Path to the published SmtpGateway.Service.exe.

.PARAMETER ServiceName
    Windows service name to register. Defaults to "SmtpGateway".

.EXAMPLE
    ./install-service.ps1 -ExePath C:\SmtpGateway\SmtpGateway.Service.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [string]$ServiceName = "SmtpGateway",

    [string]$DisplayName = "SMTP OAuth Gateway"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "install-service.ps1 must be run from an elevated (Administrator) PowerShell session."
    exit 1
}

$resolvedExePath = Resolve-Path -Path $ExePath -ErrorAction SilentlyContinue
if (-not $resolvedExePath) {
    Write-Error "ExePath '$ExePath' does not exist."
    exit 1
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Error "A service named '$ServiceName' is already registered. Run uninstall-service.ps1 first if you want to re-register it."
    exit 1
}

New-Service -Name $ServiceName `
    -BinaryPathName $resolvedExePath.Path `
    -DisplayName $DisplayName `
    -StartupType Automatic | Out-Null

Write-Host "Service '$ServiceName' registered (StartupType: Automatic). Use start-service.ps1 to start it."
